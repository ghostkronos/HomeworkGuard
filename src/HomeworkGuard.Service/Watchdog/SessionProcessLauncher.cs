using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomeworkGuard.Service.Watchdog;

/// <summary>
/// Launches HomeworkGuard.UI.exe into the active interactive user desktop session (Session 1+)
/// from Session 0 LocalSystem service, and delivers pre-lock desktop notifications via WTSSendMessage.
/// </summary>
public static class SessionProcessLauncher
{
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_TOPMOST = 0x00040000;

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionID;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessage(
        IntPtr hServer,
        uint sessionId,
        string pTitle,
        int titleLength,
        string pMessage,
        int messageLength,
        uint style,
        int timeout,
        out int pResponse,
        bool bWait);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool IsUiRunningInInteractiveSession(string processNameWithoutExt)
    {
        var procs = Process.GetProcessesByName(processNameWithoutExt);
        try
        {
            return procs.Any(p => p.SessionId > 0);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static uint FindActiveUserSessionId()
    {
        uint consoleSession = WTSGetActiveConsoleSessionId();
        if (consoleSession != 0xFFFFFFFF && consoleSession != 0)
        {
            return consoleSession;
        }

        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr pSessions, out int count))
        {
            try
            {
                int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessions + (i * structSize));
                    if (info.State == WTS_CONNECTSTATE_CLASS.WTSActive && info.SessionID > 0)
                    {
                        return info.SessionID;
                    }
                }
            }
            finally
            {
                WTSFreeMemory(pSessions);
            }
        }

        return consoleSession;
    }

    public static bool TryLaunchInActiveUserSession(string exePath, ILogger logger)
    {
        if (!File.Exists(exePath))
        {
            logger.LogError("UI çalıştırılabilir dosyası bulunamadı: {Path}", exePath);
            return false;
        }

        uint sessionId = FindActiveUserSessionId();
        if (sessionId == 0xFFFFFFFF || sessionId == 0)
        {
            logger.LogWarning("Aktif kullanıcı oturumu bulunamadı (Session={SessionId}).", sessionId);
            return false;
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                int err = Marshal.GetLastWin32Error();
                logger.LogWarning("WTSQueryUserToken başarısız (Session={SessionId}, Win32={Err})", sessionId, err);
                return false;
            }

            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(
                    userToken,
                    MAXIMUM_ALLOWED | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY,
                    ref sa,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            }

            if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
            {
                envBlock = IntPtr.Zero;
            }

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            string workingDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            uint flags = envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0;

            if (!CreateProcessAsUser(
                    primaryToken,
                    exePath,
                    $"\"{exePath}\"",
                    ref sa,
                    ref sa,
                    false,
                    flags,
                    envBlock,
                    workingDir,
                    ref si,
                    out PROCESS_INFORMATION pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            logger.LogInformation("HomeworkGuard.UI kullanıcı oturumunda başlatıldı (Session={SessionId}, PID={Pid}).", sessionId, pi.dwProcessId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UI aktif oturumda başlatılamadı (Session={SessionId}).", sessionId);
            return false;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    public static void SendDesktopNotification(string title, string message, ILogger logger)
    {
        uint sessionId = FindActiveUserSessionId();
        if (sessionId == 0xFFFFFFFF || sessionId == 0) return;

        try
        {
            WTSSendMessage(
                IntPtr.Zero,
                sessionId,
                title,
                title.Length * 2,
                message,
                message.Length * 2,
                MB_OK | MB_ICONWARNING | MB_TOPMOST,
                30,
                out _,
                false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Masaüstü bildirimi gönderilemedi.");
        }
    }
}
