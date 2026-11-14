using HomeworkGuard.Core;
using HomeworkGuard.Core.Models;
using HomeworkGuard.Service.Engine;
using HomeworkGuard.Service.Ipc;
using HomeworkGuard.Service.Security;
using HomeworkGuard.Service.Telegram;
using HomeworkGuard.Service.Time;

namespace HomeworkGuard.Service.Watchdog;

/// <summary>
/// Main 500ms watchdog loop: syncs NTP time, enforces lock screen availability in active user session
/// when state is NightLock or HomeworkRequired (unless in MaintenanceMode or DevSandboxMode),
/// and dispatches the 21:50 pre-lock warning notification.
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    private readonly TrustedTimeService _timeService;
    private readonly HomeworkGuardCoordinator _coordinator;
    private readonly NamedPipeIpcServer _ipcServer;
    private readonly TelegramAdminBotService _telegram;
    private readonly TamperProtectionService _tamperProtection;
    private readonly ILogger<WatchdogWorker> _logger;

    private string _lastWarningSentDate = string.Empty;
    private DateTime _lastUiLaunchAttemptUtc = DateTime.MinValue;
    private DateTime _lastTamperCheckUtc = DateTime.MinValue;
    private long _lastUsageSampleTickMs = Environment.TickCount64;
    private double _accumulatedUnlockedSeconds;
    private bool _startupNotified;
    private LockState? _lastObservedState;

    public WatchdogWorker(
        TrustedTimeService timeService,
        HomeworkGuardCoordinator coordinator,
        NamedPipeIpcServer ipcServer,
        TelegramAdminBotService telegram,
        TamperProtectionService tamperProtection,
        ILogger<WatchdogWorker> logger)
    {
        _timeService = timeService;
        _coordinator = coordinator;
        _ipcServer = ipcServer;
        _telegram = telegram;
        _tamperProtection = tamperProtection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HomeworkGuard Service başlatıldı. IS_DEV_SANDBOX_MODE={Sandbox}", AppBuildConfig.IS_DEV_SANDBOX_MODE);

        if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && !_coordinator.IsMaintenanceMode)
        {
            _tamperProtection.EnforceProtection();
        }

        string uiExePath = ResolveUiExecutablePath();

        // Enforce immediate UI launch at boot before waiting on network/NTP sync
        if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && !_coordinator.IsMaintenanceMode)
        {
            var bootStatus = _coordinator.GetCurrentStatus();
            if (bootStatus.CurrentState is LockState.NightLock or LockState.HomeworkRequired &&
                !SessionProcessLauncher.IsUiRunningInInteractiveSession("HomeworkGuard.UI"))
            {
                SessionProcessLauncher.TryLaunchInActiveUserSession(uiExePath, _logger);
            }
        }

        // Initial NTP sync & Telegram connection on startup
        await _timeService.SyncAsync(stoppingToken);
        await _telegram.EnsureRunningAsync(stoppingToken);

        // Start Named Pipe server in parallel
        var ipcTask = Task.Run(() => _ipcServer.RunAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_timeService.ShouldRefreshNtp())
                {
                    await _timeService.SyncAsync(stoppingToken);
                }

                await _telegram.EnsureRunningAsync(stoppingToken);

                var status = _coordinator.GetCurrentStatus();

                if (!_startupNotified && _telegram.IsReady)
                {
                    _startupNotified = true;
                    _coordinator.NotifyTelegram($"🟢 Bilgisayar / HomeworkGuard Servisi aktif.\nGün: {status.EffectiveDayName} | Durum: {status.CurrentState}\nSaat: {status.EffectiveLocalNow:HH:mm}");
                }

                if (_lastObservedState == LockState.UnlockedFreeTime && status.CurrentState == LockState.NightLock)
                {
                    _coordinator.NotifyTelegram($"⏰ {status.StatusMessage}");
                }
                _lastObservedState = status.CurrentState;

                // Track active unlocked usage seconds using monotonic clock and flush every 10s
                long nowTickMs = Environment.TickCount64;
                double deltaSec = Math.Max(0, (nowTickMs - _lastUsageSampleTickMs) / 1000.0);
                _lastUsageSampleTickMs = nowTickMs;

                if (status.CurrentState == LockState.UnlockedFreeTime && status.DailyMaxUsageMinutes > 0)
                {
                    _accumulatedUnlockedSeconds += Math.Min(deltaSec, 5.0);
                    if (_accumulatedUnlockedSeconds >= 10.0)
                    {
                        int wholeSeconds = (int)_accumulatedUnlockedSeconds;
                        _accumulatedUnlockedSeconds -= wholeSeconds;
                        _coordinator.RecordUnlockedElapsedSeconds(wholeSeconds);
                    }
                }
                else
                {
                    _accumulatedUnlockedSeconds = 0;
                }

                // Pre-lock warning notification at 21:50 (once per day)
                if (status.CurrentState == LockState.UnlockedFreeTime && status.ShowPreLockWarning)
                {
                    string todayKey = status.EffectiveLocalNow.ToString("yyyy-MM-dd");
                    if (_lastWarningSentDate != todayKey)
                    {
                        _lastWarningSentDate = todayKey;
                        _logger.LogInformation("21:50 kilit uyarısı gönderiliyor.");
                        if (!AppBuildConfig.IS_DEV_SANDBOX_MODE)
                        {
                            SessionProcessLauncher.SendDesktopNotification(
                                "HomeworkGuard Uyarı",
                                $"Saat {status.EndHour:D2}:{status.EndMinute:D2}'de bilgisayar kilitlenecek, çalışmalarını kaydet!",
                                _logger);
                        }
                    }
                }

                // Watchdog UI enforcement (ONLY when IS_DEV_SANDBOX_MODE == false!)
                if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && !_coordinator.IsMaintenanceMode)
                {
                    if ((DateTime.UtcNow - _lastTamperCheckUtc) > TimeSpan.FromSeconds(10))
                    {
                        _lastTamperCheckUtc = DateTime.UtcNow;
                        _tamperProtection.PeriodicVerify();
                    }

                    if (status.CurrentState == LockState.NightLock || status.CurrentState == LockState.HomeworkRequired)
                    {
                        if (!SessionProcessLauncher.IsUiRunningInInteractiveSession("HomeworkGuard.UI"))
                        {
                            if ((DateTime.UtcNow - _lastUiLaunchAttemptUtc) > TimeSpan.FromMilliseconds(450))
                            {
                                _lastUiLaunchAttemptUtc = DateTime.UtcNow;
                                SessionProcessLauncher.TryLaunchInActiveUserSession(uiExePath, _logger);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchdog döngüsünde beklenmeyen hata.");
            }

            await Task.Delay(500, stoppingToken);
        }

        await ipcTask;
    }

    private static string ResolveUiExecutablePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidateSameDir = Path.Combine(baseDir, "HomeworkGuard.UI.exe");
        if (File.Exists(candidateSameDir)) return candidateSameDir;

        return Path.Combine(baseDir, "UI", "HomeworkGuard.UI.exe");
    }
}
