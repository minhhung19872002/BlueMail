using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Identity;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace BlueMail.Infrastructure.Security;

/// <summary>
/// Argon2id password hashing in PHC string format:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c> (base64 without padding).
/// Parameters follow the OWASP minimum recommendation; stored parameters are read back on verify so
/// they can be raised later (old hashes are rehashed on next successful login).
/// Argon2 is not in the .NET BCL; the decision to use Konscious.Security.Cryptography is recorded in docs/00 (D2).
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher<User>
{
    public const int MemoryKib = 19456;
    public const int Iterations = 2;
    public const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string HashPassword(User user, string password) => Hash(password);

    public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        if (!TryParse(hashedPassword, out var p))
        {
            return PasswordVerificationResult.Failed;
        }

        var actual = Compute(providedPassword, p.Salt, p.Memory, p.Iterations, p.Parallelism, p.Hash.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, p.Hash))
        {
            return PasswordVerificationResult.Failed;
        }

        return p.Memory < MemoryKib || p.Iterations < Iterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(secret, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}");
    }

    public static bool Verify(string secret, string phc)
    {
        if (!TryParse(phc, out var p))
        {
            return false;
        }

        var actual = Compute(secret, p.Salt, p.Memory, p.Iterations, p.Parallelism, p.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, p.Hash);
    }

    private static byte[] Compute(string secret, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private readonly record struct Parsed(int Memory, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryParse(string phc, out Parsed parsed)
    {
        parsed = default;
        var parts = phc.Split('$');
        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }

        int m = 0, t = 0, p = 0;
        foreach (var kv in parts[3].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": m = v; break;
                case "t": t = v; break;
                case "p": p = v; break;
                default: return false;
            }
        }

        // Refuse absurd parameters from a tampered row instead of burning CPU/RAM.
        if (m is < 8 or > 1048576 || t is < 1 or > 16 || p is < 1 or > 16)
        {
            return false;
        }

        try
        {
            parsed = new Parsed(m, t, p, FromB64(parts[4]), FromB64(parts[5]));
            return parsed.Salt.Length >= 8 && parsed.Hash.Length >= 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
}
