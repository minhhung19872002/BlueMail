namespace BlueMail.Application.Common;

/// <summary>
/// Fine-grained permissions "mail.admin.&lt;group&gt;.&lt;action&gt;" (PROMPT 7). Platform permissions
/// ("mail.platform.*") belong to the service owner only. New permissions are added here as features land.
/// </summary>
public static class Permissions
{
    public const string PlatformTenantsView = "mail.platform.tenants.view";
    public const string PlatformTenantsEdit = "mail.platform.tenants.edit";
    public const string AdminUsersView = "mail.admin.users.view";
    public const string AdminUsersEdit = "mail.admin.users.edit";
    public const string AdminAuditView = "mail.admin.audit.view";
    public const string AdminQuarantineView = "mail.admin.quarantine.view";
    public const string AdminQuarantineEdit = "mail.admin.quarantine.edit";
    public const string AdminSpamEdit = "mail.admin.spam.edit";

    public static readonly IReadOnlyList<string> Platform = [PlatformTenantsView, PlatformTenantsEdit];

    public static readonly IReadOnlyList<string> TenantAdmin =
        [AdminUsersView, AdminUsersEdit, AdminAuditView, AdminQuarantineView, AdminQuarantineEdit, AdminSpamEdit];

    public static IReadOnlySet<string> For(bool isPlatformAdmin, bool isTenantAdmin)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (isPlatformAdmin)
        {
            set.UnionWith(Platform);
            set.UnionWith(TenantAdmin);
        }
        else if (isTenantAdmin)
        {
            set.UnionWith(TenantAdmin);
        }

        return set;
    }
}

/// <summary>The authenticated caller of a request, established by the authentication layer.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid TenantId { get; }
    Guid SessionId { get; }
    string Email { get; }
    bool IsPlatformAdmin { get; }
    IReadOnlySet<string> Permissions { get; }
    string? Ip { get; }
    string? UserAgent { get; }
}

public static class CurrentUserExtensions
{
    public static void Require(this ICurrentUser user, string permission)
    {
        if (!user.IsAuthenticated || !user.Permissions.Contains(permission))
        {
            throw AppException.Forbidden("permission_denied");
        }
    }
}
