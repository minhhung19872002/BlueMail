using System.Globalization;
using System.Text;
using BlueMail.Domain.Mail;
using BlueMail.MailStore;
using BlueMail.Protocols.Imap;

namespace BlueMail.Imap;

internal sealed partial class ImapSession
{
    /// <summary>Rows addressed by a sequence set: by message sequence number or (UID commands) by UID.</summary>
    private List<(int Seq, ImapMessageRow Row)> Targets(ImapSequenceSet set, bool uid)
    {
        var rows = _sel!.Rows;
        var list = new List<(int, ImapMessageRow)>();
        if (rows.Count == 0)
        {
            return list;
        }

        var maxUid = rows[^1].Uid;
        for (var i = 0; i < rows.Count; i++)
        {
            if (uid ? set.Contains(rows[i].Uid, maxUid) : set.Contains(i + 1, rows.Count))
            {
                list.Add((i + 1, rows[i]));
            }
        }

        if (!uid && set.Ranges.Any(r => r.From != long.MaxValue && r.From > rows.Count))
        {
            throw new ImapBadException("Invalid message sequence number"); // RFC 3501 6.4.5: an unknown sequence number is an error
        }

        return list;
    }

    // ---- FETCH (RFC 3501 6.4.5, CONDSTORE CHANGEDSINCE, QRESYNC VANISHED) ----

    private sealed record FetchItem(string Name, string? Section = null, long? PartialStart = null, long? PartialLength = null, bool Peek = false);

    private static List<FetchItem> ParseFetchItems(ImapValue v)
    {
        var values = v is ImapList l ? l.Items : [v];
        var items = new List<FetchItem>();
        foreach (var x in values)
        {
            var atom = (x.AsText() ?? throw new ImapBadException("Invalid fetch item")).ToUpperInvariant();
            switch (atom)
            {
                case "ALL":
                    items.AddRange([new("FLAGS"), new("INTERNALDATE"), new("RFC822.SIZE"), new("ENVELOPE")]);
                    continue;
                case "FAST":
                    items.AddRange([new("FLAGS"), new("INTERNALDATE"), new("RFC822.SIZE")]);
                    continue;
                case "FULL":
                    items.AddRange([new("FLAGS"), new("INTERNALDATE"), new("RFC822.SIZE"), new("ENVELOPE"), new("BODY")]);
                    continue;
            }

            if (atom.StartsWith("BODY[", StringComparison.Ordinal) || atom.StartsWith("BODY.PEEK[", StringComparison.Ordinal) || atom.StartsWith("BINARY", StringComparison.Ordinal))
            {
                var peek = atom.StartsWith("BODY.PEEK", StringComparison.Ordinal);
                var original = x.AsText()!;
                var open = original.IndexOf('[', StringComparison.Ordinal);
                var close = original.LastIndexOf(']');
                if (close < open || atom.StartsWith("BINARY", StringComparison.Ordinal))
                {
                    throw new ImapBadException("Invalid section");
                }

                var section = original[(open + 1)..close];
                long? start = null, length = null;
                var partial = original[(close + 1)..];
                if (partial.Length > 0)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(partial, @"^<(\d+)\.(\d+)>$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
                    if (!m.Success)
                    {
                        throw new ImapBadException("Invalid partial");
                    }

                    start = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    length = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }

                items.Add(new FetchItem("BODY", section, start, length, peek));
                continue;
            }

            if (atom is "UID" or "FLAGS" or "INTERNALDATE" or "RFC822.SIZE" or "ENVELOPE" or "BODY" or "BODYSTRUCTURE" or "RFC822" or "RFC822.HEADER" or "RFC822.TEXT"
                or "MODSEQ")
            {
                items.Add(new FetchItem(atom));
                continue;
            }

            throw new ImapBadException("Unknown fetch item " + atom);
        }

        return items;
    }

