using System.Text;
using BlueMail.Application.Common;
using BlueMail.MailStore;
using BlueMail.Mime;
using MediatR;

namespace BlueMail.Application.Features.Mail;

public sealed record CleanUpCandidate(Guid Id, DateTimeOffset At, string Text, bool HasFiles, bool Draft);

/// <summary>
/// Outlook "Clean Up Conversation": a message is redundant when a later message of the same conversation quotes all of
/// it. Its text (quote markers and spacing ignored, case-folded) must appear inside a later message's text; messages
/// with files, drafts and very short texts are always kept (a file is not part of a quote).
/// </summary>
public static class Redundancy
{
    public const int MinChars = 20;

    public static IReadOnlyList<Guid> Find(IReadOnlyList<CleanUpCandidate> messages)
    {
        // Messages come in conversation order; Date has one-second precision, so equal times are ordered by position.
        var normalized = messages.Select((m, i) => (m, i, text: Normalize(m.Text))).ToList();
        var result = new List<Guid>();
        foreach (var (m, index, text) in normalized)
        {
            if (m.HasFiles || m.Draft || text.Length < MinChars)
            {
                continue;
            }

            if (normalized.Any(o => o.m.Id != m.Id && !o.m.Draft && (o.m.At > m.At || (o.m.At == m.At && o.i > index)) && o.text.Length > text.Length
                                    && o.text.Contains(text, StringComparison.Ordinal)))
            {
                result.Add(m.Id);
            }
        }

        return result;
    }

    /// <summary>Words of the text, quote markers ("&gt;") removed, single spaces, lower case.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart();
            while (line.StartsWith('>'))
            {
                line = line[1..].TrimStart();
            }

            foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(word.ToLowerInvariant());
            }
        }

        return sb.ToString();
    }
}

/// <summary>The redundant messages of a conversation; the web moves them to Trash with the usual (undoable) action.</summary>
public sealed record FindRedundantQuery(Guid? MailboxId, Guid ThreadId) : IRequest<IReadOnlyList<Guid>>;

public sealed class FindRedundantHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<FindRedundantQuery, IReadOnlyList<Guid>>
{
    /// <summary>Longer conversations are not read whole for this.</summary>
    public const int MaxMessages = 200;

    public async Task<IReadOnlyList<Guid>> Handle(FindRedundantQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var thread = await store.GetThreadAsync(access, q.ThreadId, ct) ?? throw AppException.NotFound("thread_not_found");
        var candidates = new List<CleanUpCandidate>();
        foreach (var m in thread.Messages.TakeLast(MaxMessages))
        {
            var content = await store.GetContentAsync(access, m.Id, ct);
            var text = content?.Text ?? (content?.Html is { } html ? HtmlToText.Convert(html) : m.Snippet ?? "");
            candidates.Add(new CleanUpCandidate(m.Id, m.SentAt ?? m.ReceivedAt, text, m.Attachments.Any(a => !a.Inline), m.Draft));
        }

        return Redundancy.Find(candidates);
    }
}
