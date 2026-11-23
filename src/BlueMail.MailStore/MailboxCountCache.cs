using System.Collections.Concurrent;

namespace BlueMail.MailStore;

/// <summary>
/// Label counts and used bytes of a mailbox, remembered per <c>highest_modseq</c> (Phase 13). Every change that can move
/// a count - delivery, flag and label changes, expunge, label create/delete - takes a new modseq in the same
/// transaction, so an entry for the current modseq is exactly what a fresh count would return (PROMPT 14.7 still
/// holds). Concurrent requests for the same mailbox and modseq share one computation. One entry per mailbox; the
/// whole map is dropped above <see cref="MaxEntries"/> mailboxes.
/// </summary>
public sealed class MailboxCountCache
{
    public const int MaxEntries = 50_000;
    private readonly ConcurrentDictionary<(Guid Mailbox, string Kind), (long Modseq, Lazy<Task<object>> Value)> _entries = new();

    public async Task<T> GetAsync<T>(Guid mailbox, string kind, long modseq, Func<Task<T>> compute)
        where T : class
    {
        if (_entries.Count > MaxEntries)
        {
            _entries.Clear();
        }

        var key = (mailbox, kind);
        while (true)
        {
            if (_entries.TryGetValue(key, out var hit) && hit.Modseq == modseq)
            {
                try
                {
                    return (T)await hit.Value.Value;
                }
                catch
                {
                    _entries.TryRemove(new KeyValuePair<(Guid, string), (long, Lazy<Task<object>>)>(key, hit));
                    throw; // a failed computation is not remembered
                }
            }

            var fresh = (modseq, new Lazy<Task<object>>(async () => await compute()));
            if (hit.Value is null ? _entries.TryAdd(key, fresh) : hit.Modseq < modseq && _entries.TryUpdate(key, fresh, hit))
            {
                continue; // read it back through the shared Lazy
            }

            if (_entries.TryGetValue(key, out var newer) && newer.Modseq > modseq)
            {
                return await compute(); // this request saw an older modseq than another one: answer for its own
            }
        }
    }
}