    private async Task FetchAsync(string tag, bool uid, ImapParser p, CancellationToken ct)
    {
        var set = SequenceSet(p);
        p.SkipSpaces();
        var items = ParseFetchItems(p.ReadValue(stopAtBracket: false));
        long? changedSince = null;
        var vanished = false;
        p.SkipSpaces();
        if (!p.AtEnd)
        {
            var mods = p.ReadValue(stopAtBracket: false).AsList();
            for (var i = 0; i < mods.Count; i++)
            {
                switch ((mods[i].AsText() ?? "").ToUpperInvariant())
                {
                    case "CHANGEDSINCE" when i + 1 < mods.Count:
                        changedSince = mods[++i].AsNumber() ?? throw new ImapBadException("Invalid CHANGEDSINCE");
                        _condstore = true;
                        break;
                    case "VANISHED" when uid && _qresync:
                        vanished = true;
                        break;
                    default:
                        throw new ImapBadException("Unknown FETCH modifier");
                }
            }
        }

        End(p);
        if (vanished && changedSince is null)
        {
            throw new ImapBadException("VANISHED needs CHANGEDSINCE");
        }

        if (uid && items.All(i => i.Name != "UID"))
        {
            items.Insert(0, new FetchItem("UID"));
        }

        if ((_condstore || changedSince is not null) && items.All(i => i.Name != "MODSEQ"))
        {
            items.Add(new FetchItem("MODSEQ"));
        }

        if (vanished && changedSince is { } cs)
        {
            var changes = await WithStoreAsync(s => s.ImapChangesAsync(_access!, _sel!.Folder.LabelId, cs, ct));
            var present = _sel!.Rows.Select(r => r.Uid).ToHashSet();
            var max = _sel.Rows.Count == 0 ? 0 : _sel.Rows[^1].Uid;
            var gone = changes.Where(c => c.Change == "REMOVED" && !present.Contains(c.Uid) && set.Contains(c.Uid, Math.Max(max, c.Uid))).Select(c => (uint)c.Uid).Distinct().ToList();
            if (gone.Count > 0)
            {
                _w.Line("* VANISHED (EARLIER) " + ImapArgs.UidSet(gone));
            }
        }

        var targets = Targets(set, uid).Where(t => changedSince is null || t.Row.Modseq > changedSince).ToList();
        var needsBody = items.Any(i => i.Name is "ENVELOPE" or "BODY" or "BODYSTRUCTURE" or "RFC822" or "RFC822.HEADER" or "RFC822.TEXT");
        var markSeen = !_sel!.ReadOnly && items.Any(i => (i.Name == "BODY" && i.Section is not null && !i.Peek) || i.Name is "RFC822" or "RFC822.TEXT");
        if (markSeen)
        {
            var unseen = targets.Where(t => !t.Row.Seen).Select(t => t.Row.MessageId).ToList();
            if (unseen.Count > 0)
            {
                await WithStoreAsync(s => s.ApplyAsync(_access!, unseen, new MessageChange { Seen = true }, ct));
                var fresh = (await WithStoreAsync(s => s.ImapRowsAsync(_access!, _sel.Folder.LabelId, targets.Select(t => t.Row.Uid).ToList(), ct))).ToDictionary(r => r.Uid);
                for (var i = 0; i < targets.Count; i++)
                {
                    if (fresh.TryGetValue(targets[i].Row.Uid, out var f))
                    {
                        targets[i] = (targets[i].Seq, f);
                        _sel.Rows[targets[i].Seq - 1] = f;
                    }
                }

                if (items.All(i => i.Name != "FLAGS"))
                {
                    items.Add(new FetchItem("FLAGS")); // RFC 3501 6.4.5: the \Seen change is reported
                }
            }
        }

        AnnounceKeywords(targets.Select(t => t.Row));
        var unreadable = 0;
        var lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (seq, row) in targets)
        {
            // A long FETCH must not look like a dead server: Outlook gives up after a minute without a byte (BUG-078).
            var quiet = System.Diagnostics.Stopwatch.GetElapsedTime(lastSent);
            if (quiet > KeepAliveAfter || (_w.Pending > 0 && quiet > FlushAfter))
            {
                if (_w.Pending == 0)
                {
                    _w.Line("* OK Still here");
                }

                await _w.FlushAsync(ct);
                lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            ImapMessageFormat? msg = null;
            if (needsBody)
            {
                using var ms = new MemoryStream();
                try
                {
                    await WithStoreAsync(s => s.CopyRawAsync(_access!, row.MessageId, ms, ct));
                }
                catch (Exception e) when (IsStorageFailure(e))
                {
                    // The content of this message cannot be read from storage (BUG-078). Nothing of it has been written
                    // yet: leave it out, answer the others, and end with NO so the client knows and asks again later.
                    unreadable++;
                    log.LogError(e, "IMAP {User}: content of message {MessageId} could not be read", _login?.Email ?? "-", row.MessageId);
                    continue;
                }

                msg = new ImapMessageFormat(ms.ToArray());
            }

            _w.Raw($"* {seq} FETCH (");
            var first = true;
            foreach (var item in items)
            {
                _w.Raw(first ? "" : " ");
                first = false;
                WriteItem(item, row, msg);
            }

            _w.Raw(")\r\n");
            if (_w.Pending > 1024 * 1024)
            {
                await _w.FlushAsync(ct);
                lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        if (!uid)
        {
            // FETCH by sequence number must not be followed by EXPUNGE (RFC 3501 7.4.1); new mail is fine.
            await RefreshAsync(allowExpunge: false, ct);
        }
        else
        {
            await RefreshAsync(allowExpunge: true, ct);
        }

        if (unreadable > 0)
        {
            _w.Line(tag + " NO [UNAVAILABLE] " + unreadable.ToString(CultureInfo.InvariantCulture) + " message(s) could not be read from storage");
            return;
        }

        Ok(tag, (uid ? "UID " : "") + "FETCH completed");
    }

    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveAfter = TimeSpan.FromSeconds(10);

    private void WriteItem(FetchItem item, ImapMessageRow row, ImapMessageFormat? msg)
    {
        switch (item.Name)
        {
            case "UID":
                _w.Raw("UID " + row.Uid.ToString(CultureInfo.InvariantCulture));
                return;
            case "FLAGS":
                _w.Raw("FLAGS (" + Flags(row) + ")");
                return;
            case "MODSEQ":
                _w.Raw("MODSEQ (" + row.Modseq.ToString(CultureInfo.InvariantCulture) + ")");
                return;
            case "INTERNALDATE":
                _w.Raw("INTERNALDATE \"" + row.ReceivedAt.ToUniversalTime().ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000\"");
                return;
            case "RFC822.SIZE":
                _w.Raw("RFC822.SIZE " + row.Size.ToString(CultureInfo.InvariantCulture));
                return;
            case "ENVELOPE":
                _w.Raw("ENVELOPE ");
                msg!.Envelope(_w);
                return;
            case "BODY" when item.Section is null:
                _w.Raw("BODY ");
                msg!.BodyStructure(_w, extensible: false);
                return;
            case "BODYSTRUCTURE":
                _w.Raw("BODYSTRUCTURE ");
                msg!.BodyStructure(_w, extensible: true);
                return;
            case "RFC822":
                _w.Raw("RFC822 ").Literal(msg!.Raw);
                return;
            case "RFC822.HEADER":
                _w.Raw("RFC822.HEADER ").Literal(msg!.Section("HEADER") ?? []);
                return;
            case "RFC822.TEXT":
                _w.Raw("RFC822.TEXT ").Literal(msg!.Section("TEXT") ?? []);
                return;
            default:
                {
                    var data = msg!.Section(item.Section!) ?? [];
                    var name = "BODY[" + NormalizeSection(item.Section!) + "]";
                    if (item.PartialStart is { } start)
                    {
                        name += "<" + start.ToString(CultureInfo.InvariantCulture) + ">";
                        var from = (int)Math.Min(start, data.LongLength);
                        var len = (int)Math.Min(item.PartialLength ?? 0, data.LongLength - from);
                        data = data.AsSpan(from, len).ToArray();
                    }

                    _w.Raw(name + " ").Literal(data);
                    return;
                }
        }
    }

    /// <summary>The section as echoed in the response: upper-case keywords, field names kept, single spaces.</summary>
    private static string NormalizeSection(string section)
    {
        var open = section.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return section.ToUpperInvariant();
        }

        var head = section[..open].Trim().ToUpperInvariant();
        var fields = section[(open + 1)..section.LastIndexOf(')')].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return head + " (" + string.Join(' ', fields.Select(f => f.Trim('"').ToUpperInvariant())) + ")";
    }

    // ---- STORE (RFC 3501 6.4.6, CONDSTORE UNCHANGEDSINCE) ----

    private async Task StoreAsync(string tag, bool uid, ImapParser p, CancellationToken ct)
    {
        RequireWritable();
        var set = SequenceSet(p);
        p.SkipSpaces();
        long? unchangedSince = null;
        if (p.Peek() == '(')
        {
            var mods = p.ReadValue(stopAtBracket: false).AsList();
            if (mods.Count != 2 || !(mods[0].AsText() ?? "").Equals("UNCHANGEDSINCE", StringComparison.OrdinalIgnoreCase) || mods[1].AsNumber() is not { } us)
            {
                throw new ImapBadException("Unknown STORE modifier");
            }

            unchangedSince = us;
            _condstore = true;
        }

        p.SkipSpaces();
        var op = p.ReadAtom(stopAtBracket: false).ToUpperInvariant();
        var silent = op.EndsWith(".SILENT", StringComparison.Ordinal);
        op = silent ? op[..^7] : op;
        if (op is not ("FLAGS" or "+FLAGS" or "-FLAGS"))
        {
            throw new ImapBadException("Invalid STORE operation");
        }

        p.SkipSpaces();
        var value = p.ReadValue(stopAtBracket: false);
        var flags = (value is ImapList l ? l.Items : [value, .. p.ReadAll()]).Select(v => v is ImapNil ? "NIL" : v.AsText() ?? "").Where(f => f.Length > 0).ToList();
        End(p);
        if (flags.Any(f => f.Equals("\\Recent", StringComparison.OrdinalIgnoreCase) || (f.StartsWith('\\') && !KnownFlags.Contains(f))))
        {
            throw new ImapBadException("Invalid flag");
        }

        var targets = Targets(set, uid);
        var modified = targets.Where(t => unchangedSince is { } u && t.Row.Modseq > u).ToList();
        targets = targets.Except(modified).ToList();
        if (targets.Count > 0)
        {
            var has = (string f) => flags.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
            var keywords = flags.Where(f => !f.StartsWith('\\') && !f.Equals("$Forwarded", StringComparison.OrdinalIgnoreCase)).ToList();
            bool? Set(string f) => op switch { "+FLAGS" => has(f) ? true : null, "-FLAGS" => has(f) ? false : null, _ => has(f) };
            var change = new MessageChange
            {
                Seen = Set("\\Seen"), Starred = Set("\\Flagged"), Answered = Set("\\Answered"), Draft = Set("\\Draft"), Forwarded = Set("$Forwarded"),
                AddKeywords = op == "-FLAGS" ? [] : keywords,
                RemoveKeywords = op switch
                {
                    "-FLAGS" => keywords,
                    "FLAGS" => targets.SelectMany(t => t.Row.Keywords).Distinct().Except(keywords).ToList(),
                    _ => [],
                },
                DeletedIn = Set("\\Deleted") is { } d ? (_sel!.Folder.LabelId, d) : null,
            };
            if (_sel!.Folder.SystemKey == SystemLabels.Starred && change.Starred == false)
            {
                change = change with { Starred = false }; // un-flagging in "Starred" removes it from the folder, as in Gmail
            }

            await WithStoreAsync(s => s.ApplyAsync(_access!, targets.Select(t => t.Row.MessageId).ToList(), change, ct));
            var fresh = (await WithStoreAsync(s => s.ImapRowsAsync(_access!, _sel.Folder.LabelId, targets.Select(t => t.Row.Uid).ToList(), ct))).ToDictionary(r => r.Uid);
            AnnounceKeywords(fresh.Values);
            foreach (var (seq, row) in targets)
            {
                if (!fresh.TryGetValue(row.Uid, out var f))
                {
                    continue; // left the folder (unstarred in Starred): reported as EXPUNGE by the refresh
                }

                _sel.Rows[seq - 1] = f;
                if (!silent || (unchangedSince is not null))
                {
                    _w.Line($"* {seq} FETCH ({(uid ? $"UID {f.Uid} " : "")}FLAGS ({Flags(f)}){(_condstore ? $" MODSEQ ({f.Modseq})" : "")})");
                }
            }
        }

        await RefreshAsync(allowExpunge: uid, ct);
        var modifiedCode = modified.Count > 0 ? $"[MODIFIED {ImapArgs.UidSet(modified.Select(m => (uint)(uid ? m.Row.Uid : m.Seq)))}] " : "";
        Ok(tag, modifiedCode + (uid ? "UID " : "") + "STORE completed");
    }

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase) { "\\Seen", "\\Answered", "\\Flagged", "\\Deleted", "\\Draft" };

