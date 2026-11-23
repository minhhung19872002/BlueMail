using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlueMail.MailStore;

/// <summary>
/// Reply/forward prefixes of many languages (PROMPT 0.2), stripped for threading and display. The list is data:
/// a deployment can extend it without code changes.
/// </summary>
public static partial class SubjectNormalizer
{
    public static readonly IReadOnlyList<string> DefaultPrefixes =
    [
        "re", "fw", "fwd", "aw", "wg", "rv", "tr", "sv", "vs", "odp", "pd", "res", "enc", "ynt", "ilt", "antw", "doorst", "vb", "ref", "rif", "r", "i",
        "回复", "答复", "回覆", "转发", "轉寄", "返信", "転送", "회신", "전달", "trả lời", "tl", "chuyển tiếp", "ct", "השב", "הועבר", "رد", "إعادة توجيه", "ответ", "пересл",
    ];

    private static readonly HashSet<string> Prefixes = new(DefaultPrefixes.Select(p => p.Normalize(NormalizationForm.FormC)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes any number of leading prefixes ("Re: Fwd: TL:", "RE[2]:", "Re -") and list tags ("[team]").</summary>
    public static string Normalize(string? subject, out bool hadPrefix)
    {
        hadPrefix = false;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "";
        }

        var s = SearchText.Clean(subject)!.Normalize(NormalizationForm.FormC).Trim();
        var changed = true;
        while (changed && s.Length > 0)
        {
            changed = false;
            var tag = ListTag().Match(s);
            if (tag.Success)
            {
                s = s[tag.Length..].TrimStart();
                changed = true;
            }

            var m = PrefixPattern().Match(s);
            if (m.Success && Prefixes.Contains(m.Groups["p"].Value.Trim()))
            {
                s = s[m.Length..].TrimStart();
                hadPrefix = true;
                changed = true;
            }
        }

        return Whitespace().Replace(s, " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"^\[[^\]]{1,40}\]\s*")]
    private static partial Regex ListTag();

    // "Re:", "RE[3]:", "Re -", "Trả lời:", "回复：" (full-width colon)
    [GeneratedRegex(@"^(?<p>[\p{L} ]{1,20}?)\s*(\[\d+\]|\(\d+\))?\s*[:：]\s*")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Text prepared for the PostgreSQL search vector (PROMPT 4, 0.2): Unicode NFC, case folded; runs of characters from
/// scripts written without spaces (CJK, Thai, Lao, Khmer, Myanmar) become overlapping bigrams so substrings are
/// searchable. Accents are removed in SQL by f_unaccent so query and index agree. Queries go through the same function.
/// </summary>
public static class SearchText
{
    public const int MaxBodyChars = 200_000;

    /// <summary>The "people" part of the search vector: names, addresses, local parts and domain words of From/To/Cc.</summary>
    public static string People(MessageFacts f) => string.Join(' ', new[] { f.From }.Concat(f.To).Concat(f.Cc).Where(a => a is not null)
        .SelectMany(a => new[] { a!.Name, a.Address, a.Address.Split('@')[0], a.Address.Contains('@', StringComparison.Ordinal) ? a.Address.Split('@')[1].Replace('.', ' ') : null }));

    public static string Prepare(string? text, int maxChars = MaxBodyChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var s = text.Length > maxChars ? text[..maxChars] : text;
        s = Clean(s)!.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length + 16);
        var run = new StringBuilder();
        void FlushRun()
        {
            if (run.Length == 0)
            {
                return;
            }

            var r = run.ToString();
            sb.Append(' ');
            if (r.Length == 1)
            {
                sb.Append(r);
            }
            else
            {
                for (var i = 0; i < r.Length - 1; i++)
                {
                    sb.Append(r, i, 2).Append(' ');
                }
            }

            sb.Append(' ');
            run.Clear();
        }

        foreach (var rune in s.EnumerateRunes())
        {
            if (IsUnspacedScript(rune))
            {
                run.Append(rune.ToString());
                continue;
            }

            FlushRun();
            sb.Append(Rune.IsLetterOrDigit(rune) ? rune.ToString() : " ");
        }

        FlushRun();
        return sb.ToString();
    }

    private static bool IsUnspacedScript(Rune r)
    {
        var v = r.Value;
        return v is >= 0x3040 and <= 0x30FF       // Hiragana, Katakana
            or >= 0x3400 and <= 0x4DBF            // CJK Ext A
            or >= 0x4E00 and <= 0x9FFF            // CJK Unified
            or >= 0xF900 and <= 0xFAFF            // CJK Compatibility
            or >= 0x20000 and <= 0x2FA1F          // CJK Ext B+
            or >= 0x0E00 and <= 0x0E7F            // Thai
            or >= 0x0E80 and <= 0x0EFF            // Lao
            or >= 0x1780 and <= 0x17FF            // Khmer
            or >= 0x1000 and <= 0x109F;           // Myanmar
    }

    /// <summary>Text cut to at most <paramref name="max"/> text elements (never inside a grapheme cluster).</summary>
    public static string Snippet(string text, int max = 200)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var e = StringInfo.GetTextElementEnumerator(collapsed);
        var sb = new StringBuilder();
        var count = 0;
        while (e.MoveNext() && count++ < max)
        {
            sb.Append(e.GetTextElement());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes U+0000 and lone surrogates (PostgreSQL text cannot store them, PROMPT 8.1, 14.22) and Unicode
    /// noncharacters (U+FDD0..U+FDEF, U+xxFFFE/U+xxFFFF), on which string.Normalize throws (BUG-020).
    /// </summary>
    public static string? Clean(string? s)
    {
        if (s is null)
        {
            return null;
        }

        if (!s.Any(c => c == '\0' || char.IsSurrogate(c) || c is (>= '﷐' and <= '﷯') or '￾' or '￿'))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value != 0 && r != Rune.ReplacementChar && !IsNoncharacter(r.Value))
            {
                sb.Append(r.ToString());
            }
        }

        return sb.ToString();
    }

    private static bool IsNoncharacter(int v) => v is >= 0xFDD0 and <= 0xFDEF || (v & 0xFFFE) == 0xFFFE;
}
