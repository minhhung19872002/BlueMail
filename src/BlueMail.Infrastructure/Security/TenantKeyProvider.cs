using System.Collections.Concurrent;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.Infrastructure.Security;

/// <summary>Unwraps tenant data keys with the master key and keeps them in memory for the process lifetime.</summary>
public sealed class TenantKeyProvider(KeyService keys, IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<Guid, byte[]> _cache = new();

    public async Task<byte[]> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var key))
        {
            return key;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var wrapped = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.EncryptionKeyWrapped).FirstOrDefaultAsync(ct)
                      ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");
        key = keys.UnwrapTenantKey(tenantId, wrapped);
        _cache[tenantId] = key;
        return key;
    }

    public async Task<byte[]> EncryptAsync(Guid tenantId, string purpose, Guid owner, byte[] plaintext, CancellationToken ct = default) =>
        KeyService.Encrypt(await GetAsync(tenantId, ct), plaintext, KeyService.Aad(purpose, owner));

    public async Task<byte[]> DecryptAsync(Guid tenantId, string purpose, Guid owner, byte[] ciphertext, CancellationToken ct = default) =>
        KeyService.Decrypt(await GetAsync(tenantId, ct), ciphertext, KeyService.Aad(purpose, owner));
}
