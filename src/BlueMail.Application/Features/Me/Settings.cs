using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Me;

/// <summary>
/// Account settings (PROMPT 6.11; mail.user_settings). Stored as one JSON document; unknown or invalid values fall
/// back to the defaults below, so an old client can never store something the server cannot read.
/// </summary>
public sealed record MailSettings
{
    public int PageSize { get; init; } = 50;
    public string Density { get; init; } = "DEFAULT";
    public string ReadingPane { get; init; } = "RIGHT"; // as Outlook: list and message side by side
    public bool ConversationView { get; init; } = true;
    public int UndoSendSeconds { get; init; } = 10;
    public string DefaultReply { get; init; } = "REPLY";
    public string ExternalImages { get; init; } = "ASK";
    public bool KeyboardShortcuts { get; init; }
    public IReadOnlyList<string> InboxCategories { get; init; } = ["PRIMARY", "PROMOTIONS", "SOCIAL", "UPDATES", "FORUMS"];
    public string Theme { get; init; } = "SYSTEM";
    public bool SendAndArchive { get; init; }
    public string AutoAdvance { get; init; } = "LIST";
    public bool DesktopNotifications { get; init; }

    /// <summary>Lists pinned at the top of the folder pane (Outlook "Favorites"): web paths /mail/inbox, /mail/label/{id}.</summary>
    public IReadOnlyList<string> Favorites { get; init; } = ["/mail/inbox", "/mail/sent", "/mail/drafts"];

    public const int MaxFavorites = 20;

    /// <summary>Outlook "Quick Steps": named sequences of mail actions run with one click.</summary>
    public IReadOnlyList<QuickStep> QuickSteps { get; init; } = [];

    public const int MaxQuickSteps = 20;
    public const int MaxQuickStepActions = 5;

    /// <summary>What a Quick Step may do: reversible actions only (no delete forever, no spam/phishing reports).</summary>
    public static readonly IReadOnlySet<string> QuickStepActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "markRead", "markUnread", "star", "unstar", "important", "notImportant", "archive", "moveToInbox", "trash", "addLabel", "removeLabel", "moveTo", "mute",
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MailSettings Parse(string? json)
    {
        try
        {
            return Normalize(json is null ? new MailSettings() : JsonSerializer.Deserialize<MailSettings>(json, Json) ?? new MailSettings());
        }
        catch (JsonException)
        {
            return new MailSettings();
        }
    }

    public static readonly string[] Categories = ["PRIMARY", "PROMOTIONS", "SOCIAL", "UPDATES", "FORUMS"];

