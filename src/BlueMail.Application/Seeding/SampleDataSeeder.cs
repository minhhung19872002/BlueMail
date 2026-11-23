using BlueMail.Application.Features.Admin.Tenants;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Seeding;

public sealed record SeedOptions(string PlatformAdminEmail, string PlatformAdminPassword, string PlatformDomain, string? SampleUserPassword);

/// <summary>
/// Sample data (PROMPT 10). Every part checks for itself before inserting (a tenant by code, a user by
/// address, a group by address...), so re-running adds what is missing and never duplicates.
/// Mailbox contents are not inserted here: they are delivered through SMTP by the mail seeding of later
/// phases.
/// </summary>
public sealed class SampleDataSeeder(
    BlueMailDbContext db, TenantProvisioning tenants, UserProvisioning users, IMailStore store, ILogger<SampleDataSeeder> log)
{
    private sealed record SampleUser(string Local, string DisplayName, string? Locale, string? TimeZone, bool Admin = false, string? OrgUnit = null);

    private sealed record SampleTenant(string Code, string Name, string Domain, string Locale, string TimeZone, SampleUser[] Users,
        string[] OrgUnits, (string Local, string Name, string[] Members)[] Groups, (string Alias, string UserLocal)[] Aliases,
        (string Local, string Name, string[] Members)[] Shared);

    // Names are unique across both tenants and come from many countries and scripts.
    private static readonly SampleTenant[] Tenants =
    [
        new("bluemail", "BlueMail Việt Nam", "bluemail.test", "vi", "Asia/Ho_Chi_Minh",
        [
            new("lan", "Trần Thị Lan", "vi", null, Admin: true, OrgUnit: "Ban Giám đốc"),
            new("minh", "Nguyễn Đức Minh", "vi", null, OrgUnit: "Kinh doanh"),
            new("hoa", "Phạm Thu Hoa", "vi", null, OrgUnit: "Kinh doanh"),
            new("tuan", "Lê Anh Tuấn", "vi", null, OrgUnit: "Kỹ thuật"),
            new("ngoc", "Võ Bảo Ngọc", "vi", null, OrgUnit: "Kỹ thuật"),
            new("quang", "Đặng Văn Quang", "vi", null, OrgUnit: "Kế toán"),
            new("yen", "Hoàng Hải Yến", "vi", null, OrgUnit: "Kế toán"),
            new("kenji", "田中 健二 (Tanaka Kenji)", "ja", "Asia/Tokyo", OrgUnit: "Kỹ thuật"),
            new("mei", "王美玲 (Wang Meiling)", "zh-CN", "Asia/Shanghai", OrgUnit: "Kinh doanh"),
            new("somchai", "สมชาย ใจดี (Somchai Jaidee)", "th", "Asia/Bangkok", OrgUnit: "Kinh doanh"),
            new("anna", "Anna Kowalska", "pl", "Europe/Warsaw", OrgUnit: "Kỹ thuật"),
            new("thao", "Bùi Phương Thảo", "vi", null, OrgUnit: "Nhân sự"),
        ],
        ["Ban Giám đốc", "Kinh doanh", "Kỹ thuật", "Kế toán", "Nhân sự"],
        [("kinhdoanh", "Phòng Kinh doanh", ["minh", "hoa", "mei", "somchai"]), ("kythuat", "Phòng Kỹ thuật", ["tuan", "ngoc", "kenji", "anna"])],
        [("lan.tran", "lan"), ("sales", "minh")],
        [("hotro", "Hỗ trợ khách hàng", ["hoa", "thao", "tuan"])]),
        new("acme", "Acme Global Ltd", "acme.test", "en", "Europe/London",
        [
            new("olivia", "Olivia Bennett", "en", null, Admin: true, OrgUnit: "Management"),
            new("lucas", "Lucas Moreau", "fr", "Europe/Paris", OrgUnit: "Sales"),
            new("sofia", "Sofía Hernández", "es", "Europe/Madrid", OrgUnit: "Sales"),
            new("jonas", "Jonas Müller", "de", "Europe/Berlin", OrgUnit: "Engineering"),
            new("ivan", "Иван Петров (Ivan Petrov)", "ru", "Europe/Moscow", OrgUnit: "Engineering"),
            new("fatima", "فاطمة الزهراء (Fatima Alzahra)", "ar", "Asia/Dubai", OrgUnit: "Finance"),
            new("david", "דוד כהן (David Cohen)", "he", "Asia/Jerusalem", OrgUnit: "Finance"),
            new("jiwoo", "김지우 (Kim Jiwoo)", "ko", "Asia/Seoul", OrgUnit: "Engineering"),
            new("priya", "Priya Sharma", "en-IN", "Asia/Kolkata", OrgUnit: "Sales"),
            new("tomas", "Tomáš Dvořák", "cs", "Europe/Prague", OrgUnit: "Engineering"),
            new("amara", "Amara Okafor", "en", "Africa/Lagos", OrgUnit: "Management"),
            new("joao", "João Silva", "pt-BR", "America/Sao_Paulo", OrgUnit: "Sales"),
        ],
        ["Management", "Sales", "Engineering", "Finance"],
        [("sales", "Sales team", ["lucas", "sofia", "priya", "joao"]), ("eng", "Engineering", ["jonas", "ivan", "jiwoo", "tomas"])],
        [("o.bennett", "olivia"), ("billing", "fatima")],
        [("support", "Customer Support", ["olivia", "amara", "sofia"])]),
    ];

    public async Task SeedPlatformAsync(SeedOptions o, CancellationToken ct)
    {
        var platform = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Kind == TenantKind.Platform && t.DeletedAt == null, ct)
                       ?? await tenants.CreateAsync(new NewTenant("platform", "BlueMail Platform", o.PlatformDomain, "en", "UTC",
                           Kind: TenantKind.Platform), ct);

        var exists = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.PrimaryEmail == o.PlatformAdminEmail.ToLowerInvariant() && u.DeletedAt == null, ct);
        if (!exists)
        {
            await users.CreateAsync(new NewUser(platform.Id, o.PlatformAdminEmail, "Platform Administrator", o.PlatformAdminPassword,
                IsTenantAdmin: true, IsPlatformAdmin: true, MustChangePassword: true), ct);
            log.LogInformation("Seeded platform administrator {Email}", o.PlatformAdminEmail);
        }
    }

    public async Task SeedSampleTenantsAsync(SeedOptions o, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(o.SampleUserPassword))
        {
            throw new InvalidOperationException("BM_SEED_USER_PASSWORD is required to seed sample users.");
        }

        foreach (var t in Tenants)
        {
            var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Code == t.Code && x.DeletedAt == null, ct)
                         ?? await tenants.CreateAsync(new NewTenant(t.Code, t.Name, t.Domain, t.Locale, t.TimeZone, UserLimit: 100,
                             StorageQuotaBytes: 1024L * 1024 * 1024 * 1024), ct);

            // The sample domains are published in the dev DNS (deploy/coredns): mark them verified so mail is accepted.
            await db.Domains.IgnoreQueryFilters().Where(d => d.TenantId == tenant.Id && d.VerifiedAt == null)
                .ExecuteUpdateAsync(d => d.SetProperty(x => x.VerifiedAt, DateTimeOffset.UtcNow), ct);

            var units = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var name in t.OrgUnits)
            {
                var unit = await db.OrgUnits.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Name == name && u.DeletedAt == null, ct);
                if (unit is null)
                {
                    unit = new OrgUnit { TenantId = tenant.Id, Name = name };
                    db.OrgUnits.Add(unit);
                    await db.SaveChangesAsync(ct);
                }

                units[name] = unit.Id;
            }

            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var u in t.Users)
            {
                var email = $"{u.Local}@{t.Domain}";
                var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.PrimaryEmail == email && x.DeletedAt == null, ct)
                           ?? await users.CreateAsync(new NewUser(tenant.Id, email, u.DisplayName, o.SampleUserPassword, u.Admin,
                               MustChangePassword: false, Locale: u.Locale, TimeZone: u.TimeZone), ct);
                if (u.OrgUnit is not null && user.OrgUnitId != units[u.OrgUnit])
                {
                    user.OrgUnitId = units[u.OrgUnit];
                    await db.SaveChangesAsync(ct);
                }

                ids[u.Local] = user.Id;
            }

            foreach (var (local, name, members) in t.Groups)
            {
                var email = $"{local}@{t.Domain}";
                var group = await db.Groups.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Email == email && g.DeletedAt == null, ct);
                if (group is null)
                {
                    group = new Group { TenantId = tenant.Id, Email = email, Name = name };
                    db.Groups.Add(group);
                    await db.SaveChangesAsync(ct);
                }

                foreach (var m in members)
                {
                    var has = await db.GroupMembers.IgnoreQueryFilters().AnyAsync(x => x.GroupId == group.Id && x.UserId == ids[m] && x.DeletedAt == null, ct);
                    if (!has)
                    {
                        db.GroupMembers.Add(new GroupMember { TenantId = tenant.Id, GroupId = group.Id, UserId = ids[m] });
                    }
                }

                await db.SaveChangesAsync(ct);
            }

            foreach (var (alias, owner) in t.Aliases)
            {
                var email = $"{alias}@{t.Domain}";
                if (!await db.UserAliases.IgnoreQueryFilters().AnyAsync(a => a.Email == email && a.DeletedAt == null, ct))
                {
                    db.UserAliases.Add(new UserAlias { TenantId = tenant.Id, UserId = ids[owner], Email = email });
                    await db.SaveChangesAsync(ct);
                }
            }

            foreach (var (local, name, members) in t.Shared)
            {
                var email = $"{local}@{t.Domain}";
                var shared = await db.SharedMailboxes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Email == email && s.DeletedAt == null, ct);
                if (shared is null)
                {
                    shared = new SharedMailbox { TenantId = tenant.Id, Email = email, Name = name };
                    db.SharedMailboxes.Add(shared);
                    await db.SaveChangesAsync(ct);
                }

                await store.ProvisionMailboxAsync(tenant.Id, shared.Id, MailboxOwnerKind.Shared, ct);
                foreach (var m in members)
                {
                    if (!await db.SharedMailboxMembers.IgnoreQueryFilters().AnyAsync(x => x.SharedMailboxId == shared.Id && x.UserId == ids[m] && x.DeletedAt == null, ct))
                    {
                        db.SharedMailboxMembers.Add(new SharedMailboxMember { TenantId = tenant.Id, SharedMailboxId = shared.Id, UserId = ids[m], CanSendAs = true });
                    }
                }

                await db.SaveChangesAsync(ct);
            }

            log.LogInformation("Sample tenant {Code} ready with {Users} users", t.Code, t.Users.Length);
        }
    }
}
