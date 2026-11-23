using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BlueMail.Domain.Mail;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;

namespace BlueMail.Imap;

internal sealed partial class ImapSession
{
    /// <summary>System labels shown as folders, with their names and RFC 6154 attributes. Others (Snoozed, tabs) stay web-only.</summary>
    private static readonly Dictionary<string, (string Name, string? Use)> SystemFolders = new()
    {
        [SystemLabels.Inbox] = ("INBOX", null),
        [SystemLabels.Sent] = ("Sent", "\\Sent"),
        [SystemLabels.Drafts] = ("Drafts", "\\Drafts"),
        [SystemLabels.Spam] = ("Spam", "\\Junk"),
        [SystemLabels.Trash] = ("Trash", "\\Trash"),
        [SystemLabels.Starred] = ("Starred", "\\Flagged"),
        [SystemLabels.Important] = ("Important", "\\Important"),
    };

    private async Task<List<Folder>> FoldersAsync(CancellationToken ct)
    {
        var (labels, unsubscribed) = await WithStoreAsync(async s => (await s.GetLabelsAsync(_access!, ct), await s.UnsubscribedAsync(_access!, ct)));
        var byId = labels.ToDictionary(l => l.Id);
        string Path(LabelSummary l)
        {
            var parts = new List<string> { l.Name };
            var parent = l.ParentId;
            for (var guard = 0; parent is { } p && byId.TryGetValue(p, out var pl) && guard < 32; guard++)
            {
                parts.Insert(0, pl.Name);
                parent = pl.ParentId;
            }

            return string.Join('/', parts);
        }

        var folders = new List<Folder>();
        foreach (var l in labels)
        {
            if (l.SystemKey is { } key)
            {
                if (SystemFolders.TryGetValue(key, out var sf))
                {
                    folders.Add(new Folder(l.Id, key, sf.Name, sf.Use, !unsubscribed.Contains(l.Id), false));
                }

                continue;
            }

            folders.Add(new Folder(l.Id, null, Path(l), null, !unsubscribed.Contains(l.Id), labels.Any(c => c.ParentId == l.Id)));
        }

        return folders;
    }

