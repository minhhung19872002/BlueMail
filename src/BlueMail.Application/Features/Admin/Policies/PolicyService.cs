using System.Text.Json;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Policies;

/// <summary>
/// Policy keys (PROMPT 7.4) with their type and default. Every key is read somewhere in the code; the convention test
/// <c>Every_policy_key_has_a_reader</c> fails when one is not.
/// </summary>
public static class PolicyKeys
{
    public sealed record Definition(string Key, string Type, object Default);

    public const string MaxMessageSizeMb = "max_message_size_mb";
    public const string BlockedExtensions = "blocked_extensions";
    public const string AllowExternalForwarding = "allow_external_forwarding";
    public const string RequireTwoFactor = "require_2fa";
    public const string PasswordMinLength = "password_min_length";
    public const string LockoutAttempts = "lockout_attempts";
    public const string ExternalSenderBanner = "external_sender_banner";
    public const string OrgAllowList = "org_allow_list";
    public const string OrgBlockList = "org_block_list";
    public const string OutboundFooter = "outbound_footer";

    public static readonly IReadOnlyList<Definition> All =
    [
        new(MaxMessageSizeMb, "int", 50),
        new(BlockedExtensions, "list", Array.Empty<string>()),
        new(AllowExternalForwarding, "bool", true),
        new(RequireTwoFactor, "bool", false),
        new(PasswordMinLength, "int", 10),
        new(LockoutAttempts, "int", 10),
        new(ExternalSenderBanner, "bool", true),
        new(OrgAllowList, "list", Array.Empty<string>()),
        new(OrgBlockList, "list", Array.Empty<string>()),
        new(OutboundFooter, "text", ""),
    ];

    public static Definition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}

/// <summary>Reads policies: the user's organisational unit first, then the tenant, then the default.</summary>
public sealed class PolicyService(BlueMailDbContext db)
{
    private readonly Dictionary<(Guid, Guid?, string), JsonElement?> _cache = [];

    public async Task<JsonElement?> RawAsync(Guid tenant, Guid? orgUnit, string key, CancellationToken ct)
    {
        if (_cache.TryGetValue((tenant, orgUnit, key), out var hit))
        {
            return hit;
        }

        var rows = await db.Policies.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenant && p.Key == key && p.DeletedAt == null && (p.OrgUnitId == null || p.OrgUnitId == orgUnit)).ToListAsync(ct);
        var row = rows.FirstOrDefault(r => r.OrgUnitId != null && r.OrgUnitId == orgUnit) ?? rows.FirstOrDefault(r => r.OrgUnitId == null);
        JsonElement? value = row is null ? null : JsonDocument.Parse(row.Value).RootElement.Clone();
        _cache[(tenant, orgUnit, key)] = value;
        return value;
    }

    public async Task<int> IntAsync(Guid tenant, Guid? orgUnit, string key, CancellationToken ct) =>
        await RawAsync(tenant, orgUnit, key, ct) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : (int)PolicyKeys.Find(key)!.Default;

    public async Task<bool> BoolAsync(Guid tenant, Guid? orgUnit, string key, CancellationToken ct) =>
        await RawAsync(tenant, orgUnit, key, ct) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : (bool)PolicyKeys.Find(key)!.Default;

    public async Task<IReadOnlyList<string>> ListAsync(Guid tenant, Guid? orgUnit, string key, CancellationToken ct) =>
        await RawAsync(tenant, orgUnit, key, ct) is { ValueKind: JsonValueKind.Array } v
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    public async Task<string> TextAsync(Guid tenant, Guid? orgUnit, string key, CancellationToken ct) =>
        await RawAsync(tenant, orgUnit, key, ct) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : (string)PolicyKeys.Find(key)!.Default;

    /// <summary>The organisational unit of a user (policies by unit).</summary>
    public Task<Guid?> OrgUnitOfAsync(Guid userId, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).Select(u => u.OrgUnitId).FirstOrDefaultAsync(ct);

    /// <summary>"address or domain" lists (org allow/block): exact address, the domain, or "@domain".</summary>
    public static bool ListMatches(IReadOnlyList<string> list, string? address)
    {
        if (address is null || list.Count == 0)
        {
            return false;
        }

        var a = address.ToLowerInvariant();
        var domain = a.Contains('@', StringComparison.Ordinal) ? a[(a.LastIndexOf('@') + 1)..] : a;
        return list.Select(x => x.Trim().ToLowerInvariant()).Any(x => x == a || x == domain || x == "@" + domain || (x.StartsWith("*.", StringComparison.Ordinal) && domain.EndsWith(x[1..], StringComparison.Ordinal)));
    }
}
