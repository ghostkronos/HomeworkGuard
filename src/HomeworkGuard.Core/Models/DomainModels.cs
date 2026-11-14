using System.Text.Json.Serialization;

namespace HomeworkGuard.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LockState
{
    NightLock,
    HomeworkRequired,
    UnlockedFreeTime,
    MaintenanceMode
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HomeworkEvaluationStage
{
    Idle,
    Stage1AnalyzingMedia,
    Stage2SocraticQuestions,
    Approved,
    Rejected
}

public sealed record StudySiteEntry(string Name, string Url, string Category);

/// <summary>
/// Curated Turkish and core educational study-only whitelist defaults and domain label resolver.
/// </summary>
public static class StudyWhitelistCatalog
{
    public static IReadOnlyList<StudySiteEntry> DefaultSites { get; } =
    [
        new("EBA", "https://www.eba.gov.tr", "MEB"),
        new("OGM Materyal", "https://ogmmateryal.eba.gov.tr", "MEB"),
        new("MEBİ", "https://mebi.eba.gov.tr", "MEB"),
        new("Yardımcı Kaynaklar", "https://yardimcikaynaklar.meb.gov.tr", "MEB"),
        new("TDK Sözlük", "https://sozluk.gov.tr", "Başvuru"),
        new("Khan Academy Türkçe", "https://tr.khanacademy.org", "Ders"),
        new("TÜBİTAK Bilim Genç", "https://bilimgenc.tubitak.gov.tr", "Bilim"),
        new("GeoGebra", "https://www.geogebra.org", "Matematik"),
        new("Desmos", "https://www.desmos.com", "Matematik"),
        new("PhET Simülasyonları", "https://phet.colorado.edu", "Fen"),
        new("Türkçe Vikipedi", "https://tr.wikipedia.org", "Ansiklopedi"),
        new("MEB Resmî", "https://www.meb.gov.tr", "MEB")
    ];

    public static List<string> GetDefaultUrls() => DefaultSites.Select(s => s.Url).ToList();

    public static string GetFriendlyLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        foreach (var site in DefaultSites)
        {
            if (Uri.TryCreate(site.Url, UriKind.Absolute, out var known) &&
                string.Equals(uri.Host, known.Host, StringComparison.OrdinalIgnoreCase))
            {
                return site.Name;
            }
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
    }
}

public sealed class DailyScheduleRule
{
    public DayOfWeek Day { get; set; }
    public int StartHour { get; set; } = 10;
    public int StartMinute { get; set; } = 0;
    public int EndHour { get; set; } = 22;
    public int EndMinute { get; set; } = 0;
    /// <summary>0 = Sınırsız, &gt;0 ise o gün için maksimum dakika hakkı.</summary>
    public int MaxUsageMinutes { get; set; } = 0;
    /// <summary>True ise o gün bilgisayar tüm gün kilitli tutulur.</summary>
    public bool IsDayBlocked { get; set; } = false;
}

public sealed class HomeworkRuleConfig
{
    public int StartHour { get; set; } = 10;
    public int StartMinute { get; set; } = 0;
    public int EndHour { get; set; } = 22;
    public int EndMinute { get; set; } = 0;
    public int WarningMinutesBeforeLock { get; set; } = 10;
    /// <summary>
    /// 0 = Sınırsız (EndHour:EndMinute saatine kadar). &gt; 0 ise ödev onaylandıktan sonraki günlük maksimum serbest dakika.
    /// </summary>
    public int DailyMaxUsageMinutes { get; set; } = 0;
    public string GeminiModel { get; set; } = AppBuildConfig.GeminiModelName;
    public string DailyHomeworkCriteria { get; set; } =
        "Günün ödevinden en az 10 soru çözülmüş ve işlem adımları açıkça yazılmış olmalıdır.";
    public List<string> WhitelistUrls { get; set; } = StudyWhitelistCatalog.GetDefaultUrls();
    public List<DailyScheduleRule> WeeklySchedule { get; set; } = [];

