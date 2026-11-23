using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Identity.Services;

public sealed record TwoFactorSetup(string ProtectedSecret, string SecretBase32, string OtpAuthUri);

/// <summary>
/// TOTP two-factor authentication (PROMPT 8.1). The secret is encrypted with the tenant key in
/// identity.two_factor; each accepted time step is stored so a code cannot be replayed. Recovery codes are
/// stored as Argon2id hashes and are single-use.
/// </summary>
public sealed class TwoFactorService(
    BlueMailDbContext db,
    UserManager<User> users,
    TenantKeyProvider keys,
    ITenantContext tenant,
    AuditWriter audit,
    IDataProtectionProvider protection)
{
    private const string Purpose = "totp-secret";
    private const int RecoveryCodeCount = 10;
    private readonly ITimeLimitedDataProtector _setup = protection.CreateProtector("bluemail.2fa-setup").ToTimeLimitedDataProtector();

    public TwoFactorSetup Begin(User user, string issuer)
    {
        var secret = Totp.NewSecret();
        var protectedSecret = _setup.Protect(Convert.ToBase64String(secret) + "|" + user.Id.ToString("N"), TimeSpan.FromMinutes(15));
        return new TwoFactorSetup(protectedSecret, Totp.Base32(secret), Totp.ProvisioningUri(secret, issuer, user.PrimaryEmail));
    }

    /// <summary>Enables 2FA if <paramref name="code"/> matches the pending secret; returns the plain recovery codes once.</summary>
    public async Task<IReadOnlyList<string>?> ConfirmAsync(User user, string protectedSecret, string code, CancellationToken ct)
    {
        byte[] secret;
        try
        {
            var parts = _setup.Unprotect(protectedSecret).Split('|');
            if (parts.Length != 2 || parts[1] != user.Id.ToString("N"))
            {
                return null;
            }

            secret = Convert.FromBase64String(parts[0]);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var step = Totp.Verify(secret, code, DateTimeOffset.UtcNow, 0);
        if (step is null)
        {
            return null;
        }

        tenant.Set(user.TenantId, user.Id);
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        var existing = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (existing is not null)
        {
            existing.DeletedAt = DateTimeOffset.UtcNow;
        }

        db.TwoFactors.Add(new TwoFactor
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            TotpSecretEncrypted = await keys.EncryptAsync(user.TenantId, Purpose, user.Id, secret, ct),
            RecoveryCodesHash = JsonSerializer.Serialize(codes.Select(Argon2PasswordHasher.Hash)),
            LastUsedStep = step.Value,
        });
        await db.SaveChangesAsync(ct);
        await users.SetTwoFactorEnabledAsync(user, true); // also rotates the security stamp
        await audit.WriteAsync(new AuditEntry("auth.2fa.enable", AuditResult.Success, user.TenantId, user.Id, user.PrimaryEmail), ct);
        return codes;
    }

    public async Task<bool> VerifyAsync(User user, string code, CancellationToken ct)
    {
        tenant.Set(user.TenantId, user.Id);
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (row is null)
        {
            return false;
        }

        var secret = await keys.DecryptAsync(user.TenantId, Purpose, user.Id, row.TotpSecretEncrypted, ct);
        var step = Totp.Verify(secret, code, DateTimeOffset.UtcNow, row.LastUsedStep);
        if (step is null)
        {
            return false;
        }

        // Compare-and-set: of two concurrent requests with the same code, only one advances the step.
        var updated = await db.TwoFactors.Where(t => t.Id == row.Id && t.LastUsedStep < step.Value)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.LastUsedStep, step.Value), ct);
        return updated == 1;
    }

    public async Task<bool> VerifyRecoveryCodeAsync(User user, string code, CancellationToken ct)
    {
        tenant.Set(user.TenantId, user.Id);
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (row is null)
        {
            return false;
        }

        var normalized = code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var hashes = JsonSerializer.Deserialize<List<string>>(row.RecoveryCodesHash) ?? [];
        var match = hashes.FindIndex(h => Argon2PasswordHasher.Verify(normalized, h));
        if (match < 0)
        {
            return false;
        }

        var old = row.RecoveryCodesHash;
        hashes.RemoveAt(match);
        var updated = await db.TwoFactors.Where(t => t.Id == row.Id && t.RecoveryCodesHash == old)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RecoveryCodesHash, JsonSerializer.Serialize(hashes)), ct);
        if (updated == 1)
        {
            await audit.WriteAsync(new AuditEntry("auth.2fa.recovery_code_used", AuditResult.Success, user.TenantId, user.Id, user.PrimaryEmail,
                NewValueJson: JsonSerializer.Serialize(new { remaining = hashes.Count })), ct);
        }

        return updated == 1;
    }

    /// <summary>10 characters from an unambiguous alphabet, shown as XXXXX-XXXXX.</summary>
    private static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> c = stackalloc char[10];
        for (var i = 0; i < c.Length; i++)
        {
            c[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(c);
    }

    public static string Display(string code) => code[..5] + "-" + code[5..];
}
