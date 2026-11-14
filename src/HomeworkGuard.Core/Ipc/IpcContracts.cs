using System.Text.Json;
using System.Text.Json.Serialization;
using HomeworkGuard.Core.Models;

namespace HomeworkGuard.Core.Ipc;

public static class IpcCommands
{
    public const string GetStatus = "GetStatus";
    public const string SubmitHomeworkMedia = "SubmitHomeworkMedia";
    public const string SubmitSocraticAnswers = "SubmitSocraticAnswers";
    public const string ResetHomeworkSession = "ResetHomeworkSession";
    public const string AuthenticateAdmin = "AuthenticateAdmin";
    public const string AdminForceUnlock = "AdminForceUnlock";
    public const string AdminForceLock = "AdminForceLock";
    public const string AdminEnterMaintenanceMode = "AdminEnterMaintenanceMode";
    public const string AdminExitMaintenanceMode = "AdminExitMaintenanceMode";
    public const string AdminGetSettings = "AdminGetSettings";
    public const string AdminUpdateSettings = "AdminUpdateSettings";
    public const string AdminRotateRecoveryKey = "AdminRotateRecoveryKey";
    public const string ReportClientEvent = "ReportClientEvent";
}

public sealed class IpcRequest
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
}

public sealed class IpcResponse
{
    public string CorrelationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? PayloadJson { get; set; }

    public static IpcResponse Ok(string correlationId, string message = "", object? payload = null)
    {
        return new IpcResponse
        {
            CorrelationId = correlationId,
            Success = true,
            Message = message,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, IpcJson.Options)
        };
    }

    public static IpcResponse Fail(string correlationId, string message, string? errorCode = null, object? payload = null)
    {
        return new IpcResponse
        {
            CorrelationId = correlationId,
            Success = false,
            Message = message,
            ErrorCode = errorCode,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, IpcJson.Options)
        };
    }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class SubmitHomeworkMediaRequest
{
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string Base64Data { get; set; } = string.Empty;
    public string StudentNote { get; set; } = string.Empty;
}

public sealed class SubmitSocraticAnswersRequest
{
    public string Answer1 { get; set; } = string.Empty;
    public string Answer2 { get; set; } = string.Empty;
}

public sealed class AdminAuthRequest
{
    public string Credential { get; set; } = string.Empty;
}

public sealed class AdminAuthResult
{
    public bool Authenticated { get; set; }
    public string AuthMethod { get; set; } = string.Empty;
    public bool RequiresPasswordReset { get; set; }
    public bool IsLockedOut { get; set; }
    public int RemainingAttempts { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
    public string SessionToken { get; set; } = string.Empty;
}

public sealed class AdminSessionRequest
{
    public string SessionToken { get; set; } = string.Empty;
}

public sealed class ClientActivityEventRequest
{
    public string EventType { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

public sealed class AdminSettingsDto
{
    public int StartHour { get; set; }
    public int StartMinute { get; set; }
    public int EndHour { get; set; }
    public int EndMinute { get; set; }
    public int WarningMinutesBeforeLock { get; set; }
    public int DailyMaxUsageMinutes { get; set; }
    public List<DailyScheduleRule> WeeklySchedule { get; set; } = [];
    public string GeminiModel { get; set; } = AppBuildConfig.GeminiModelName;
    public string DailyCriteria { get; set; } = string.Empty;
    public List<string> WhitelistUrls { get; set; } = [];
    public bool HasGeminiApiKey { get; set; }
    public bool HasTelegramBotToken { get; set; }
    public string AdminChatId { get; set; } = string.Empty;
    public List<AuditLogEntry> RecentAuditLogs { get; set; } = [];
    public List<HomeworkAttemptEntry> RecentAttempts { get; set; } = [];
}

public sealed class AdminUpdateSettingsRequest
{
    public string SessionToken { get; set; } = string.Empty;
    public int StartHour { get; set; }
    public int StartMinute { get; set; }
    public int EndHour { get; set; }
    public int EndMinute { get; set; }
    public int WarningMinutesBeforeLock { get; set; } = 10;
    public int DailyMaxUsageMinutes { get; set; } = 0;
    public List<DailyScheduleRule>? WeeklySchedule { get; set; }
    public string GeminiModel { get; set; } = AppBuildConfig.GeminiModelName;
    public string DailyCriteria { get; set; } = string.Empty;
    public List<string> WhitelistUrls { get; set; } = [];
    public string? NewGeminiApiKey { get; set; }
    public string? NewTelegramBotToken { get; set; }
    public string? AdminChatId { get; set; }
    public string? NewAdminPassword { get; set; }
}

public sealed class AdminRotateRecoveryKeyResponse
{
    public string NewRecoveryKey { get; set; } = string.Empty;
}
