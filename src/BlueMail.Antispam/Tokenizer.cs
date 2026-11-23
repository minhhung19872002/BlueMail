using System.Globalization;
using System.Text;

namespace BlueMail.Antispam;

/// <summary>
/// Language-independent tokens for the Bayes classifier (PROMPT 2.3): Unicode NFC, case folded; words split on
/// Unicode letter/digit boundaries (UAX #29 word rules, simplified: a word is a run of letters, marks and digits,
/// with ' and . inside allowed); scripts written without spaces (CJK, Thai, Lao, Khmer, Myanmar) become bigrams;
/// Latin words also yield an accent-free variant so "khuyến mãi" and "khuyen mai" share evidence.
/// Header tokens are prefixed ("subj:", "from:", "url:") so the same word means different things in different places.
/// </summary>
public static class Tokenizer
{
    public const int MaxTokens = 3000;
    public const int MaxTokenLength = 40;

    public static IEnumerable<string> Tokens(string? text, string prefix = "")
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var s = text.Length > 200_000 ? text[..200_000] : text;
        s = s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var word = new StringBuilder();
        var run = new StringBuilder();
        var count = 0;

        foreach (var rune in s.EnumerateRunes())
        {
            if (count >= MaxTokens)
            {
                yield break;
            }

            if (IsUnspaced(rune))
            {
                foreach (var t in Flush(word, prefix))
                {
                    count++;
                    yield return t;
                }

                run.Append(rune.ToString());
                continue;
            }

            foreach (var t in FlushRun(run, prefix))
            {
                count++;
                yield return t;
            }

            var cat = Rune.GetUnicodeCategory(rune);
            var inWord = Rune.IsLetterOrDigit(rune) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                         || (word.Length > 0 && rune.Value is '\'' or '.' or '-' or '_');
            if (inWord)
            {
                word.Append(rune.ToString());
            }
            else
            {
                foreach (var t in Flush(word, prefix))
                {
                    count++;
                    yield return t;
                }
            }
        }

        foreach (var t in Flush(word, prefix).Concat(FlushRun(run, prefix)))
        {
            yield return t;
        }
    }

    private static IEnumerable<string> Flush(StringBuilder word, string prefix)
    {
        if (word.Length == 0)
        {
            yield break;
        }

        var w = word.ToString().Trim('.', '\'', '-', '_');
        word.Clear();
        if (w.Length is < 2 or > MaxTokenLength)
        {
            if (w.Length > MaxTokenLength)
            {
                yield return prefix + "skip:" + (w.Length / 10 * 10); // very long words: only their length class (SpamBayes)
            }

            yield break;
        }

        yield return prefix + w;
        var plain = RemoveMarks(w);
        if (plain != w)
        {
            yield return prefix + plain;
        }
    }

    private static IEnumerable<string> FlushRun(StringBuilder run, string prefix)
    {
        if (run.Length == 0)
        {
            yield break;
        }

        var text = run.ToString();
        run.Clear();
        var elements = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            elements.Add(e.GetTextElement());
        }

        if (elements.Count == 1)
        {
            yield return prefix + elements[0];
            yield break;
        }

        for (var i = 0; i < elements.Count - 1; i++)
        {
            yield return prefix + elements[i] + elements[i + 1];
        }
    }

    public static string RemoveMarks(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', 'ł' => 'l', 'ø' => 'o', 'ß' => 's', _ => c });
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsUnspaced(Rune r) => r.Value is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x0E00 and <= 0x0E7F or >= 0x0E80 and <= 0x0EFF or >= 0x1780 and <= 0x17FF
        or >= 0x1000 and <= 0x109F;

    /// <summary>Stable 64-bit token id (FNV-1a over UTF-8) - the database stores ids, never the words.</summary>
    public static long Id(string token)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var h = offset;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            h ^= b;
            h *= prime;
        }

        return unchecked((long)h);
    }
}

/// <summary>The Bayes evidence of a message: subject, body text, sender domain and link hosts, each with its own prefix.</summary>
public static class SpamTokens
{
    public static IEnumerable<string> Of(SpamInput i)
    {
        foreach (var t in Tokenizer.Tokens(i.Subject, "subj:"))
        {
            yield return t;
        }

        var body = string.IsNullOrWhiteSpace(i.TextBody) && i.HtmlBody is { } h ? StripTags(h) : i.TextBody;
        foreach (var t in Tokenizer.Tokens(body))
        {
            yield return t;
        }

        if (SpamRules.DomainOf(i.FromAddress) is { } d)
        {
            yield return "from:" + d;
            var parts = d.Split('.');
            if (parts.Length >= 2)
            {
                yield return "fromtld:" + parts[^1];
            }
        }

        if (!string.IsNullOrEmpty(i.FromName))
        {
            foreach (var t in Tokenizer.Tokens(i.FromName, "name:"))
            {
                yield return t;
            }
        }

        foreach (var (href, _) in i.Links)
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var u))
            {
                yield return "url:" + u.Host.ToLowerInvariant();
            }
        }

        if (i.Images > 0 && string.IsNullOrWhiteSpace(i.TextBody))
        {
            yield return "shape:image-only";
        }
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<')
            {
                inTag = true;
            }
            else if (c == '>')
            {
                inTag = false;
                sb.Append(' ');
            }
            else if (!inTag)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
