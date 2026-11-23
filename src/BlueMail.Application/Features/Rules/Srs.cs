using System.Security.Cryptography;
using System.Text;
using BlueMail.Infrastructure.Configuration;

namespace BlueMail.Application.Features.Rules;

/// <summary>
/// Sender Rewriting Scheme (PROMPT 6.8): a forwarded message leaves with MAIL FROM
/// <c>SRS0=HHHH=TT=orig-domain=orig-local@our-domain</c>, so SPF of the next hop checks our server, and a bounce to
/// that address can be returned to the original sender. HHHH is an HMAC (key derived from the master key) over
/// the rest; TT is the day; addresses older than <see cref="MaxAgeDays"/> or with a wrong hash are refused, so the
/// scheme cannot be used as an open relay.
/// </summary>
public sealed class Srs
{
    public const int MaxAgeDays = 21;
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";
    private readonly byte[] _key;
    private readonly Func<DateTimeOffset> _clock;

    public Srs(BmSettings settings) : this(Convert.FromBase64String(settings.MasterKeyBase64), () => DateTimeOffset.UtcNow)
    {
    }

    public Srs(byte[] masterKey, Func<DateTimeOffset> clock)
    {
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.ASCII.GetBytes("bluemail-srs-v1"));
        _clock = clock;
    }

    public static bool IsSrs(string address) => address.StartsWith("SRS0=", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rewrites <paramref name="original"/> (local@domain) to an address at <paramref name="ourDomain"/>. Null sender stays null.</summary>
    public string Forward(string original, string ourDomain)
    {
        if (string.IsNullOrEmpty(original))
        {
            return "";
        }

        var at = original.LastIndexOf('@');
        var local = original[..at];
        var domain = original[(at + 1)..].ToLowerInvariant();
        var tt = Day(_clock());
        return $"SRS0={Hash(tt, domain, local)}={tt}={domain}={local}@{ourDomain.ToLowerInvariant()}";
    }

    /// <summary>The original address of a valid, recent SRS0 address; null otherwise.</summary>
    public string? Reverse(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0 || !IsSrs(address))
        {
            return null;
        }

        var parts = address[..at].Split('=', 5);
        if (parts.Length != 5 || parts[1].Length != 4 || parts[2].Length != 2 || parts[3].Length == 0 || parts[4].Length == 0)
        {
            return null;
        }

        var (hash, tt, domain, local) = (parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant(), parts[3].ToLowerInvariant(), parts[4]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(tt, domain, local))))
        {
            return null;
        }

        var day = Decode(tt);
        var age = (DayNumber(_clock()) - day + 1024) % 1024;
        return day >= 0 && age <= MaxAgeDays ? local + "@" + domain : null;
    }

    private string Hash(string tt, string domain, string local)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(tt + "=" + domain + "=" + local.ToLowerInvariant()));
        var sb = new StringBuilder(4);
        for (var i = 0; i < 4; i++)
        {
            sb.Append(Base32[mac[i] & 31]);
        }

        return sb.ToString();
    }

    private static int DayNumber(DateTimeOffset t) => (int)(t.ToUnixTimeSeconds() / 86400 % 1024);

    private static string Day(DateTimeOffset t)
    {
        var d = DayNumber(t);
        return new string([Base32[d >> 5], Base32[d & 31]]);
    }

    private static int Decode(string tt)
    {
        var hi = Base32.IndexOf(tt[0], StringComparison.Ordinal);
        var lo = Base32.IndexOf(tt[1], StringComparison.Ordinal);
        return hi < 0 || lo < 0 ? -1 : (hi << 5) | lo;
    }
}
