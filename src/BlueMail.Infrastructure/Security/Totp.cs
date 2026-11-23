using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlueMail.Infrastructure.Security;

/// <summary>
/// TOTP (RFC 6238) over HOTP (RFC 4226): HMAC-SHA1, 30-second step, 6 digits - the parameters every
/// authenticator app supports. HMAC from System.Security.Cryptography. Codes are accepted for the
/// current step and one step either side (clock drift); the caller stores the accepted step to refuse
/// replay of the same code.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 6238 default; authenticator apps only implement HMAC-SHA1. HMAC-SHA1 is not affected by SHA-1 collisions.
        HMACSHA1.HashData(secret, counter, mac);
#pragma warning restore CA5350
        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the matched step, or null. Steps not greater than <paramref name="lastUsedStep"/> are refused.</summary>
    public static long? Verify(byte[] secret, string code, DateTimeOffset now, long lastUsedStep)
    {
        code = code.Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        for (var s = current - 1; s <= current + 1; s++)
        {
            if (s <= lastUsedStep)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(code)))
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>otpauth URI (Key Uri Format) for QR codes.</summary>
    public static string ProvisioningUri(byte[] secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>RFC 4648 base32 without padding.</summary>
    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
