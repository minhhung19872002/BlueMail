using System.Globalization;
using System.Security.Cryptography;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Sending;

/// <summary>
/// DKIM signing keys per domain (PROMPT 2.2): RSA 2048, private key encrypted with the tenant key
/// (AES-256-GCM, associated data binds it to the domain). Rotation and DNS checks come with the domain wizard
/// (Phase 9); until a key is published, receivers report dkim=fail for it, which is visible in the admin pages.
/// </summary>
public sealed class DkimKeyService(BlueMailDbContext db, TenantKeyProvider tenantKeys)
{
    public sealed record SigningKey(string Domain, string Selector, RSA Rsa);

    public static string NewSelector(DateTimeOffset now) => "bm" + now.ToString("yyyyMM", CultureInfo.InvariantCulture);

    /// <summary>The active key of a domain of the tenant; creates one when the domain has none.</summary>
    public async Task<SigningKey?> GetOrCreateAsync(Guid tenantId, string domain, CancellationToken ct)
    {
        var d = await db.Domains.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Domain == domain && x.DeletedAt == null, ct);
        if (d is null)
        {
            return null;
        }

        var key = await db.DkimKeys.IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId && k.DomainId == d.Id && k.Status == DkimKeyStatus.Active && k.DeletedAt == null)
            .OrderByDescending(k => k.ActivatedAt).FirstOrDefaultAsync(ct);
        if (key is null)
        {
            using var rsa = RSA.Create(2048);
            key = await StoreAsync(tenantId, d.Id, NewSelector(DateTimeOffset.UtcNow), rsa, ct);
        }

        var tk = await tenantKeys.GetAsync(tenantId, ct);
        var pkcs8 = KeyService.Decrypt(tk, key.PrivateKeyEncrypted, KeyService.Aad("dkim", d.Id));
        var signing = RSA.Create();
        signing.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        return new SigningKey(d.Domain, key.Selector, signing);
    }

    /// <summary>Stores a key as the active one (used by provisioning and by the dev seed for keys already in DNS).</summary>
    public async Task<DkimKey> StoreAsync(Guid tenantId, Guid domainId, string selector, RSA rsa, CancellationToken ct, DkimKeyStatus status = DkimKeyStatus.Active)
    {
        var tk = await tenantKeys.GetAsync(tenantId, ct);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var key = new DkimKey
        {
            TenantId = tenantId,
            DomainId = domainId,
            Selector = selector,
            Algorithm = DkimAlgorithm.Rsa2048,
            PrivateKeyEncrypted = KeyService.Encrypt(tk, pkcs8, KeyService.Aad("dkim", domainId)),
            PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            Status = status,
            ActivatedAt = status == DkimKeyStatus.Active ? DateTimeOffset.UtcNow : null,
        };
        CryptographicOperations.ZeroMemory(pkcs8);
        db.DkimKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>The TXT record to publish at selector._domainkey.domain.</summary>
    public static string DnsRecord(string publicKeyBase64) => "v=DKIM1; k=rsa; p=" + publicKeyBase64;
}
