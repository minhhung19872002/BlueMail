using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Identity;
using BlueMail.Identity.Services;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Identity.Pages.Account;

/// <summary>
/// Uses a support ticket (PROMPT 7.1): only the platform administrator who asked for it, signed in to BlueID, within
/// 10 minutes, once. The browser is then signed in as the user for one hour, marked as a support session.
/// </summary>
[Authorize]
public sealed class SupportModel(BlueMailDbContext db, UserManager<User> users, SignInService signIn, BmSettings settings) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Ticket { get; set; }
    public string? ErrorKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) || string.IsNullOrEmpty(Ticket))
        {
            ErrorKey = "id.support.invalid";
            return Page();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Ticket));
        var now = DateTimeOffset.UtcNow;
        // Claimed with one conditional update: a ticket works once, even with two tabs.
        var used = await db.SupportTickets.Where(t => t.TokenHash == hash && t.ActorUserId == actorId && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.UsedAt, now), ct);
        if (used == 0)
        {
            ErrorKey = "id.support.invalid";
            return Page();
        }

        var ticket = await db.SupportTickets.AsNoTracking().FirstAsync(t => t.TokenHash == hash, ct);
        var target = await users.FindByIdAsync(ticket.TargetUserId.ToString());
        if (target is null || target.Status != UserStatus.Active)
        {
            ErrorKey = "id.support.invalid";
            return Page();
        }

        await signIn.SignOutAsync(User, ct);
        await signIn.CompleteSupportAsync(target, actorId, ct);
        return Redirect(settings.WebUrl + "/");
    }
}
