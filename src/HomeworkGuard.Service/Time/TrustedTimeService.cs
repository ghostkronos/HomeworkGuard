using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HomeworkGuard.Core.Models;
using HomeworkGuard.Service.Storage;

namespace HomeworkGuard.Service.Time;

/// <summary>
/// Tamper-resistant clock and state machine evaluator.
/// Queries NTP servers (pool.ntp.org, time.google.com, time.windows.com) and advances time
/// via Environment.TickCount64 (monotonic clock) so Windows system clock tampering cannot bypass rules.
/// </summary>
public sealed class TrustedTimeService
{
    private static readonly string[] NtpServers = ["pool.ntp.org", "time.google.com", "time.windows.com"];
    private readonly DatabaseService _db;
    private readonly object _lock = new();

    private DateTime _anchorUtc;
    private long _anchorTickCount64;
    private bool _isNtpSynced;
    private bool _isInternetAvailable;
    private DateTime _lastSyncAttemptUtc = DateTime.MinValue;

    public TrustedTimeService(DatabaseService db)
    {
        _db = db;
        var persistedUtc = _db.GetLastTrustedUtc();
        _anchorUtc = persistedUtc.HasValue && persistedUtc.Value > DateTime.UtcNow
            ? persistedUtc.Value
            : DateTime.UtcNow;
        _anchorTickCount64 = Environment.TickCount64;
    }

    public bool IsTimeSyncedWithNtp
    {
        get { lock (_lock) return _isNtpSynced; }
    }

    public bool IsInternetAvailable
    {
        get { lock (_lock) return _isInternetAvailable; }
    }

    public DateTime GetTrustedUtcNow()
    {
        lock (_lock)
        {
            long elapsedMs = Math.Max(0, Environment.TickCount64 - _anchorTickCount64);
            return _anchorUtc.AddMilliseconds(elapsedMs);
        }
    }

