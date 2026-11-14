using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Models;
using HomeworkGuard.Core.Security;
using Microsoft.Data.Sqlite;

namespace HomeworkGuard.Service.Storage;

/// <summary>
/// Thread-safe SQLite persistence layer stored in C:\ProgramData\HomeworkGuard (or DevSandbox in dev mode).
/// Sensitive API keys and tokens are encrypted at rest using Windows DPAPI (LocalMachine scope in production).
/// </summary>
public sealed class DatabaseService
{
    private readonly string _dataDirectory;
    private readonly string _dbPath;
    private readonly string _submissionsDir;
    private readonly string _connectionString;
    private readonly object _syncRoot = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DatabaseService(string? customDataDirectory = null)
    {
        _dataDirectory = customDataDirectory ?? AppBuildConfig.GetDataDirectory();
        _dbPath = Path.Combine(_dataDirectory, "homeworkguard.db");
        _submissionsDir = Path.Combine(_dataDirectory, "Submissions");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        EnsureInitialized();
    }

    public string DataDirectory => _dataDirectory;
    public string SubmissionsDirectory => _submissionsDir;

    private void EnsureInitialized()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_submissionsDir);

        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS Settings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS DailyApprovals (
                    DateKey TEXT PRIMARY KEY,
                    ApprovedAtUtc TEXT NOT NULL,
                    Score INTEGER NOT NULL,
                    Summary TEXT NOT NULL,
                    MediaPath TEXT NOT NULL,
                    UsedSeconds INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS AuditLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TimestampUtc TEXT NOT NULL,
                    EventType TEXT NOT NULL,
                    Actor TEXT NOT NULL,
                    Details TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS HomeworkAttempts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DateLocal TEXT NOT NULL,
                    TimestampUtc TEXT NOT NULL,
                    Stage TEXT NOT NULL,
                    Approved INTEGER NOT NULL,
                    Score INTEGER NULL,
                    Summary TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SecurityState (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    FailedAttempts INTEGER NOT NULL DEFAULT 0,
                    LockoutUntilUtc TEXT NULL,
                    ActiveOtpHash TEXT NULL,
                    ActiveOtpExpiresUtc TEXT NULL
                );
                INSERT OR IGNORE INTO SecurityState (Id, FailedAttempts) VALUES (1, 0);
                """;
            cmd.ExecuteNonQuery();

            // Safe migration if upgrading existing DB without UsedSeconds column
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE DailyApprovals ADD COLUMN UsedSeconds INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists
            }

            // Seed default sandbox credentials only if none exist yet and running in Dev Sandbox Mode
            if (AppBuildConfig.IS_DEV_SANDBOX_MODE)
            {
                if (string.IsNullOrEmpty(GetSettingInternal(conn, "AdminPasswordHash")))
                {
                    SetSettingInternal(conn, "AdminPasswordHash", CryptoHelper.HashSecret("Admin123!"));
                }
                if (string.IsNullOrEmpty(GetSettingInternal(conn, "MasterRecoveryKeyHash")))
                {
                    var devRecovery = "HG-DEV0-SANDB-OX00-0000-0000-0001";
                    SetSettingInternal(conn, "MasterRecoveryKeyHash", CryptoHelper.HashSecret(CryptoHelper.NormalizeRecoveryKey(devRecovery)));
                }
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string? GetSettingInternal(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    private static void SetSettingInternal(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static string ProtectSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(plainText);
        var scope = AppBuildConfig.IS_DEV_SANDBOX_MODE
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine;
        byte[] protectedBytes = ProtectedData.Protect(bytes, null, scope);
        return "DPAPI:" + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectSecret(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        if (!stored.StartsWith("DPAPI:", StringComparison.Ordinal)) return stored;

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(stored["DPAPI:".Length..]);
            var scope = AppBuildConfig.IS_DEV_SANDBOX_MODE
                ? DataProtectionScope.CurrentUser
                : DataProtectionScope.LocalMachine;
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, scope);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public HomeworkRuleConfig GetRuleConfig()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            var cfg = new HomeworkRuleConfig();

            if (int.TryParse(GetSettingInternal(conn, "StartHour"), out int sh)) cfg.StartHour = sh;
            if (int.TryParse(GetSettingInternal(conn, "StartMinute"), out int sm)) cfg.StartMinute = sm;
            if (int.TryParse(GetSettingInternal(conn, "EndHour"), out int eh)) cfg.EndHour = eh;
            if (int.TryParse(GetSettingInternal(conn, "EndMinute"), out int em)) cfg.EndMinute = em;
            if (int.TryParse(GetSettingInternal(conn, "WarningMinutesBeforeLock"), out int wm)) cfg.WarningMinutesBeforeLock = wm;
            if (int.TryParse(GetSettingInternal(conn, "DailyMaxUsageMinutes"), out int maxMin)) cfg.DailyMaxUsageMinutes = Math.Max(0, maxMin);

            var model = GetSettingInternal(conn, "GeminiModel");
            if (!string.IsNullOrWhiteSpace(model) &&
                !string.Equals(model.Trim(), "gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
            {
                cfg.GeminiModel = model.Trim();
            }

            var criteria = GetSettingInternal(conn, "DailyHomeworkCriteria");
            if (!string.IsNullOrWhiteSpace(criteria)) cfg.DailyHomeworkCriteria = criteria;

            var whitelistJson = GetSettingInternal(conn, "WhitelistUrlsJson");
            if (!string.IsNullOrWhiteSpace(whitelistJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(whitelistJson, JsonOpts);
                    if (parsed is not null) cfg.WhitelistUrls = parsed;
                }
                catch
                {
                    // Keep default whitelist on corrupt JSON
                }
            }

            var weeklyJson = GetSettingInternal(conn, "WeeklyScheduleJson");
            if (!string.IsNullOrWhiteSpace(weeklyJson))
            {
                try
                {
                    var parsedWeekly = JsonSerializer.Deserialize<List<DailyScheduleRule>>(weeklyJson, JsonOpts);
                    if (parsedWeekly is { Count: > 0 })
                    {
                        cfg.WeeklySchedule = NormalizeWeeklySchedule(parsedWeekly, cfg);
                    }
                    else
                    {
                        cfg.WeeklySchedule = HomeworkRuleConfig.CreateDefaultWeeklySchedule(
                            cfg.StartHour, cfg.StartMinute, cfg.EndHour, cfg.EndMinute, cfg.DailyMaxUsageMinutes);
                    }
                }
                catch
                {
                    cfg.WeeklySchedule = HomeworkRuleConfig.CreateDefaultWeeklySchedule(
                        cfg.StartHour, cfg.StartMinute, cfg.EndHour, cfg.EndMinute, cfg.DailyMaxUsageMinutes);
                }
            }
            else
            {
                cfg.WeeklySchedule = HomeworkRuleConfig.CreateDefaultWeeklySchedule(
                    cfg.StartHour, cfg.StartMinute, cfg.EndHour, cfg.EndMinute, cfg.DailyMaxUsageMinutes);
            }

            return cfg;
        }
    }

    private static List<DailyScheduleRule> NormalizeWeeklySchedule(List<DailyScheduleRule> raw, HomeworkRuleConfig fallback)
    {
        var defaults = HomeworkRuleConfig.CreateDefaultWeeklySchedule(
            fallback.StartHour, fallback.StartMinute, fallback.EndHour, fallback.EndMinute, fallback.DailyMaxUsageMinutes);
        var map = raw.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.First());
        return defaults.Select(d => map.TryGetValue(d.Day, out var existing) ? existing : d).ToList();
    }

    public void SaveRuleConfig(HomeworkRuleConfig cfg)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "StartHour", Math.Clamp(cfg.StartHour, 0, 23).ToString());
            SetSettingInternal(conn, "StartMinute", Math.Clamp(cfg.StartMinute, 0, 59).ToString());
            SetSettingInternal(conn, "EndHour", Math.Clamp(cfg.EndHour, 0, 23).ToString());
            SetSettingInternal(conn, "EndMinute", Math.Clamp(cfg.EndMinute, 0, 59).ToString());
            SetSettingInternal(conn, "WarningMinutesBeforeLock", Math.Clamp(cfg.WarningMinutesBeforeLock, 1, 60).ToString());
            SetSettingInternal(conn, "DailyMaxUsageMinutes", Math.Max(0, cfg.DailyMaxUsageMinutes).ToString());
            SetSettingInternal(conn, "GeminiModel", string.IsNullOrWhiteSpace(cfg.GeminiModel) ? AppBuildConfig.GeminiModelName : cfg.GeminiModel.Trim());
            SetSettingInternal(conn, "DailyHomeworkCriteria", cfg.DailyHomeworkCriteria ?? string.Empty);
            SetSettingInternal(conn, "WhitelistUrlsJson", JsonSerializer.Serialize(cfg.WhitelistUrls ?? [], JsonOpts));
            var weekly = NormalizeWeeklySchedule(cfg.WeeklySchedule ?? [], cfg);
            SetSettingInternal(conn, "WeeklyScheduleJson", JsonSerializer.Serialize(weekly, JsonOpts));
        }
    }

    public void UpdateDailyCriteria(string criteria)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "DailyHomeworkCriteria", criteria.Trim());
        }
    }

    public string GetGeminiApiKey()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            var stored = GetSettingInternal(conn, "GeminiApiKey");
            var val = UnprotectSecret(stored);
            if (!string.IsNullOrWhiteSpace(val)) return val;
            return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
        }
    }

    public void SetGeminiApiKey(string apiKey)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "GeminiApiKey", ProtectSecret(apiKey.Trim()));
        }
    }

    public string GetTelegramBotToken()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            var stored = GetSettingInternal(conn, "TelegramBotToken");
            return UnprotectSecret(stored);
        }
    }

    public void SetTelegramBotToken(string token)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "TelegramBotToken", ProtectSecret(token.Trim()));
        }
    }

    public long? GetAdminChatId()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            var val = GetSettingInternal(conn, "AdminChatId");
            return long.TryParse(val, out long id) ? id : null;
        }
    }

    public void SetAdminChatId(string chatId)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "AdminChatId", chatId.Trim());
        }
    }

    public string? GetAdminPasswordHash()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            return GetSettingInternal(conn, "AdminPasswordHash");
        }
    }

    public void SetAdminPassword(string newPlainPassword)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "AdminPasswordHash", CryptoHelper.HashSecret(newPlainPassword));
        }
    }

    public string? GetMasterRecoveryKeyHash()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            return GetSettingInternal(conn, "MasterRecoveryKeyHash");
        }
    }

    public string GenerateAndStoreNewMasterRecoveryKey()
    {
        string key = CryptoHelper.GenerateMasterRecoveryKey();
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "MasterRecoveryKeyHash", CryptoHelper.HashSecret(key));
        }
        return key;
    }

    public bool IsDateApproved(string dateKey)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM DailyApprovals WHERE DateKey = $d;";
            cmd.Parameters.AddWithValue("$d", dateKey);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }

    public void SetDateApproved(string dateKey, DateTime approvedAtUtc, int score, string summary, string mediaPath)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DailyApprovals (DateKey, ApprovedAtUtc, Score, Summary, MediaPath)
                VALUES ($d, $t, $s, $sum, $m)
                ON CONFLICT(DateKey) DO UPDATE SET
                    ApprovedAtUtc = excluded.ApprovedAtUtc,
                    Score = excluded.Score,
                    Summary = excluded.Summary,
                    MediaPath = excluded.MediaPath;
                """;
            cmd.Parameters.AddWithValue("$d", dateKey);
            cmd.Parameters.AddWithValue("$t", approvedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$s", score);
            cmd.Parameters.AddWithValue("$sum", summary);
            cmd.Parameters.AddWithValue("$m", mediaPath);
            cmd.ExecuteNonQuery();
        }
    }

    public void RevokeDateApproval(string dateKey)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM DailyApprovals WHERE DateKey = $d;";
            cmd.Parameters.AddWithValue("$d", dateKey);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetUsedSecondsForDate(string dateKey)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT UsedSeconds FROM DailyApprovals WHERE DateKey = $d;";
            cmd.Parameters.AddWithValue("$d", dateKey);
            var res = cmd.ExecuteScalar();
            return res is long l ? (int)l : (res is int i ? i : 0);
        }
    }

    public void AddUsedSecondsForDate(string dateKey, int deltaSeconds)
    {
        if (deltaSeconds <= 0) return;
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE DailyApprovals SET UsedSeconds = UsedSeconds + $delta WHERE DateKey = $d;";
            cmd.Parameters.AddWithValue("$delta", deltaSeconds);
            cmd.Parameters.AddWithValue("$d", dateKey);
            cmd.ExecuteNonQuery();
        }
    }

    public void ResetUsedSecondsForDate(string dateKey)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE DailyApprovals SET UsedSeconds = 0 WHERE DateKey = $d;";
            cmd.Parameters.AddWithValue("$d", dateKey);
            cmd.ExecuteNonQuery();
        }
    }

    public (int FailedAttempts, DateTime? LockoutUntilUtc, string? ActiveOtpHash, DateTime? ActiveOtpExpiresUtc) GetSecurityState()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FailedAttempts, LockoutUntilUtc, ActiveOtpHash, ActiveOtpExpiresUtc FROM SecurityState WHERE Id = 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (0, null, null, null);

            int failed = reader.GetInt32(0);
            DateTime? lockout = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
            string? otpHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTime? otpExp = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);
            return (failed, lockout, otpHash, otpExp);
        }
    }

    public void UpdateSecurityFailures(int failedAttempts, DateTime? lockoutUntilUtc)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SecurityState
                SET FailedAttempts = $f, LockoutUntilUtc = $l
                WHERE Id = 1;
                """;
            cmd.Parameters.AddWithValue("$f", failedAttempts);
            cmd.Parameters.AddWithValue("$l", lockoutUntilUtc.HasValue ? lockoutUntilUtc.Value.ToString("O") : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetActiveOtp(string otpHash, DateTime expiresUtc)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SecurityState
                SET ActiveOtpHash = $h, ActiveOtpExpiresUtc = $e
                WHERE Id = 1;
                """;
            cmd.Parameters.AddWithValue("$h", otpHash);
            cmd.Parameters.AddWithValue("$e", expiresUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearActiveOtp()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE SecurityState SET ActiveOtpHash = NULL, ActiveOtpExpiresUtc = NULL WHERE Id = 1;";
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveLastTrustedUtc(DateTime utcNow)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            SetSettingInternal(conn, "LastKnownUtcTicks", utcNow.Ticks.ToString());
        }
    }

    public DateTime? GetLastTrustedUtc()
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            var val = GetSettingInternal(conn, "LastKnownUtcTicks");
            if (long.TryParse(val, out long ticks) && ticks > 0)
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }
            return null;
        }
    }

    public void AddAuditLog(string eventType, string actor, string details)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AuditLogs (TimestampUtc, EventType, Actor, Details)
                VALUES ($t, $e, $a, $d);
                """;
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$e", eventType);
            cmd.Parameters.AddWithValue("$a", actor);
            cmd.Parameters.AddWithValue("$d", details);
            cmd.ExecuteNonQuery();
        }
    }

    public List<AuditLogEntry> GetRecentAuditLogs(int limit = 50)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, TimestampUtc, EventType, Actor, Details
                FROM AuditLogs
                ORDER BY Id DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<AuditLogEntry>();
            while (reader.Read())
            {
                list.Add(new AuditLogEntry
                {
                    Id = reader.GetInt64(0),
                    TimestampUtc = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    EventType = reader.GetString(2),
                    Actor = reader.GetString(3),
                    Details = reader.GetString(4)
                });
            }
            return list;
        }
    }

    public void AddHomeworkAttempt(string dateLocal, string stage, bool approved, int? score, string summary)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO HomeworkAttempts (DateLocal, TimestampUtc, Stage, Approved, Score, Summary)
                VALUES ($dl, $tu, $st, $ap, $sc, $su);
                """;
            cmd.Parameters.AddWithValue("$dl", dateLocal);
            cmd.Parameters.AddWithValue("$tu", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$st", stage);
            cmd.Parameters.AddWithValue("$ap", approved ? 1 : 0);
            cmd.Parameters.AddWithValue("$sc", score.HasValue ? score.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$su", summary);
            cmd.ExecuteNonQuery();
        }
    }

    public List<HomeworkAttemptEntry> GetRecentAttempts(int limit = 20)
    {
        lock (_syncRoot)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, DateLocal, TimestampUtc, Stage, Approved, Score, Summary
                FROM HomeworkAttempts
                ORDER BY Id DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<HomeworkAttemptEntry>();
            while (reader.Read())
            {
                list.Add(new HomeworkAttemptEntry
                {
                    Id = reader.GetInt64(0),
                    DateLocal = reader.GetString(1),
                    TimestampUtc = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Stage = reader.GetString(3),
                    Approved = reader.GetInt32(4) == 1,
                    Score = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    Summary = reader.GetString(6)
                });
            }
            return list;
        }
    }
}