    // ---- COPY / MOVE (RFC 3501 6.4.7, RFC 6851, UIDPLUS COPYUID) ----

    private async Task CopyAsync(string tag, bool uid, bool move, ImapParser p, CancellationToken ct)
    {
        var set = SequenceSet(p);
        var name = MailboxName(p);
        End(p);
        if (move)
        {
            RequireWritable();
        }

        var dest = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "TRYCREATE");
        var targets = Targets(set, uid);
        if (targets.Count == 0)
        {
            Ok(tag, (uid ? "UID " : "") + (move ? "MOVE" : "COPY") + " completed");
            return;
        }

        var ids = targets.Select(t => t.Row.MessageId).ToList();
        var src = _sel!.Folder;
        ObserveFolder(dest.LabelId, (await WithStoreAsync(s => s.ImapStatusAsync(_access!, dest.LabelId, ct))).UidNext - 1);
        if (move && dest.LabelId == src.LabelId)
        {
            // MOVE onto itself: out and back in, so the messages get new UIDs (and old ones are expunged).
            await WithStoreAsync(s => s.ApplyAsync(_access!, ids, Remove(new MessageChange(), src), ct));
            await WithStoreAsync(s => s.ApplyAsync(_access!, ids, AddTo(dest), ct));
        }
        else
        {
            var change = AddTo(dest);
            if (move)
            {
                change = Remove(change, src);
            }

            await WithStoreAsync(s => s.ApplyAsync(_access!, ids, change, ct));
        }

