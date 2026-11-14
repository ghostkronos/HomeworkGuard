namespace HomeworkGuard.Core;

/// <summary>
/// Global build-time safety configuration for HomeworkGuard.
/// Host PC builds keep IS_DEV_SANDBOX_MODE = true so fullscreen kiosk lock,
/// low-level keyboard hook, and aggressive watchdog restarts never trigger on the developer machine.
/// Release VM builds pass /p:ProductionKiosk=true to set IS_DEV_SANDBOX_MODE = false.
/// </summary>
public static class AppBuildConfig
{
#if PRODUCTION_KIOSK
    public static readonly bool IS_DEV_SANDBOX_MODE = false;
#else
    public static readonly bool IS_DEV_SANDBOX_MODE = true;
#endif

    public const string ServiceName = "HomeworkGuardService";
    public const string PipeName = "HomeworkGuard_IPC_v1";
    public const string GeminiModelName = "gemini-3.5-flash-lite";

    public static string GetDataDirectory()
    {
        if (IS_DEV_SANDBOX_MODE)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "HomeworkGuard", "DevSandbox");
        }

        return @"C:\ProgramData\HomeworkGuard";
    }

    public static string GetDatabasePath() => Path.Combine(GetDataDirectory(), "homeworkguard.db");
    public static string GetSubmissionsDirectory() => Path.Combine(GetDataDirectory(), "Submissions");
    public static string GetLogsDirectory() => Path.Combine(GetDataDirectory(), "Logs");
}