    public DateTime GetTrustedLocalNow()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(GetTrustedUtcNow(), TimeZoneInfo.Local);
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _lastSyncAttemptUtc = DateTime.UtcNow;
        }

        DateTime? ntpTime = null;
        foreach (var server in NtpServers)
        {
            ntpTime = await QueryNtpServerAsync(server, ct);
            if (ntpTime.HasValue) break;
        }

        bool internetOk = ntpTime.HasValue || await CheckBasicInternetAsync(ct);

        lock (_lock)
        {
            _isInternetAvailable = internetOk;
            if (ntpTime.HasValue)
            {
                _anchorUtc = ntpTime.Value;
                _anchorTickCount64 = Environment.TickCount64;
                _isNtpSynced = true;
            }
        }

        if (ntpTime.HasValue)
        {
            _db.SaveLastTrustedUtc(ntpTime.Value);
        }
        else
        {
            _db.SaveLastTrustedUtc(GetTrustedUtcNow());
        }
    }

    public bool ShouldRefreshNtp()
    {
        lock (_lock)
        {
            if (!_isNtpSynced)
            {
                return (DateTime.UtcNow - _lastSyncAttemptUtc) > TimeSpan.FromSeconds(30);
            }
            return (DateTime.UtcNow - _lastSyncAttemptUtc) > TimeSpan.FromMinutes(5);
        }
    }

    public static async Task<DateTime?> QueryNtpServerAsync(string ntpServer, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(ntpServer, ct);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null) return null;

            byte[] ntpData = new byte[48];
            ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (Client)

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 3000;
            udp.Client.SendTimeout = 3000;
            var endPoint = new IPEndPoint(ipv4, 123);

            await udp.SendAsync(ntpData, ntpData.Length, endPoint);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);
            var receiveResult = await udp.ReceiveAsync(timeoutCts.Token);
            if (receiveResult.Buffer.Length < 48) return null;

            const byte serverReplyTimeOffset = 40;
            ulong intPart = BitConverter.ToUInt32(receiveResult.Buffer, serverReplyTimeOffset);
            ulong fractPart = BitConverter.ToUInt32(receiveResult.Buffer, serverReplyTimeOffset + 4);

            intPart = SwapEndianness(intPart);
            fractPart = SwapEndianness(fractPart);

            ulong milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
            var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);
            return networkDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> CheckBasicInternetAsync(CancellationToken ct)
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            var addresses = await Dns.GetHostAddressesAsync("generativelanguage.googleapis.com", ct);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static uint SwapEndianness(ulong x)
    {
        return (uint)(((x & 0x000000ff) << 24) +
                      ((x & 0x0000ff00) << 8) +
                      ((x & 0x00ff0000) >> 8) +
                      ((x & 0xff000000) >> 24));
    }

    /// <summary>
    /// Deterministic state machine evaluation based on trusted local time, daily approval records,
    /// and optional DailyMaxUsageMinutes quota.
    /// </summary>
    public static (LockState State, bool ShowPreLockWarning, string RemainingText, string StatusMessage) EvaluateState(
        DateTime trustedLocalNow,
        HomeworkRuleConfig rules,
        bool isTodayApproved,
        bool isMaintenanceMode,
        bool isInternetAvailable,
        int usedSecondsToday = 0)
    {
        if (isMaintenanceMode)
        {
            return (LockState.MaintenanceMode, false, "Bakım Modu", "Sistem yönetici tarafından bakım moduna alındı.");
        }

        var todayRule = rules.GetEffectiveRuleForDay(trustedLocalNow.DayOfWeek);
        string dayName = HomeworkRuleConfig.GetTurkishDayName(trustedLocalNow.DayOfWeek);

        if (todayRule.IsDayBlocked)
        {
            return (
                LockState.NightLock,
                false,
                $"{dayName} Kapalı",
                $"{dayName} günü yönetici tarafından bilgisayar erişimine kapalı olarak ayarlanmıştır.");
        }

        var currentTod = trustedLocalNow.TimeOfDay;
        var startTod = new TimeSpan(todayRule.StartHour, todayRule.StartMinute, 0);
        var endTod = new TimeSpan(todayRule.EndHour, todayRule.EndMinute, 0);

        bool isAllowedDayWindow = IsWithinAllowedWindow(currentTod, startTod, endTod);
        if (!isAllowedDayWindow)
        {
            return (
                LockState.NightLock,
                false,
                "Gece Kilidi Aktif",
                $"Gece kilidi aktif ({todayRule.EndHour:D2}:{todayRule.EndMinute:D2} - {todayRule.StartHour:D2}:{todayRule.StartMinute:D2}). {dayName} izinli saat {todayRule.StartHour:D2}:{todayRule.StartMinute:D2}'da başlayacaktır.");
        }

        if (!isTodayApproved)
        {
            string msg = isInternetAvailable
                ? "Günlük ödeviniz henüz onaylanmadı. Ödevinizi yükleyip Yapay Zeka Asistanı ile doğrulayın."
                : "Ödev kontrolü için internet bağlantısı bekleniyor.";
            return (LockState.HomeworkRequired, false, "Ödev Onayı Bekleniyor", msg);
        }

        // Compute remaining time until EndTime
        TimeSpan remainingUntilEnd = endTod >= currentTod
            ? endTod - currentTod
            : TimeSpan.FromHours(24) - currentTod + endTod;

        TimeSpan effectiveRemaining = remainingUntilEnd;
        bool limitedByDailyQuota = false;

        if (todayRule.MaxUsageMinutes > 0)
        {
            int maxSeconds = todayRule.MaxUsageMinutes * 60;
            int leftSeconds = maxSeconds - usedSecondsToday;
            if (leftSeconds <= 0)
            {
                return (
                    LockState.NightLock,
                    false,
                    "Günlük Süre Doldu",
                    $"{dayName} günü için belirlenen {todayRule.MaxUsageMinutes} dakikalık kullanım süreniz doldu. Yönetici ek süre verene kadar kilit aktif.");
            }

            var remainingQuota = TimeSpan.FromSeconds(leftSeconds);
            if (remainingQuota < effectiveRemaining)
            {
                effectiveRemaining = remainingQuota;
                limitedByDailyQuota = true;
            }
        }

        bool showWarning = effectiveRemaining.TotalMinutes <= rules.WarningMinutesBeforeLock && effectiveRemaining.TotalSeconds > 0;
        string remainingFormatted = $"{(int)effectiveRemaining.TotalHours} sa {effectiveRemaining.Minutes} dk";
        string statusMsg = showWarning
            ? (limitedByDailyQuota
                ? $"{dayName} kullanım sürenin dolmasına {Math.Ceiling(effectiveRemaining.TotalMinutes)} dakika kaldı, çalışmalarını kaydet!"
                : $"Saat {todayRule.EndHour:D2}:{todayRule.EndMinute:D2}'de bilgisayar kilitlenecek, çalışmalarını kaydet!")
            : $"Günlük ödev onaylandı. {dayName} kalan serbest süre: {remainingFormatted}";

        return (LockState.UnlockedFreeTime, showWarning, remainingFormatted, statusMsg);
    }

    private static bool IsWithinAllowedWindow(TimeSpan current, TimeSpan start, TimeSpan end)
    {
        if (start == end) return true;
        if (start < end)
        {
            return current >= start && current < end;
        }
        // Overnight allowed window (rare, but supported cleanly)
        return current >= start || current < end;
    }
}
