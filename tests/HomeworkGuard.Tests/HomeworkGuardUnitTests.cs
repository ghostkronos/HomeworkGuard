using System.Text.Json;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Models;
using HomeworkGuard.Core.Security;
using HomeworkGuard.Service.AI;
using HomeworkGuard.Service.Engine;
using HomeworkGuard.Service.Security;
using HomeworkGuard.Service.Storage;
using HomeworkGuard.Service.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeworkGuard.Tests;

public class HomeworkGuardUnitTests : IDisposable
{
    private readonly string _tempDir;

    public HomeworkGuardUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HomeworkGuard_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void DevSandboxMode_IsTrueByDefault()
    {
        Assert.True(AppBuildConfig.IS_DEV_SANDBOX_MODE);
    }

    [Fact]
    public void CryptoHelper_Pbkdf2_HashesWithRandomSaltAndVerifies()
    {
        string h1 = CryptoHelper.HashSecret("SuperSecret#2026");
        string h2 = CryptoHelper.HashSecret("SuperSecret#2026");

        Assert.NotEqual(h1, h2);
        Assert.StartsWith("PBKDF2$SHA256$", h1);
        Assert.True(CryptoHelper.VerifySecret("SuperSecret#2026", h1));
        Assert.False(CryptoHelper.VerifySecret("WrongPassword", h1));
    }

    [Fact]
    public void CryptoHelper_MasterRecoveryKey_Is24CharsAndNormalizes()
    {
        string key = CryptoHelper.GenerateMasterRecoveryKey();
        Assert.StartsWith("HG-", key);
        string rawChars = key.Replace("HG-", "").Replace("-", "");
        Assert.Equal(24, rawChars.Length);

        string lowerNoDash = rawChars.ToLowerInvariant();
        Assert.Equal(key, CryptoHelper.NormalizeRecoveryKey(lowerNoDash));
    }

    [Theory]
    [InlineData(22, 0, true, LockState.NightLock, false)]
    [InlineData(23, 30, true, LockState.NightLock, false)]
    [InlineData(0, 15, false, LockState.NightLock, false)]
    [InlineData(9, 59, true, LockState.NightLock, false)]
    [InlineData(10, 0, false, LockState.HomeworkRequired, false)]
    [InlineData(15, 30, false, LockState.HomeworkRequired, false)]
    [InlineData(15, 30, true, LockState.UnlockedFreeTime, false)]
    [InlineData(21, 49, true, LockState.UnlockedFreeTime, false)]
    [InlineData(21, 50, true, LockState.UnlockedFreeTime, true)]
    [InlineData(21, 59, true, LockState.UnlockedFreeTime, true)]
    public void StateMachine_EvaluatesTimeRulesCorrectly(
        int hour, int minute, bool approved, LockState expectedState, bool expectedWarning)
    {
        var rules = new HomeworkRuleConfig { StartHour = 10, StartMinute = 0, EndHour = 22, EndMinute = 0, WarningMinutesBeforeLock = 10 };
        var localNow = new DateTime(2026, 11, 13, hour, minute, 0, DateTimeKind.Local);

        var (state, showWarning, _, _) = TrustedTimeService.EvaluateState(
            localNow, rules, approved, isMaintenanceMode: false, isInternetAvailable: true);

        Assert.Equal(expectedState, state);
        Assert.Equal(expectedWarning, showWarning);
    }

    [Fact]
    public void StateMachine_FailSecure_ShowsOfflineMessage()
    {
        var rules = new HomeworkRuleConfig();
        var localNow = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Local);

        var (state, _, _, msg) = TrustedTimeService.EvaluateState(
            localNow, rules, isTodayApproved: false, isMaintenanceMode: false, isInternetAvailable: false);