    private async Task<Folder?> FindFolderAsync(string name, CancellationToken ct) =>
        (await FoldersAsync(ct)).FirstOrDefault(f => f.SystemKey == SystemLabels.Inbox ? name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) : f.Name == name);

    // ---- LIST / LSUB (RFC 3501 6.3.8-9, RFC 5258, RFC 6154) ----

    private static readonly HashSet<string> SelectionOptions = new(StringComparer.OrdinalIgnoreCase) { "SUBSCRIBED", "REMOTE", "RECURSIVEMATCH", "SPECIAL-USE" };
    private static readonly HashSet<string> ReturnOptions = new(StringComparer.OrdinalIgnoreCase) { "SUBSCRIBED", "CHILDREN", "SPECIAL-USE" };

    private async Task ListAsync(string tag, bool lsub, ImapParser p, CancellationToken ct)
    {
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        p.SkipSpaces();
        if (!lsub && p.Peek() == '(')
        {
            foreach (var v in p.ReadValue(stopAtBracket: false).AsList())
            {
                var o = v.AsText() ?? "";
                if (!SelectionOptions.Contains(o))
                {
                    throw new ImapBadException("Unknown selection option " + o);
                }

                selection.Add(o);
            }

            // RFC 5258 3.1: RECURSIVEMATCH needs another option that selects something (REMOTE alone does not).
            if (selection.Contains("RECURSIVEMATCH") && !selection.Contains("SUBSCRIBED") && !selection.Contains("SPECIAL-USE"))
            {
                throw new ImapBadException("RECURSIVEMATCH needs a selection option");
            }
        }

        var reference = ModifiedUtf7.Decode(AString(p));
        p.SkipSpaces();
        var patterns = new List<string>();
        if (p.Peek() == '(')
        {
            patterns.AddRange(p.ReadValue(stopAtBracket: false).AsList().Select(v => ModifiedUtf7.Decode(v.AsText() ?? "")));
        }
        else
        {
            patterns.Add(ModifiedUtf7.Decode(AString(p)));
        }

        var returns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        p.SkipSpaces();
        if (!p.AtEnd)
        {
            if (!AString(p).Equals("RETURN", StringComparison.OrdinalIgnoreCase))
            {
                throw new ImapBadException("RETURN expected");
            }

            p.SkipSpaces();
            foreach (var v in p.ReadValue(stopAtBracket: false).AsList())
            {
                var o = v.AsText() ?? "";
                if (!ReturnOptions.Contains(o))
                {
                    throw new ImapBadException("Unknown return option " + o);
                }

                returns.Add(o);
            }
        }

        End(p);
        var verb = lsub ? "LSUB" : "LIST";
        if (patterns.Count == 1 && patterns[0].Length == 0)
        {
            _w.Line($"* {verb} (\\Noselect) \"/\" \"\""); // hierarchy delimiter request
            Ok(tag, verb + " completed");
            return;
        }

        var folders = await FoldersAsync(ct);
        bool Match(string name) => patterns.Any(pat => Matches(reference + pat, name));
        var subscribedOnly = lsub || selection.Contains("SUBSCRIBED");
        var specialOnly = selection.Contains("SPECIAL-USE");
        var recursive = selection.Contains("RECURSIVEMATCH");
        var showSubscribed = !lsub && (subscribedOnly || returns.Contains("SUBSCRIBED"));
        foreach (var f in folders)
        {
            var matches = Match(f.Name);
            var selected = (!subscribedOnly || f.Subscribed) && (!specialOnly || f.SpecialUse is not null);
            var attrs = new List<string> { f.HasChildren ? "\\HasChildren" : "\\HasNoChildren" };
            if (f.SpecialUse is { } use)
            {
                attrs.Add(use);
            }

            if (showSubscribed && f.Subscribed)
            {
                attrs.Add("\\Subscribed");
            }

            if (matches && selected)
            {
                _w.Raw($"* {verb} ({string.Join(' ', attrs)}) \"/\" ").Str(f.Wire).Raw("\r\n");
                continue;
            }

            if (!matches || !subscribedOnly || f.Subscribed)
            {
                continue;
            }

            // A parent that is not subscribed itself but has subscribed descendants not matched by the patterns:
            // CHILDINFO with RECURSIVEMATCH (RFC 5258 3.5), \Noselect in LSUB for '%' (RFC 3501 6.3.9).
            var hidden = folders.Where(c => c.Name.StartsWith(f.Name + "/", StringComparison.Ordinal) && c.Subscribed && !Match(c.Name)).ToList();
            if (hidden.Count == 0)
            {
                continue;
            }

            if (lsub)
            {
                _w.Raw("* LSUB (\\Noselect) \"/\" ").Str(f.Wire).Raw("\r\n");
            }
            else if (recursive)
            {
                attrs.Remove("\\Subscribed");
                _w.Raw($"* LIST ({string.Join(' ', attrs)}) \"/\" ").Str(f.Wire).Raw(" (CHILDINFO (\"SUBSCRIBED\"))\r\n");
            }
        }

        Ok(tag, verb + " completed");
    }

    /// <summary>'*' matches anything, '%' anything but the delimiter (RFC 3501 6.3.8). INBOX is case-insensitive.</summary>
    internal static bool Matches(string pattern, string name)
    {
        var rx = "^" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '%' => "[^/]*",
            _ => Regex.Escape(c.ToString()),
        })) + "$";
        var options = RegexOptions.CultureInvariant | (name == "INBOX" ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.IsMatch(name, rx, options, TimeSpan.FromMilliseconds(200));
    }

    // ---- STATUS ----

    private async Task StatusAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var name = MailboxName(p);
        p.SkipSpaces();
        var items = p.ReadValue(stopAtBracket: false).AsList().Select(v => (v.AsText() ?? "").ToUpperInvariant()).ToList();
        End(p);
        var f = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "NONEXISTENT");
        var st = await WithStoreAsync(s => s.ImapStatusAsync(_access!, f.LabelId, ct));
        var parts = new List<string>();
        foreach (var item in items)
        {
            parts.Add(item switch
            {
                "MESSAGES" => "MESSAGES " + st.Messages,
                "RECENT" => "RECENT " + (RecentSeen.TryGetValue(f.LabelId, out var seen) ? Math.Max(0, st.UidNext - 1 - seen) : 0).ToString(CultureInfo.InvariantCulture),
                "UIDNEXT" => "UIDNEXT " + st.UidNext,
                "UIDVALIDITY" => "UIDVALIDITY " + st.UidValidity,
                "UNSEEN" => "UNSEEN " + st.Unseen,
                "HIGHESTMODSEQ" => "HIGHESTMODSEQ " + st.HighestModseq,
                "SIZE" => "SIZE " + st.Size,
                _ => throw new ImapBadException("Unknown STATUS item " + item),
            });
        }

        _w.Raw("* STATUS ").Str(f.Wire).Raw(" (" + string.Join(' ', parts) + ")\r\n");
        Ok(tag, "STATUS completed");
    }

    // ---- SELECT / EXAMINE (+ CONDSTORE, QRESYNC) ----

    private async Task SelectAsync(string tag, bool readOnly, ImapParser p, CancellationToken ct)
    {
        var name = MailboxName(p);
        long? qValidity = null, qModseq = null;
        ImapSequenceSet? qKnown = null;
        p.SkipSpaces();
        if (p.Peek() == '(')
        {
            var ps = p.ReadValue(stopAtBracket: false).AsList();
            for (var i = 0; i < ps.Count; i++)
            {
                switch ((ps[i].AsText() ?? "").ToUpperInvariant())
                {
                    case "CONDSTORE":
                        _condstore = true;
                        break;
                    case "QRESYNC":
                        if (!_qresync)
                        {
                            throw new ImapBadException("QRESYNC not enabled");
                        }

                        var q = i + 1 < ps.Count ? ps[++i].AsList() : [];
                        qValidity = q.Count > 0 ? q[0].AsNumber() : null;
                        qModseq = q.Count > 1 ? q[1].AsNumber() : null;
                        if (q.Count > 2 && q[2].AsText() is { } known && ImapSequenceSet.TryParse(known, out var ks))
                        {
                            qKnown = ks;
                        }

                        if (qValidity is null || qModseq is null)
                        {
                            throw new ImapBadException("QRESYNC needs UIDVALIDITY and MODSEQ");
                        }

                        break;
                    default:
                        throw new ImapBadException("Unknown SELECT parameter");
                }
            }
        }

        End(p);
        var hadSelection = _sel is not null;
        _sel = null;
        registry.Unwatch(this);
        if (hadSelection && _qresync)
        {
            _w.Line("* OK [CLOSED] Previous mailbox closed");
        }

        var f = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "NONEXISTENT");
        var sel = new Selected(f, readOnly);
        var (rows, st) = await WithStoreAsync(async s => (await s.ImapListAsync(_access!, f.LabelId, ct), await s.ImapStatusAsync(_access!, f.LabelId, ct)));
        sel.Rows = rows.ToList();
        sel.UidValidity = st.UidValidity;
        sel.UidNext = st.UidNext;
        sel.Modseq = st.HighestModseq;
        ObserveFolder(f.LabelId, sel.Rows.Count == 0 ? 0 : sel.Rows[^1].Uid);
        var seenUpTo = RecentSeen[f.LabelId];
        foreach (var r in sel.Rows.Where(r => r.Uid > seenUpTo))
        {
            sel.Recent.Add(r.Uid);
        }

        if (!readOnly && sel.Rows.Count > 0)
        {
            RecentSeen.AddOrUpdate(f.LabelId, sel.Rows[^1].Uid, (_, old) => Math.Max(old, sel.Rows[^1].Uid));
        }

        foreach (var k in sel.Rows.SelectMany(r => r.Keywords))
        {
            sel.Keywords.Add(k);
        }

        _sel = sel;
        WriteFlagsLines();
        _w.Line("* " + sel.Rows.Count + " EXISTS");
        _w.Line("* " + sel.Recent.Count + " RECENT");
        var firstUnseen = sel.Rows.FindIndex(r => !r.Seen);
        if (firstUnseen >= 0)
        {
            _w.Line($"* OK [UNSEEN {firstUnseen + 1}] First unseen");
        }

        _w.Line($"* OK [UIDVALIDITY {sel.UidValidity}] UIDs valid");
        _w.Line($"* OK [UIDNEXT {sel.UidNext}] Predicted next UID");
        _w.Line($"* OK [HIGHESTMODSEQ {sel.Modseq}] Highest");

        if (qValidity == sel.UidValidity && qModseq is { } since)
        {
            // RFC 7162 3.2.5: what disappeared and what changed since the client's last state.
            var changes = await WithStoreAsync(s => s.ImapChangesAsync(_access!, f.LabelId, since, ct));
            var present = sel.Rows.Select(r => r.Uid).ToHashSet();
            var max = sel.Rows.Count == 0 ? 0 : sel.Rows[^1].Uid;
            var vanished = changes.Where(c => c.Change == "REMOVED" && !present.Contains(c.Uid) && (qKnown is null || qKnown.Contains(c.Uid, max))).Select(c => (uint)c.Uid)
                .Distinct().ToList();
            if (vanished.Count > 0)
            {
                _w.Line("* VANISHED (EARLIER) " + ImapArgs.UidSet(vanished));
            }

            for (var i = 0; i < sel.Rows.Count; i++)
            {
                if (sel.Rows[i].Modseq > since)
                {
                    _w.Line($"* {i + 1} FETCH (UID {sel.Rows[i].Uid} FLAGS ({Flags(sel.Rows[i])}) MODSEQ ({sel.Rows[i].Modseq}))");
                }
            }
        }

        registry.Watch(this, _access!.MailboxId);
        Ok(tag, readOnly ? "[READ-ONLY] EXAMINE completed" : "[READ-WRITE] SELECT completed");
    }

    /// <summary>
    /// Tells the client what changed in the selected folder since it last looked (other clients, the web, new mail):
    /// EXPUNGE (or VANISHED with QRESYNC), EXISTS, FETCH FLAGS. EXPUNGE is only sent where RFC 3501 7.4.1 allows it.
    /// </summary>
    private async Task RefreshAsync(bool allowExpunge, CancellationToken ct)
    {
        if (_sel is null)
        {
            return;
        }

        var highest = await WithStoreAsync(s => s.HighestModseqAsync(_access!, ct));
        if (highest == _sel.Modseq)
        {
            return;
        }

        var fresh = await WithStoreAsync(s => s.ImapListAsync(_access!, _sel.Folder.LabelId, ct));
        var freshByUid = fresh.ToDictionary(r => r.Uid);
        var rows = _sel.Rows;
        if (allowExpunge)
        {
            var gone = new List<uint>();
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (!freshByUid.ContainsKey(rows[i].Uid))
                {
                    if (_qresync)
                    {
                        gone.Add((uint)rows[i].Uid);
                    }
                    else
                    {
                        _w.Line($"* {i + 1} EXPUNGE");
                    }

                    rows.RemoveAt(i);
                }
            }

            if (gone.Count > 0)
            {
                _w.Line("* VANISHED " + ImapArgs.UidSet(gone));
            }
        }

        AnnounceKeywords(fresh);
        for (var i = 0; i < rows.Count; i++)
        {
            if (freshByUid.TryGetValue(rows[i].Uid, out var f) && (f.Modseq != rows[i].Modseq || FlagsOf(f) != FlagsOf(rows[i])))
            {
                rows[i] = f;
                _w.Line($"* {i + 1} FETCH (UID {f.Uid} FLAGS ({Flags(f)}){(_condstore ? $" MODSEQ ({f.Modseq})" : "")})");
            }
        }

        var lastUid = rows.Count == 0 ? 0 : rows[^1].Uid;
        var added = fresh.Where(r => r.Uid > lastUid).ToList();
        if (added.Count > 0)
        {
            if (!_sel.ReadOnly)
            {
                // New mail is \Recent for the first session that learns about it.
                var seenUpTo = RecentSeen.GetOrAdd(_sel.Folder.LabelId, lastUid);
                foreach (var r in added.Where(r => r.Uid > seenUpTo))
                {
                    _sel.Recent.Add(r.Uid);
                }

                RecentSeen.AddOrUpdate(_sel.Folder.LabelId, added[^1].Uid, (_, old) => Math.Max(old, added[^1].Uid));
            }

            rows.AddRange(added);
            _w.Line("* " + rows.Count + " EXISTS");
            _w.Line("* " + _sel.Recent.Count(u => freshByUid.ContainsKey(u)) + " RECENT");
        }

        _sel.Modseq = allowExpunge || rows.Count == fresh.Count ? highest : _sel.Modseq;
    }

    /// <summary>Flags of a row of the selected folder, with this session's \Recent.</summary>
    private string Flags(ImapMessageRow r)
    {
        var f = FlagsOf(r);
        return _sel is not null && _sel.Recent.Contains(r.Uid) ? (f.Length == 0 ? "\\Recent" : "\\Recent " + f) : f;
    }

    private void WriteFlagsLines()
    {
        var kws = string.Concat(_sel!.Keywords.Select(k => " " + k));
        _w.Line("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded" + kws + ")");
        _w.Line(_sel.ReadOnly ? "* OK [PERMANENTFLAGS ()] Read-only"
            : "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded" + kws + " \\*)] Flags permitted");
    }

    /// <summary>Announces keywords not yet in FLAGS before a response uses them.</summary>
    private void AnnounceKeywords(IEnumerable<ImapMessageRow> rows)
    {
        var added = false;
        foreach (var k in rows.SelectMany(r => r.Keywords))
        {
            added |= _sel!.Keywords.Add(k);
        }

        if (added)
        {
            WriteFlagsLines();
        }
    }

    internal static string FlagsOf(ImapMessageRow r)
    {
        var f = new List<string>();
        if (r.Seen)
        {
            f.Add("\\Seen");
        }

        if (r.Answered)
        {
            f.Add("\\Answered");
        }

        if (r.Flagged)
        {
            f.Add("\\Flagged");
        }

        if (r.Draft)
        {
            f.Add("\\Draft");
        }

        if (r.Deleted)
        {
            f.Add("\\Deleted");
        }

        if (r.Forwarded)
        {
            f.Add("$Forwarded");
        }

        f.AddRange(r.Keywords);
        return string.Join(' ', f);
    }

    // ---- CREATE / DELETE / RENAME: user labels ----

    private async Task CreateAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var name = MailboxName(p).TrimEnd('/');
        End(p);
        if (name.Length == 0 || name.Split('/').Any(s => s.Trim().Length == 0))
        {
            throw new ImapNoException("Invalid mailbox name", "CANNOT");
        }

        var folders = await FoldersAsync(ct);
        if (folders.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ImapNoException("Mailbox already exists", "ALREADYEXISTS");
        }

        Guid? parent = null;
        var path = "";
        foreach (var part in name.Split('/'))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            var existing = folders.FirstOrDefault(f => f.SystemKey is null && f.Name == path);
            if (existing is not null)
            {
                parent = existing.LabelId;
                continue;
            }

            var p2 = parent;
            parent = await WithStoreAsync(s => s.CreateLabelAsync(_access!, part, p2, null, null, ct));
            ObserveFolder(parent.Value, 0);
            var created = parent.Value;
            await WithStoreAsync(s => s.SetSubscribedAsync(_access!, created, false, ct));
        }

        Ok(tag, "CREATE completed");
    }

    private async Task DeleteAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var name = MailboxName(p);
        End(p);
        var f = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "NONEXISTENT");
        if (f.SystemKey is not null)
        {
            throw new ImapNoException("System folders cannot be deleted", "CANNOT");
        }

        if (_sel?.Folder.LabelId == f.LabelId)
        {
            _sel = null;
            registry.Unwatch(this);
        }

        // Like Gmail: deleting a label keeps the messages (they stay in their other folders / All Mail).
        await WithStoreAsync(s => s.DeleteLabelAsync(_access!, f.LabelId, ct));
        Ok(tag, "DELETE completed");
    }

    private async Task RenameAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var from = MailboxName(p);
        var to = MailboxName(p).TrimEnd('/');
        End(p);
        var f = await FindFolderAsync(from, ct) ?? throw new ImapNoException("No such mailbox", "NONEXISTENT");
        if (f.SystemKey is not null)
        {
            throw new ImapNoException("System folders cannot be renamed", "CANNOT");
        }

        var oldParent = from.Contains('/', StringComparison.Ordinal) ? from[..from.LastIndexOf('/')] : "";
        var newParent = to.Contains('/', StringComparison.Ordinal) ? to[..to.LastIndexOf('/')] : "";
        if (oldParent != newParent)
        {
            throw new ImapNoException("Moving a folder to another parent is not supported; rename within its parent", "CANNOT");
        }

        if (await FindFolderAsync(to, ct) is not null)
        {
            throw new ImapNoException("Mailbox already exists", "ALREADYEXISTS");
        }

        var leaf = to[(to.LastIndexOf('/') + 1)..];
        await WithStoreAsync(async s =>
        {
            var label = (await s.GetLabelsAsync(_access!, ct)).First(l => l.Id == f.LabelId);
            await s.UpdateLabelAsync(_access!, f.LabelId, leaf, label.ColorBg, label.ColorText, ct: ct);
        });
        Ok(tag, "RENAME completed");
    }

    // ---- APPEND (RFC 3501 6.3.11, UIDPLUS APPENDUID) ----

    private async Task AppendAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var name = MailboxName(p);
        var flags = new List<string>();
        DateTimeOffset? date = null;
        p.SkipSpaces();
        if (p.Peek() == '(')
        {
            flags.AddRange(p.ReadValue(stopAtBracket: false).AsList().Select(v => v is ImapNil ? "NIL" : v.AsText() ?? ""));
            p.SkipSpaces();
        }

        if (p.Peek() == '"')
        {
            var text = AString(p);
            date = ImapArgs.TryParseInternalDate(text, out var d) ? d : throw new ImapBadException("Invalid date-time");
            p.SkipSpaces();
        }

        if (p.ReadValue(stopAtBracket: false) is not ImapString { IsLiteral: true } literal)
        {
            throw new ImapBadException("Message literal expected");
        }

        End(p);
        var f = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "TRYCREATE");
        ObserveFolder(f.LabelId, (await WithStoreAsync(s => s.ImapStatusAsync(_access!, f.LabelId, ct))).UidNext - 1);
        await using var content = literal.OpenRead();
        var raw = new byte[content.Length];
        await content.ReadExactlyAsync(raw, ct);
        var parsed = MimeMessage.Parse(raw);
        var (uid, validity) = await WithStoreAsync(async s =>
        {
            // A mail program that sent through SMTP submission also APPENDs to Sent: same Message-ID, no second copy.
            if (parsed.MessageId is { } mid && await s.ImapFindByMessageIdAsync(_access!, f.LabelId, mid, ct) is { } existing)
            {
                var u = (await s.ImapUidsOfAsync(_access!, f.LabelId, [existing], ct))[existing];
                return (u, (await s.ImapStatusAsync(_access!, f.LabelId, ct)).UidValidity);
            }

            var system = new List<string>();
            var extra = new List<Guid>();
            if (f.SystemKey is { } key)
            {
                system.Add(key);
            }
            else
            {
                extra.Add(f.LabelId);
            }

            var has = (string flag) => flags.Any(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (has("\\Flagged") && !system.Contains(SystemLabels.Starred))
            {
                system.Add(SystemLabels.Starred);
            }

            var ms = new MemoryStream(raw, writable: false);
            var facts = MessageFacts.Extract(parsed, ms);
            ms.Position = 0;
            var delivered = await s.DeliverAsync(new DeliveryRequest(_access!.TenantId, ms, facts,
                [
                    new MailboxDelivery(_access.MailboxId, system, Seen: has("\\Seen"), ExtraLabelIds: extra, Draft: has("\\Draft"))
                    {
                        Answered = has("\\Answered"), Forwarded = has("$Forwarded"),
                        Keywords = flags.Where(x => !x.StartsWith('\\') && !x.Equals("$Forwarded", StringComparison.OrdinalIgnoreCase)).ToList(),
                    },
                ], date ?? DateTimeOffset.UtcNow), ct);
            var id = delivered[0].MessageId;
            return ((await s.ImapUidsOfAsync(_access, f.LabelId, [id], ct))[id], (await s.ImapStatusAsync(_access, f.LabelId, ct)).UidValidity);
        });
        if (_sel?.Folder.LabelId == f.LabelId)
        {
            await RefreshAsync(allowExpunge: false, ct);
        }

        Ok(tag, $"[APPENDUID {validity} {uid}] APPEND completed");
    }

    // ---- IDLE (RFC 2177) ----

    private async Task IdleAsync(string tag, CancellationToken ct)
    {
        _w.Line("+ idling");
        await _w.FlushAsync(ct);
        using var idleEnd = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleEnd.CancelAfter(options.IdleTimeout);
        // Cancelled (not only disposed) on the way out: a still-pending wait on _mailboxChanged would otherwise stay
        // queued on the semaphore and swallow the next change signal, so the next IDLE would miss new mail.
        using var stopWaiting = new CancelOnExit(idleEnd);
        var done = _reader.ReadLineAsync(null, idleEnd.Token);
        while (true)
        {
            var changed = _mailboxChanged.WaitAsync(idleEnd.Token);
            var finished = await Task.WhenAny(done, changed);
            if (finished == done)
            {
                using var line = await done;
                if (line is null)
                {
                    throw new IOException("Connection closed during IDLE");
                }

                var text = Encoding.ASCII.GetString(line.Text[0]).Trim();
                if (!text.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ImapBadException("Expected DONE");
                }

                await RefreshAsync(allowExpunge: true, ct);
                Ok(tag, "IDLE terminated");
                return;
            }

            if (changed.IsCanceled || changed.IsFaulted)
            {
                // IDLE limit reached (RFC 2177: re-issue at least every 29 minutes); the server logs the client out.
                throw new OperationCanceledException();
            }

            if (_revoked)
            {
                throw new IOException("revoked");
            }

            await RefreshAsync(allowExpunge: true, ct);
            await _w.FlushAsync(ct);
        }
    }
}

internal readonly struct CancelOnExit(CancellationTokenSource source) : IDisposable
{
    public void Dispose() => source.Cancel();
}
