using System.Security.Cryptography;
using System.Text;

namespace HomeworkGuard.Core.Security;

/// <summary>
/// Provides PBKDF2-SHA256 hashing with per-secret random salts, constant-time verification,
/// 24-character Master Recovery Key generation, and 6-digit OTP generation.
/// </summary>
public static class CryptoHelper
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 120_000;
    private const string Prefix = "PBKDF2$SHA256";
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string HashSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifySecret(string candidate, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256")
        {
            return false;
        }

        if (!int.TryParse(parts[2], out int iterations) || iterations < 10_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expectedHash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(candidate),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Generates a 24-character recovery key formatted as HG-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX.
    /// </summary>
    public static string GenerateMasterRecoveryKey()
    {
        var chars = new char[24];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        }

        var sb = new StringBuilder("HG");
        for (int i = 0; i < chars.Length; i += 4)
        {
            sb.Append('-');
            sb.Append(chars, i, 4);
        }

        return sb.ToString();
    }

    public static string NormalizeRecoveryKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string cleaned = input.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        if (cleaned.StartsWith("HG", StringComparison.Ordinal) && cleaned.Length == 26)
        {
            cleaned = cleaned[2..];
        }

        if (cleaned.Length != 24)
        {
            return input.Trim().ToUpperInvariant();
        }

        var sb = new StringBuilder("HG");
        for (int i = 0; i < 24; i += 4)
        {
            sb.Append('-');
            sb.Append(cleaned.AsSpan(i, 4));
        }

        return sb.ToString();
    }

    public static string GenerateSixDigitOtp()
    {
        int code = RandomNumberGenerator.GetInt32(100_000, 1_000_000);
        return code.ToString("D6");
    }

    public static string GenerateSessionToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }
}
