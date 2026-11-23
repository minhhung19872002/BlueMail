using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;

namespace BlueMail.UnitTests.Security;

public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_is_PHC_argon2id_and_verifies()
    {
        var hash = _hasher.HashPassword(new User(), "Mật khẩu rất dài 🔐");
        hash.Should().StartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        _hasher.VerifyHashedPassword(new User(), hash, "Mật khẩu rất dài 🔐").Should().Be(PasswordVerificationResult.Success);
        _hasher.VerifyHashedPassword(new User(), hash, "Mat khau rat dai").Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void Same_password_gets_different_salts()
    {
        Argon2PasswordHasher.Hash("same").Should().NotBe(Argon2PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=999999999,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=19456,t=2$c2FsdHNhbHQ$aGFzaA")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$!!!$###")]
    public void Malformed_or_abusive_hashes_fail_closed(string phc)
    {
        Argon2PasswordHasher.Verify("x", phc).Should().BeFalse();
    }

    [Fact]
    public void Weaker_stored_parameters_ask_for_rehash()
    {
        // m=8192,t=1 produced by an older configuration
        var salt = RandomNumberGenerator.GetBytes(16);
        using var argon = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes("pw")) { Salt = salt, MemorySize = 8192, Iterations = 1, DegreeOfParallelism = 1 };
        var h = argon.GetBytes(32);
        var phc = $"$argon2id$v=19$m=8192,t=1,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(h).TrimEnd('=')}";
        _hasher.VerifyHashedPassword(new User(), phc, "pw").Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
    }
}

public class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 seed "12345678901234567890"; 6-digit codes are the last 6 digits.
    private static readonly byte[] Seed = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void Matches_rfc6238_vectors(long unixTime, string expected)
    {
        Totp.Code(Seed, unixTime / 30).Should().Be(expected);
    }

    [Fact]
    public void Accepts_adjacent_step_and_refuses_replay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        var previous = Totp.Code(Seed, step - 1);
        Totp.Verify(Seed, previous, now, 0).Should().Be(step - 1);
        Totp.Verify(Seed, previous, now, step - 1).Should().BeNull("a code whose step was already used is refused");
        Totp.Verify(Seed, Totp.Code(Seed, step - 2), now, 0).Should().BeNull("outside the drift window");
        Totp.Verify(Seed, "12a456", now, 0).Should().BeNull();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc4648_vectors_without_padding(string input, string expected)
    {
        Totp.Base32(Encoding.ASCII.GetBytes(input)).Should().Be(expected);
    }
}

public class KeyServiceTests
{
    [Fact]
    public void Tenant_key_round_trips_and_is_bound_to_its_tenant()
    {
        var svc = new KeyService(RandomNumberGenerator.GetBytes(32));
        var tenantA = Guid.NewGuid();
        var wrapped = svc.NewWrappedTenantKey(tenantA);
        svc.UnwrapTenantKey(tenantA, wrapped).Should().HaveCount(32);
        var act = () => svc.UnwrapTenantKey(Guid.NewGuid(), wrapped);
        act.Should().Throw<CryptographicException>("a wrapped key copied to another tenant must not open");
    }

    [Fact]
    public void Tampered_ciphertext_fails()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var owner = Guid.NewGuid();
        var ct = KeyService.Encrypt(key, "secret"u8, KeyService.Aad("p", owner));
        ct[^1] ^= 1;
        var act = () => KeyService.Decrypt(key, ct, KeyService.Aad("p", owner));
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Master_key_must_be_32_bytes()
    {
        var act = () => new KeyService(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }
}
