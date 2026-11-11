using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Lyceum.Services;

/// <summary>
/// Time-based one-time passwords (RFC 6238: 30-second steps, 6 digits, HMAC-SHA1), as used by
/// Google Authenticator, Aegis, 1Password, Bitwarden… Secrets are stored encrypted with Data Protection.
/// </summary>
public sealed class TotpService(IDataProtectionProvider dataProtection)
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private readonly IDataProtector _protector = dataProtection.CreateProtector("CourseHub.Totp.v1");  // Former app name kept on purpose: it is part of the encryption key for data already stored.

    public string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string stored) => _protector.Unprotect(stored);

    public static string ProvisioningUri(string secret, string account, string issuer = "Lyceum") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    /// <summary>Shows the secret in groups of four, easier to type into an app by hand.</summary>
    public static string Pretty(string secret) => string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4))));

    public static long CurrentStep => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>
    /// Checks a code against the current step and one step either side (clock drift).
    /// Returns the matching step, or null. Steps at or before <paramref name="lastUsedStep"/> are refused (no replay).
    /// </summary>
    public static long? Verify(string secret, string code, long lastUsedStep)
    {
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != Digits) return null;
        var key = Base32Decode(secret);
        var now = CurrentStep;
        for (var step = now - 1; step <= now + 1; step++)
        {
            if (step <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step)), Encoding.ASCII.GetBytes(code))) return step;
        }
        return null;
    }

    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xff); step >>= 8; }
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    // ---------- recovery codes ----------

    /// <summary>Eight one-time codes like "k7f2-9xq4" for when the phone is lost.</summary>
    public static List<string> NewRecoveryCodes()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return Enumerable.Range(0, 8).Select(_ =>
        {
            var chars = Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
            return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        }).ToList();
    }

    public static string HashRecovery(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant().Replace(" ", ""))));

    // ---------- base32 (RFC 4648, no padding) ----------

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c != '=' && c != ' '))
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return [.. bytes];
    }
}