        Assert.Equal(LockState.HomeworkRequired, state);
        Assert.Contains("internet bağlantısı bekleniyor", msg);
    }

    [Fact]
    public void StateMachine_EnforcesDailyMaxUsageMinutesQuota()
    {
        var rules = new HomeworkRuleConfig
        {
            StartHour = 10,
            StartMinute = 0,
            EndHour = 22,
            EndMinute = 0,
            WarningMinutesBeforeLock = 10,
            DailyMaxUsageMinutes = 120
        };
        var localNow = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Local);

        // 60 min used out of 120 -> unlocked, no warning
        var (s1, w1, _, _) = TrustedTimeService.EvaluateState(localNow, rules, true, false, true, usedSecondsToday: 60 * 60);
        Assert.Equal(LockState.UnlockedFreeTime, s1);
        Assert.False(w1);

        // 115 min used out of 120 -> unlocked, pre-lock warning active (5 min left <= 10)
        var (s2, w2, _, msg2) = TrustedTimeService.EvaluateState(localNow, rules, true, false, true, usedSecondsToday: 115 * 60);
        Assert.Equal(LockState.UnlockedFreeTime, s2);
        Assert.True(w2);
        Assert.Contains("5 dakika", msg2);

        // 120 min used -> locked
        var (s3, _, _, msg3) = TrustedTimeService.EvaluateState(localNow, rules, true, false, true, usedSecondsToday: 120 * 60);
        Assert.Equal(LockState.NightLock, s3);
        Assert.Contains("120 dakikalık", msg3);
    }

    [Fact]
    public void DatabaseService_PersistsEncryptedSecretsAndDailyApprovals()
    {
        var db = new DatabaseService(_tempDir);
        db.SetGeminiApiKey("AIzaTestSecretKey");
        db.SetTelegramBotToken("123456:ABC-TestToken");
        db.SetAdminChatId("987654321");
        db.SetDateApproved("2026-11-13", DateTime.UtcNow, 95, "Matematik 15 soru", "c:\\test.jpg");

        var dbReloaded = new DatabaseService(_tempDir);
        Assert.Equal("AIzaTestSecretKey", dbReloaded.GetGeminiApiKey());
        Assert.Equal("123456:ABC-TestToken", dbReloaded.GetTelegramBotToken());
        Assert.Equal(987654321L, dbReloaded.GetAdminChatId());
        Assert.True(dbReloaded.IsDateApproved("2026-11-13"));

        dbReloaded.RevokeDateApproval("2026-11-13");
        Assert.False(dbReloaded.IsDateApproved("2026-11-13"));
    }

    [Fact]
    public async Task AdminSecurity_ThreeFailedAttemptsLocksOutAndFiresAlert()
    {
        var db = new DatabaseService(_tempDir);
        db.SetAdminPassword("CorrectPass123!");
        var sec = new AdminSecurityService(db);

        string? alertMessage = null;
        var alertTcs = new TaskCompletionSource<string>();
        sec.OnSecurityBreachDetected += msg =>
        {
            alertMessage = msg;
            alertTcs.TrySetResult(msg);
            return Task.CompletedTask;
        };

        var r1 = sec.Authenticate("wrong1");
        Assert.False(r1.Authenticated);
        Assert.Equal(2, r1.RemainingAttempts);
        Assert.False(r1.IsLockedOut);

        var r2 = sec.Authenticate("wrong2");
        Assert.False(r2.Authenticated);
        Assert.Equal(1, r2.RemainingAttempts);
        Assert.False(r2.IsLockedOut);

        var r3 = sec.Authenticate("wrong3");
        Assert.False(r3.Authenticated);
        Assert.Equal(0, r3.RemainingAttempts);
        Assert.True(r3.IsLockedOut);
        Assert.NotNull(r3.LockoutUntilUtc);

        await Task.WhenAny(alertTcs.Task, Task.Delay(2000));
        Assert.NotNull(alertMessage);
        Assert.Contains("3 ardışık hatalı", alertMessage);

        // Even correct password is blocked during lockout
        var r4 = sec.Authenticate("CorrectPass123!");
        Assert.False(r4.Authenticated);
        Assert.True(r4.IsLockedOut);
    }

    [Fact]
    public void AdminSecurity_OtpAndMasterRecoveryKeyWork()
    {
        var db = new DatabaseService(_tempDir);
        db.SetAdminPassword("MainPass!");
        string recoveryKey = db.GenerateAndStoreNewMasterRecoveryKey();
        var sec = new AdminSecurityService(db);

        string otp = sec.IssueEmergencyOtp();
        var otpAuth = sec.Authenticate(otp);
        Assert.True(otpAuth.Authenticated);
        Assert.Equal("TelegramOTP", otpAuth.AuthMethod);
        Assert.True(otpAuth.RequiresPasswordReset);
        Assert.True(sec.ValidateSessionToken(otpAuth.SessionToken));

        // OTP is single-use
        var otpReuse = sec.Authenticate(otp);
        Assert.False(otpReuse.Authenticated);

        // Offline recovery key works (even normalized without dashes)
        var recAuth = sec.Authenticate(recoveryKey.Replace("-", "").ToLowerInvariant());
        Assert.True(recAuth.Authenticated);
        Assert.Equal("MasterRecoveryKey", recAuth.AuthMethod);
        Assert.True(recAuth.RequiresPasswordReset);
    }

    [Fact]
    public async Task Coordinator_HandlesFullAdminIpcLifecycle()
    {
        var db = new DatabaseService(_tempDir);
        db.SetAdminPassword("TestAdmin#1");
        var timeSvc = new TrustedTimeService(db);
        var sec = new AdminSecurityService(db);
        var gemini = new GeminiHomeworkEvaluator();
        var coord = new HomeworkGuardCoordinator(db, timeSvc, sec, gemini, NullLogger<HomeworkGuardCoordinator>.Instance);

        // 1. Authenticate
        var authResp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.AuthenticateAdmin,
            PayloadJson = JsonSerializer.Serialize(new AdminAuthRequest { Credential = "TestAdmin#1" }, IpcJson.Options)
        }, CancellationToken.None);
        Assert.True(authResp.Success);
        var auth = JsonSerializer.Deserialize<AdminAuthResult>(authResp.PayloadJson!, IpcJson.Options)!;
        Assert.True(auth.Authenticated);

        // 2. Update settings
        var updResp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.AdminUpdateSettings,
            PayloadJson = JsonSerializer.Serialize(new AdminUpdateSettingsRequest
            {
                SessionToken = auth.SessionToken,
                StartHour = 10,
                StartMinute = 0,
                EndHour = 22,
                EndMinute = 0,
                DailyCriteria = "Yeni Fen ödevi kriteri",
                WhitelistUrls = ["https://www.khanacademy.org.tr"]
            }, IpcJson.Options)
        }, CancellationToken.None);
        Assert.True(updResp.Success);

        var settingsDto = JsonSerializer.Deserialize<AdminSettingsDto>(updResp.PayloadJson!, IpcJson.Options)!;
        Assert.Equal("Yeni Fen ödevi kriteri", settingsDto.DailyCriteria);

        // 3. Force Unlock & Force Lock
        var unlockResp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.AdminForceUnlock,
            PayloadJson = JsonSerializer.Serialize(new AdminSessionRequest { SessionToken = auth.SessionToken }, IpcJson.Options)
        }, CancellationToken.None);
        Assert.True(unlockResp.Success);
        Assert.True(db.IsDateApproved(timeSvc.GetTrustedLocalNow().ToString("yyyy-MM-dd")));

        var lockResp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.AdminForceLock,
            PayloadJson = JsonSerializer.Serialize(new AdminSessionRequest { SessionToken = auth.SessionToken }, IpcJson.Options)
        }, CancellationToken.None);
        Assert.True(lockResp.Success);
        Assert.False(db.IsDateApproved(timeSvc.GetTrustedLocalNow().ToString("yyyy-MM-dd")));

        // 4. Maintenance Mode
        var maintResp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.AdminEnterMaintenanceMode,
            PayloadJson = JsonSerializer.Serialize(new AdminSessionRequest { SessionToken = auth.SessionToken }, IpcJson.Options)
        }, CancellationToken.None);
        Assert.True(maintResp.Success);
        Assert.True(coord.IsMaintenanceMode);
        var maintStatus = JsonSerializer.Deserialize<ServiceRuntimeStatus>(maintResp.PayloadJson!, IpcJson.Options)!;
        Assert.Equal(LockState.MaintenanceMode, maintStatus.CurrentState);
    }

    [Fact]
    public void StudyWhitelistCatalog_ContainsCuratedTurkishStudySites()
    {
        var defaults = StudyWhitelistCatalog.GetDefaultUrls();
        Assert.True(defaults.Count >= 10);
        Assert.Contains("https://www.eba.gov.tr", defaults);
        Assert.Contains("https://ogmmateryal.eba.gov.tr", defaults);
        Assert.Contains("https://sozluk.gov.tr", defaults);
        Assert.Contains("https://tr.khanacademy.org", defaults);
        Assert.Contains("https://bilimgenc.tubitak.gov.tr", defaults);

        Assert.Equal("EBA", StudyWhitelistCatalog.GetFriendlyLabel("https://www.eba.gov.tr"));
        Assert.Equal("TDK Sözlük", StudyWhitelistCatalog.GetFriendlyLabel("https://sozluk.gov.tr"));
        Assert.Equal("example.edu.tr", StudyWhitelistCatalog.GetFriendlyLabel("https://www.example.edu.tr"));
    }

    [Fact]
    public void TamperProtection_IsNoOpInDevSandboxMode()
    {
        using var svc = new TamperProtectionService(NullLogger<TamperProtectionService>.Instance);
        // Should cleanly no-op on Developer Host PC without touching registry or files
        svc.EnforceProtection();
        svc.PeriodicVerify();
        svc.SuspendForMaintenance();
        Assert.True(AppBuildConfig.IS_DEV_SANDBOX_MODE);
    }

    [Fact]
    public void WeeklySchedule_EvaluatesPerDayQuotaAndBlockedDay()
    {
        var rules = new HomeworkRuleConfig
        {
            WeeklySchedule = HomeworkRuleConfig.CreateDefaultWeeklySchedule()
        };
        // 2026-11-16 is Monday, 2026-11-14 is Saturday, 2026-11-15 is Sunday
        var mon = rules.WeeklySchedule.First(d => d.Day == DayOfWeek.Monday);
        mon.MaxUsageMinutes = 60;
        var sat = rules.WeeklySchedule.First(d => d.Day == DayOfWeek.Saturday);
        sat.MaxUsageMinutes = 180;
        sat.EndHour = 23;
        var sun = rules.WeeklySchedule.First(d => d.Day == DayOfWeek.Sunday);
        sun.IsDayBlocked = true;

        var monTime = new DateTime(2026, 11, 16, 14, 0, 0);
        var (monState, _, _, _) = TrustedTimeService.EvaluateState(
            monTime, rules, isTodayApproved: true, isMaintenanceMode: false, isInternetAvailable: true, usedSecondsToday: 61 * 60);
        Assert.Equal(LockState.NightLock, monState);

        var satTime = new DateTime(2026, 11, 14, 22, 15, 0);
        var (satState, _, _, _) = TrustedTimeService.EvaluateState(
            satTime, rules, isTodayApproved: true, isMaintenanceMode: false, isInternetAvailable: true, usedSecondsToday: 90 * 60);
        Assert.Equal(LockState.UnlockedFreeTime, satState);

        var sunTime = new DateTime(2026, 11, 15, 13, 0, 0);
        var (sunState, _, sunRem, _) = TrustedTimeService.EvaluateState(
            sunTime, rules, isTodayApproved: true, isMaintenanceMode: false, isInternetAvailable: true, usedSecondsToday: 0);
        Assert.Equal(LockState.NightLock, sunState);
        Assert.Contains("Kapalı", sunRem);
    }

    [Fact]
    public void Database_PersistsAndRestoresWeeklySchedule()
    {
        var db = new DatabaseService(_tempDir);
        var cfg = db.GetRuleConfig();
        cfg.WeeklySchedule.First(d => d.Day == DayOfWeek.Wednesday).MaxUsageMinutes = 95;
        cfg.WeeklySchedule.First(d => d.Day == DayOfWeek.Friday).EndHour = 23;
        db.SaveRuleConfig(cfg);

        var reloaded = db.GetRuleConfig();
        Assert.Equal(7, reloaded.WeeklySchedule.Count);
        Assert.Equal(95, reloaded.WeeklySchedule.First(d => d.Day == DayOfWeek.Wednesday).MaxUsageMinutes);
        Assert.Equal(23, reloaded.WeeklySchedule.First(d => d.Day == DayOfWeek.Friday).EndHour);
    }

    [Fact]
    public async Task Coordinator_ReportClientEventLogsStudentActivity()
    {
        var db = new DatabaseService(_tempDir);
        var timeSvc = new TrustedTimeService(db);
        var secSvc = new AdminSecurityService(db);
        var gemini = new GeminiHomeworkEvaluator();
        var coord = new HomeworkGuardCoordinator(db, timeSvc, secSvc, gemini, NullLogger<HomeworkGuardCoordinator>.Instance);

        var resp = await coord.HandleIpcRequestAsync(new IpcRequest
        {
            Command = IpcCommands.ReportClientEvent,
            PayloadJson = JsonSerializer.Serialize(new ClientActivityEventRequest
            {
                EventType = "BROWSER_NAV_BLOCKED",
                Details = "Engellenen adres: https://youtube.com"
            }, IpcJson.Options)
        }, CancellationToken.None);

        Assert.True(resp.Success);
        var logs = db.GetRecentAuditLogs(5);
        Assert.Contains(logs, l => l.EventType == "BROWSER_NAV_BLOCKED" && l.Details.Contains("youtube.com"));
    }
}
