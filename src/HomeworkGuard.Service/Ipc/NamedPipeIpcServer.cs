using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Service.Engine;

namespace HomeworkGuard.Service.Ipc;

/// <summary>
/// Named Pipe server allowing the UI process (running in standard user session)
/// to securely exchange commands with the LocalSystem service.
/// </summary>
public sealed class NamedPipeIpcServer
{
    private readonly HomeworkGuardCoordinator _coordinator;
    private readonly ILogger<NamedPipeIpcServer> _logger;

    public NamedPipeIpcServer(HomeworkGuardCoordinator coordinator, ILogger<NamedPipeIpcServer> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Named Pipe sunucusu dinliyor: {PipeName}", AppBuildConfig.PipeName);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(ct);

                var connectedPipe = pipe;
                pipe = null; // Ownership transferred to client task
                _ = Task.Run(() => HandleClientAsync(connectedPipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger.LogError(ex, "Named Pipe bağlantı kabul hatası.");
                await Task.Delay(250, ct);
            }
        }
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        var pipeSecurity = new PipeSecurity();

        if (AppBuildConfig.IS_DEV_SANDBOX_MODE)
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }
        }
        else
        {
            // Production: LocalSystem & Admins have FullControl; standard AuthenticatedUsers can ReadWrite IPC messages
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            AppBuildConfig.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };

                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) return;

                IpcRequest? request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
                IpcResponse response = request is null
                    ? IpcResponse.Fail(string.Empty, "Geçersiz IPC isteği.")
                    : await _coordinator.HandleIpcRequestAsync(request, ct);

                string responseJson = JsonSerializer.Serialize(response, IpcJson.Options);
                await writer.WriteLineAsync(responseJson.AsMemory(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "IPC istemci bağlantısı sonlandı.");
            }
        }
    }
}
