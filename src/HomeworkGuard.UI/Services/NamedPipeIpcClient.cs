using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;

namespace HomeworkGuard.UI.Services;

/// <summary>
/// Named Pipe IPC client communicating with HomeworkGuard.Service.
/// No secrets or credentials are stored in the UI process.
/// </summary>
public sealed class NamedPipeIpcClient
{
    public async Task<IpcResponse> SendAsync(string command, object? payload = null, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var request = new IpcRequest
        {
            Command = command,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, IpcJson.Options)
        };

        try
        {
            using var pipe = new NamedPipeClientStream(".", AppBuildConfig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Math.Min(timeoutMs, 3000), ct);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            string reqJson = JsonSerializer.Serialize(request, IpcJson.Options);
            await writer.WriteLineAsync(reqJson.AsMemory(), ct);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(timeoutMs);

            string? respLine = await reader.ReadLineAsync(readCts.Token);
            if (string.IsNullOrWhiteSpace(respLine))
            {
                return IpcResponse.Fail(request.CorrelationId, "Servisten boş yanıt alındı.", "EMPTY_RESPONSE");
            }

            return JsonSerializer.Deserialize<IpcResponse>(respLine, IpcJson.Options)
                ?? IpcResponse.Fail(request.CorrelationId, "Servis yanıtı çözümlenemedi.", "INVALID_RESPONSE");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail(request.CorrelationId, "HomeworkGuard servisine bağlanılamadı (zaman aşımı).", "SERVICE_TIMEOUT");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IpcResponse.Fail(request.CorrelationId, $"Servis bağlantısı kurulamadı: {ex.Message}", "SERVICE_UNAVAILABLE");
        }
    }

    public static T? Unpack<T>(IpcResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.PayloadJson)) return default;
        return JsonSerializer.Deserialize<T>(response.PayloadJson, IpcJson.Options);
    }
}