    public static MailSettings Normalize(MailSettings s)
    {
        var d = new MailSettings();
        return s with
        {
            PageSize = s.PageSize is 25 or 50 or 100 ? s.PageSize : d.PageSize,
            Density = s.Density is "DEFAULT" or "COMFORTABLE" or "COMPACT" ? s.Density : d.Density,
            ReadingPane = s.ReadingPane is "NONE" or "RIGHT" or "BELOW" ? s.ReadingPane : d.ReadingPane,
            UndoSendSeconds = s.UndoSendSeconds is 5 or 10 or 20 or 30 ? s.UndoSendSeconds : d.UndoSendSeconds,
            DefaultReply = s.DefaultReply is "REPLY" or "REPLY_ALL" ? s.DefaultReply : d.DefaultReply,
            ExternalImages = s.ExternalImages is "ASK" or "ALWAYS" ? s.ExternalImages : d.ExternalImages,
            // Primary is always there; the order is fixed (as Gmail).
            InboxCategories = Categories.Where(c => c == "PRIMARY" || (s.InboxCategories ?? []).Contains(c)).ToList(),
            Theme = s.Theme is "SYSTEM" or "LIGHT" or "DARK" ? s.Theme : d.Theme,
            AutoAdvance = s.AutoAdvance is "LIST" or "NEWER" or "OLDER" ? s.AutoAdvance : d.AutoAdvance,
            Favorites = (s.Favorites ?? d.Favorites).Where(f => f is { Length: > 6 and <= 64 } && f.StartsWith("/mail/", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).Take(MaxFavorites).ToList(),
            QuickSteps = (s.QuickSteps ?? []).Where(q => q is not null && !string.IsNullOrWhiteSpace(q.Name))
                .Select(q => new QuickStep(q.Id is { Length: > 0 and <= 40 } id ? id : Guid.NewGuid().ToString("N"),
                    q.Name.Trim().Length > 40 ? q.Name.Trim()[..40] : q.Name.Trim(),
                    (q.Actions ?? []).Where(a => a is not null && QuickStepActions.Contains(a.Action)
                                                 && (a.Action is not ("addLabel" or "removeLabel" or "moveTo") || a.LabelId is not null))
                        .Select(a => new QuickStepAction(a.Action, a.Action is "addLabel" or "removeLabel" or "moveTo" ? a.LabelId : null))
                        .Take(MaxQuickStepActions).ToList()))
                .Where(q => q.Actions.Count > 0).DistinctBy(q => q.Id).Take(MaxQuickSteps).ToList(),
        };
    }
}

public sealed record GetSettingsQuery : IRequest<MailSettings>;

public sealed class GetSettingsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetSettingsQuery, MailSettings>
{
    public async Task<MailSettings> Handle(GetSettingsQuery q, CancellationToken ct) =>
        MailSettings.Parse(await db.UserSettings.AsNoTracking().Where(s => s.UserId == me.UserId).Select(s => s.Settings).FirstOrDefaultAsync(ct));
}

public sealed record SaveSettingsCommand(MailSettings Settings) : IRequest<MailSettings>;

public sealed class SaveSettingsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveSettingsCommand, MailSettings>
{
    public async Task<MailSettings> Handle(SaveSettingsCommand c, CancellationToken ct)
    {
        var s = MailSettings.Normalize(c.Settings);
        var row = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == me.UserId, ct);
        if (row is null)
        {
            row = new Domain.Mail.UserSettings { UserId = me.UserId, TenantId = me.TenantId };
            db.UserSettings.Add(row);
        }

        row.Settings = JsonSerializer.Serialize(s, MailSettings.Json);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return s;
    }
}

/// <summary>Language, time zone, clock and week start of the account (PROMPT 6.11 "Chung").</summary>
public sealed record SaveProfileCommand(string DisplayName, string? Locale, string? TimeZone, string? HourCycle, int? WeekStart) : IRequest<Unit>;

public sealed class SaveProfileValidator : AbstractValidator<SaveProfileCommand>
{
    public SaveProfileValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Locale).Must(l => l is null || JsonStringLocalizerFactory.Languages.Contains(l)).WithErrorCode("invalid_option");
        RuleFor(x => x.TimeZone).Must(tz => tz is null || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.HourCycle).Must(h => h is null or "h12" or "h23").WithErrorCode("invalid_option");
        RuleFor(x => x.WeekStart).InclusiveBetween(0, 6).When(x => x.WeekStart is not null);
    }
}

public sealed class SaveProfileHandler(BlueMailDbContext db, ICurrentUser me, ISessionValidator sessions) : IRequestHandler<SaveProfileCommand, Unit>
{
    public async Task<Unit> Handle(SaveProfileCommand c, CancellationToken ct)
    {
        var u = await db.Users.FirstAsync(x => x.Id == me.UserId, ct);
        u.DisplayName = c.DisplayName.Trim();
        u.Locale = c.Locale;
        u.TimeZone = c.TimeZone;
        u.HourCycle = c.HourCycle;
        u.WeekStart = c.WeekStart;
        await db.SaveChangesAsync(ct);
        await sessions.InvalidateUserAsync(me.UserId); // cached session state carries the locale
        return Unit.Value;
    }
}

public sealed record QuickStepAction(string Action, Guid? LabelId);

public sealed record QuickStep(string Id, string Name, IReadOnlyList<QuickStepAction> Actions);

