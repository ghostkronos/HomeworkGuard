using System.Collections.Concurrent;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Security;
using HomeworkGuard.Service.Storage;

namespace HomeworkGuard.Service.Security;

/// <summary>
/// Manages Admin authentication via PBKDF2 password, Telegram 6-digit OTP (5-min expiry),
/// or offline 24-character Master Recovery Key. Enforces 3-strike brute-force lockout (15 min).
/// </summary>
public sealed class AdminSecurityService
{
    public const int MaxFailedAttempts = 3;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OtpValidityDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private readonly DatabaseService _db;
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new();
    private readonly object _authLock = new();

    public event Func<string, Task>? OnSecurityBreachDetected;

    public AdminSecurityService(DatabaseService db)
    {
        _db = db;
    }

    public AdminAuthResult Authenticate(string credential)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (_authLock)
        {
            var (failed, lockoutUntil, otpHash, otpExpires) = _db.GetSecurityState();

            if (lockoutUntil.HasValue && lockoutUntil.Value > nowUtc)
            {
                _db.AddAuditLog("ADMIN_LOGIN_BLOCKED", "UI", $"Kilitli süre içinde giriş denemesi (bitiş: {lockoutUntil.Value:O})");
                return new AdminAuthResult
                {
                    Authenticated = false,
                    IsLockedOut = true,
                    RemainingAttempts = 0,
                    LockoutUntilUtc = lockoutUntil.Value
                };
            }

            // Lockout expired -> reset failure counter cleanly
            if (lockoutUntil.HasValue && lockoutUntil.Value <= nowUtc)
            {
                failed = 0;
                lockoutUntil = null;
                _db.UpdateSecurityFailures(0, null);
            }

            string trimmed = credential?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmed))
            {
                return RecordFailedAttempt(failed, nowUtc);
            }

            // 1. Check PBKDF2 Admin Password
            string? passwordHash = _db.GetAdminPasswordHash();
            if (CryptoHelper.VerifySecret(trimmed, passwordHash))
            {
                return IssueSuccessSession("Password");
            }

            // 2. Check 6-digit Telegram OTP (valid for 5 minutes)
            if (!string.IsNullOrEmpty(otpHash) && otpExpires.HasValue && otpExpires.Value >= nowUtc)
            {
                if (CryptoHelper.VerifySecret(trimmed, otpHash))
                {
                    _db.ClearActiveOtp();
                    return IssueSuccessSession("TelegramOTP", requiresPasswordReset: true);
                }
            }

            // 3. Check 24-character offline Master Recovery Key
            string? recoveryHash = _db.GetMasterRecoveryKeyHash();
            string normalizedRecovery = CryptoHelper.NormalizeRecoveryKey(trimmed);
            if (CryptoHelper.VerifySecret(normalizedRecovery, recoveryHash) ||
                CryptoHelper.VerifySecret(trimmed, recoveryHash))
            {
                return IssueSuccessSession("MasterRecoveryKey", requiresPasswordReset: true);
            }

            return RecordFailedAttempt(failed, nowUtc);
        }
    }

    private AdminAuthResult IssueSuccessSession(string method, bool requiresPasswordReset = false)
    {
        _db.UpdateSecurityFailures(0, null);
        string token = CryptoHelper.GenerateSessionToken();
        _activeSessions[token] = DateTime.UtcNow.Add(SessionLifetime);
        _db.AddAuditLog("ADMIN_LOGIN_SUCCESS", "Admin", $"Yönetici girişi başarılı (Yöntem: {method})");

        return new AdminAuthResult
        {
            Authenticated = true,
            AuthMethod = method,
            RequiresPasswordReset = requiresPasswordReset,
            IsLockedOut = false,
            RemainingAttempts = MaxFailedAttempts,
            SessionToken = token
        };
    }

    private AdminAuthResult RecordFailedAttempt(int previousFailed, DateTime nowUtc)
    {
        int newFailed = previousFailed + 1;
        DateTime? newLockout = null;
        bool isLockedOut = false;

        if (newFailed >= MaxFailedAttempts)
        {
            newLockout = nowUtc.Add(LockoutDuration);
            isLockedOut = true;
            _db.UpdateSecurityFailures(newFailed, newLockout);
            _db.AddAuditLog("ADMIN_BRUTE_FORCE_LOCKOUT", "Unknown", "3 ardışık hatalı yönetici şifre denemesi. 15 dakika kilitlendi.");

            var handler = OnSecurityBreachDetected;
            if (handler is not null)
            {
                _ = Task.Run(() => handler(
                    $"🚨 GÜVENLİK UYARISI: HomeworkGuard Admin panelinde 3 ardışık hatalı şifre denemesi tespit edildi! Giriş 15 dakika süreyle kilitlendi ({newLockout.Value.ToLocalTime():HH:mm:ss}'e kadar)."));
            }
        }
        else
        {
            _db.UpdateSecurityFailures(newFailed, null);
            _db.AddAuditLog("ADMIN_LOGIN_FAILED", "Unknown", $"Hatalı yönetici giriş denemesi ({newFailed}/{MaxFailedAttempts}).");
        }

        return new AdminAuthResult
        {
            Authenticated = false,
            IsLockedOut = isLockedOut,
            RemainingAttempts = Math.Max(0, MaxFailedAttempts - newFailed),
            LockoutUntilUtc = newLockout
        };
    }

    public bool ValidateSessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_activeSessions.TryGetValue(token, out DateTime expires)) return false;

        if (expires < DateTime.UtcNow)
        {
            _activeSessions.TryRemove(token, out _);
            return false;
        }

        // Slide expiration while active
        _activeSessions[token] = DateTime.UtcNow.Add(SessionLifetime);
        return true;
    }

    public string IssueEmergencyOtp()
    {
        string otp = CryptoHelper.GenerateSixDigitOtp();
        string hash = CryptoHelper.HashSecret(otp);
        DateTime expires = DateTime.UtcNow.Add(OtpValidityDuration);
        _db.SetActiveOtp(hash, expires);
        _db.AddAuditLog("TELEGRAM_OTP_ISSUED", "AdminTelegram", $"5 dakika geçerli acil OTP üretildi (son geçerlilik: {expires:O}).");
        return otp;
    }

    public void ResetPasswordFromTelegram(string newPassword)
    {
        _db.SetAdminPassword(newPassword);
        _db.UpdateSecurityFailures(0, null);
        _db.AddAuditLog("ADMIN_PASSWORD_RESET", "AdminTelegram", "Yönetici şifresi Telegram üzerinden sıfırlandı.");
    }
}