    public static List<DailyScheduleRule> CreateDefaultWeeklySchedule(
        int startHour = 10, int startMinute = 0, int endHour = 22, int endMinute = 0, int maxMinutes = 0)
    {
        DayOfWeek[] orderedDays =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        ];
        return orderedDays.Select(d => new DailyScheduleRule
        {
            Day = d,
            StartHour = startHour,
            StartMinute = startMinute,
            EndHour = endHour,
            EndMinute = endMinute,
            MaxUsageMinutes = Math.Max(0, maxMinutes),
            IsDayBlocked = false
        }).ToList();
    }

    public DailyScheduleRule GetEffectiveRuleForDay(DayOfWeek day)
    {
        var found = WeeklySchedule?.FirstOrDefault(r => r.Day == day);
        if (found is not null) return found;
        return new DailyScheduleRule
        {
            Day = day,
            StartHour = StartHour,
            StartMinute = StartMinute,
            EndHour = EndHour,
            EndMinute = EndMinute,
            MaxUsageMinutes = DailyMaxUsageMinutes,
            IsDayBlocked = false
        };
    }

    public static string GetTurkishDayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Pazartesi",
        DayOfWeek.Tuesday => "Salı",
        DayOfWeek.Wednesday => "Çarşamba",
        DayOfWeek.Thursday => "Perşembe",
        DayOfWeek.Friday => "Cuma",
        DayOfWeek.Saturday => "Cumartesi",
        DayOfWeek.Sunday => "Pazar",
        _ => day.ToString()
    };
}

public sealed class ServiceRuntimeStatus
{
    public LockState CurrentState { get; set; } = LockState.HomeworkRequired;
    public bool IsDevSandboxMode { get; set; } = AppBuildConfig.IS_DEV_SANDBOX_MODE;
    public DateTime EffectiveUtcNow { get; set; }
    public DateTime EffectiveLocalNow { get; set; }
    public bool IsTimeSyncedWithNtp { get; set; }
    public bool IsInternetAvailable { get; set; }
    public bool IsMaintenanceMode { get; set; }
    public bool IsTodayApproved { get; set; }
    public string ApprovedDate { get; set; } = string.Empty;
    public string DailyCriteria { get; set; } = string.Empty;
    public List<string> WhitelistUrls { get; set; } = [];
    public int StartHour { get; set; } = 10;
    public int StartMinute { get; set; } = 0;
    public int EndHour { get; set; } = 22;
    public int EndMinute { get; set; } = 0;
    public int WarningMinutesBeforeLock { get; set; } = 10;
    public int DailyMaxUsageMinutes { get; set; } = 0;
    public int UsedMinutesToday { get; set; } = 0;
    public bool IsTodayBlocked { get; set; }
    public string EffectiveDayName { get; set; } = string.Empty;
    public List<DailyScheduleRule> WeeklySchedule { get; set; } = [];
    public string GeminiModel { get; set; } = AppBuildConfig.GeminiModelName;
    public int FailedAdminAttempts { get; set; }
    public DateTime? AdminLockoutUntilUtc { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool ShowPreLockWarning { get; set; }
    public HomeworkEvaluationStage EvaluationStage { get; set; } = HomeworkEvaluationStage.Idle;
    public List<string> SocraticQuestions { get; set; } = [];
    public string LastAiFeedback { get; set; } = string.Empty;
    public int? LastScore { get; set; }
    public string RemainingFreeTimeText { get; set; } = string.Empty;
}

public sealed class GeminiStage1Result
{
    [JsonPropertyName("passedStage1")]
    public bool PassedStage1 { get; set; }

    [JsonPropertyName("feedback")]
    public string Feedback { get; set; } = string.Empty;

    [JsonPropertyName("detectedSummary")]
    public string DetectedSummary { get; set; } = string.Empty;

    [JsonPropertyName("socraticQuestions")]
    public List<string> SocraticQuestions { get; set; } = [];
}

public sealed class GeminiStage2Result
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("feedback")]
    public string Feedback { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

public sealed class HomeworkAttemptEntry
{
    public long Id { get; set; }
    public string DateLocal { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string Stage { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public int? Score { get; set; }
    public string Summary { get; set; } = string.Empty;
}