        var (map, validity) = await WithStoreAsync(async s => (await s.ImapUidsOfAsync(_access!, dest.LabelId, ids, ct), (await s.ImapStatusAsync(_access!, dest.LabelId, ct)).UidValidity));
        var pairs = targets.Where(t => map.ContainsKey(t.Row.MessageId)).Select(t => ((uint)t.Row.Uid, (uint)map[t.Row.MessageId])).ToList();
        var code = pairs.Count == 0 ? "" : $"[COPYUID {validity} {string.Join(',', pairs.Select(x => x.Item1))} {string.Join(',', pairs.Select(x => x.Item2))}] ";
        if (move)
        {
            _w.Line("* OK " + code.TrimEnd() + " Moved"); // RFC 6851 4.3: COPYUID in an untagged OK, then EXPUNGE
            await RefreshAsync(allowExpunge: true, ct);
            Ok(tag, (uid ? "UID " : "") + "MOVE completed");
            return;
        }

        await RefreshAsync(allowExpunge: uid, ct);
        Ok(tag, code + (uid ? "UID " : "") + "COPY completed");
    }

    /// <summary>Adding a message to a folder: its label (Starred and Important are flags too).</summary>
    private static MessageChange AddTo(Folder f) => f.SystemKey switch
    {
        SystemLabels.Starred => new MessageChange { Starred = true },
        SystemLabels.Important => new MessageChange { Important = true },
        { } key => new MessageChange { AddSystem = [key], RemoveSystem = key is SystemLabels.Trash or SystemLabels.Spam ? [SystemLabels.Inbox] : [] },
        null => new MessageChange { AddLabels = [f.LabelId] },
    };

    private static MessageChange Remove(MessageChange c, Folder f) => f.SystemKey switch
    {
        SystemLabels.Starred => c with { Starred = false },
        SystemLabels.Important => c with { Important = false },
        { } key => c with { RemoveSystem = [.. c.RemoveSystem, key] },
        null => c with { RemoveLabels = [.. c.RemoveLabels, f.LabelId] },
    };

    // ---- EXPUNGE / CLOSE ----

    /// <summary>
    /// Messages marked \Deleted here leave the folder: in Trash and Spam they are deleted for good (the store keeps what
    /// legal holds and retention keep, docs/00 D22); elsewhere the label is removed, like Gmail's IMAP.
    /// </summary>
    private async Task ExpungeCoreAsync(ImapSequenceSet? uidSet, bool report, CancellationToken ct)
    {
        var sel = _sel!;
        var max = sel.Rows.Count == 0 ? 0 : sel.Rows[^1].Uid;
        var doomed = sel.Rows.Where(r => r.Deleted && (uidSet is null || uidSet.Contains(r.Uid, max))).Select(r => r.MessageId).ToList();
        if (doomed.Count > 0)
        {
            var change = sel.Folder.IsTrashOrSpam ? new MessageChange { Expunge = true } : Remove(new MessageChange(), sel.Folder);
            await WithStoreAsync(s => s.ApplyAsync(_access!, doomed, change, ct));
        }

        if (report)
        {
            await RefreshAsync(allowExpunge: true, ct);
        }
    }
}
