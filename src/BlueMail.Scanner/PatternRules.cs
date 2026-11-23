using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace BlueMail.Scanner;

/// <summary>
/// Layer 8 (PROMPT 2.6.4): byte-pattern rules the operations team can add quickly during an attack wave.
/// Syntax (one or more rules per file, "#" comments):
/// <code>
/// rule powershell_encoded : suspicious
///   text $a = "powershell" nocase
///   text $b = "-encodedcommand" nocase wide
///   hex  $c = 4D 5A ?? 00 [0-16] 50 45
///   when $a and ($b or $c at 0) and filesize &lt; 2MB
/// end
/// </code>
/// Conditions: and, or, not, parentheses, $id, $id at N, any, all, N of them, filesize (&lt; &lt;= &gt; &gt;= ==) N[KB|MB].
/// Severity: suspicious | malicious. Matching reads at most <see cref="PatternRuleSet.MaxScanBytes"/> per file.
/// </summary>
public sealed class PatternRuleSet
{
    public const int MaxScanBytes = 16 * 1024 * 1024;

    public IReadOnlyList<PatternRule> Rules { get; }

    private PatternRuleSet(IReadOnlyList<PatternRule> rules) => Rules = rules;

    public static PatternRuleSet Empty { get; } = new([]);

    public static PatternRuleSet Parse(string text, string source = "rules")
    {
        var rules = new List<PatternRule>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        PatternRuleBuilder? cur = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string Where() => $"{source}:{i + 1}";
            if (line.StartsWith("rule ", StringComparison.Ordinal))
            {
                if (cur is not null)
                {
                    throw new FormatException($"{Where()}: rule '{cur.Name}' has no 'end'");
                }

                var head = line[5..].Split(':', 2, StringSplitOptions.TrimEntries);
                if (head.Length != 2 || head[0].Length == 0 || !head[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    throw new FormatException($"{Where()}: expected 'rule <name> : suspicious|malicious'");
                }

                var sev = head[1] switch
                {
                    "suspicious" => ScanStatus.Suspicious,
                    "malicious" => ScanStatus.Malicious,
                    _ => throw new FormatException($"{Where()}: severity must be suspicious or malicious"),
                };
                cur = new PatternRuleBuilder(head[0], sev);
            }
            else if (cur is null)
            {
                throw new FormatException($"{Where()}: statement outside a rule");
            }
            else if (line.StartsWith("text ", StringComparison.Ordinal) || line.StartsWith("hex ", StringComparison.Ordinal))
            {
                cur.Strings.Add(ParseString(line, Where()));
            }
            else if (line.StartsWith("when ", StringComparison.Ordinal))
            {
                cur.Condition = new ConditionParser(line[5..], Where(), cur.Strings.Select(s => s.Id).ToHashSet()).Parse();
            }
            else if (line == "end")
            {
                if (cur.Condition is null || cur.Strings.Count == 0)
                {
                    throw new FormatException($"{Where()}: rule '{cur.Name}' needs strings and a 'when' condition");
                }

                rules.Add(new PatternRule(cur.Name, cur.Severity, cur.Strings, cur.Condition));
                cur = null;
            }
            else
            {
                throw new FormatException($"{Where()}: unknown statement '{line}'");
            }
        }

        if (cur is not null)
        {
            throw new FormatException($"{source}: rule '{cur.Name}' has no 'end'");
        }

        var dup = rules.GroupBy(r => r.Name).FirstOrDefault(g => g.Count() > 1);
        return dup is null ? new PatternRuleSet(rules) : throw new FormatException($"{source}: rule name '{dup.Key}' used twice");
    }

    public PatternRuleSet Merge(PatternRuleSet other) => new([.. Rules.Where(r => other.Rules.All(o => o.Name != r.Name)), .. other.Rules]);

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && inQuote)
            {
                i++;
            }
            else if (line[i] == '"')
            {
                inQuote = !inQuote;
            }
            else if (line[i] == '#' && !inQuote)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static PatternString ParseString(string line, string where)
    {
        var isHex = line.StartsWith("hex ", StringComparison.Ordinal);
        var rest = line[(isHex ? 4 : 5)..].Trim();
        var eq = rest.IndexOf('=');
        if (eq < 2 || rest[0] != '$')
        {
            throw new FormatException($"{where}: expected '$id = ...'");
        }

        var id = rest[1..eq].Trim();
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new FormatException($"{where}: bad identifier");
        }

        var value = rest[(eq + 1)..].Trim();
        if (!isHex)
        {
            if (value.Length < 2 || value[0] != '"')
            {
                throw new FormatException($"{where}: text needs a quoted value");
            }

            var end = 1;
            var sb = new StringBuilder();
            for (; end < value.Length && value[end] != '"'; end++)
            {
                if (value[end] == '\\' && end + 1 < value.Length)
                {
                    end++;
                    sb.Append(value[end] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var c => c });
                }
                else
                {
                    sb.Append(value[end]);
                }
            }

            if (end >= value.Length)
            {
                throw new FormatException($"{where}: unterminated string");
            }

            var mods = value[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mods.Any(m => m is not ("nocase" or "wide")))
            {
                throw new FormatException($"{where}: modifiers are nocase and wide");
            }

            var text = sb.ToString();
            if (text.Length == 0)
            {
                throw new FormatException($"{where}: empty string");
            }

            var bytes = mods.Contains("wide") ? Encoding.Unicode.GetBytes(text) : Encoding.UTF8.GetBytes(text);
            return new PatternString(id, bytes.Select(b => new PatternToken(b, false, 0, 0)).ToArray(), mods.Contains("nocase"));
        }

        var tokens = new List<PatternToken>();
        foreach (var t in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t == "??")
            {
                tokens.Add(new PatternToken(0, true, 0, 0));
            }
            else if (t.StartsWith('[') && t.EndsWith(']'))
            {
                var r = t[1..^1].Split('-');
                if (r.Length != 2 || !int.TryParse(r[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min) ||
                    !int.TryParse(r[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max) || min > max || max > 4096 || tokens.Count == 0)
                {
                    throw new FormatException($"{where}: jump must be [min-max] with max ≤ 4096, not first");
                }

                tokens.Add(new PatternToken(0, true, min, max));
            }
            else if (t.Length == 2 && byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                tokens.Add(new PatternToken(b, false, 0, 0));
            }
            else
            {
                throw new FormatException($"{where}: bad hex token '{t}'");
            }
        }

        if (tokens.Count == 0 || tokens[0].Wildcard || tokens[^1].Wildcard)
        {
            throw new FormatException($"{where}: hex pattern must start and end with a byte");
        }

        return new PatternString(id, [.. tokens], false);
    }

    private sealed class PatternRuleBuilder(string name, ScanStatus severity)
    {
        public string Name { get; } = name;
        public ScanStatus Severity { get; } = severity;
        public List<PatternString> Strings { get; } = [];
        public Condition? Condition { get; set; }
    }

    /// <summary>The rules that match <paramref name="data"/> (the first <see cref="MaxScanBytes"/> of a file).</summary>
    public IEnumerable<PatternRule> Match(ReadOnlySpan<byte> data, long fileSize)
    {
        var hits = new List<PatternRule>();
        foreach (var rule in Rules)
        {
            var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var s in rule.Strings)
            {
                found[s.Id] = s.FindAll(data, 64);
            }

            if (rule.Condition.Eval(found, fileSize))
            {
                hits.Add(rule);
            }
        }

        return hits;
    }
}

public readonly record struct PatternToken(byte Value, bool Wildcard, int MinJump, int MaxJump)
{
    public bool IsJump => Wildcard && MaxJump > 0;
}

public sealed record PatternString(string Id, PatternToken[] Tokens, bool NoCase)
{
    public List<int> FindAll(ReadOnlySpan<byte> data, int limit)
    {
        var result = new List<int>();
        var first = Tokens[0].Value;
        for (var i = 0; i < data.Length && result.Count < limit; i++)
        {
            if (!Eq(data[i], first))
            {
                continue;
            }

            if (MatchAt(data, i, 1, i + 1))
            {
                result.Add(i);
            }
        }

        return result;
    }

    private bool Eq(byte a, byte b) => a == b || (NoCase && a < 128 && b < 128 && char.ToLowerInvariant((char)a) == char.ToLowerInvariant((char)b));

    private bool MatchAt(ReadOnlySpan<byte> data, int start, int token, int pos)
    {
        for (var t = token; t < Tokens.Length; t++)
        {
            var tok = Tokens[t];
            if (tok.IsJump)
            {
                for (var skip = tok.MinJump; skip <= tok.MaxJump; skip++)
                {
                    if (pos + skip <= data.Length && MatchAt(data, start, t + 1, pos + skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pos >= data.Length || (!tok.Wildcard && !Eq(data[pos], tok.Value)))
            {
                return false;
            }

            pos++;
        }

        return true;
    }
}

public sealed record PatternRule(string Name, ScanStatus Severity, IReadOnlyList<PatternString> Strings, Condition Condition);

public abstract record Condition
{
    public abstract bool Eval(IReadOnlyDictionary<string, List<int>> found, long fileSize);
}

internal sealed record AndCond(Condition L, Condition R) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => L.Eval(f, s) && R.Eval(f, s);
}

internal sealed record OrCond(Condition L, Condition R) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => L.Eval(f, s) || R.Eval(f, s);
}

internal sealed record NotCond(Condition C) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => !C.Eval(f, s);
}

internal sealed record StringCond(string Id, int? At) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => At is { } a ? f[Id].Contains(a) : f[Id].Count > 0;
}

internal sealed record CountCond(int N) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => f.Values.Count(v => v.Count > 0) >= (N < 0 ? f.Count : N);
}

internal sealed record SizeCond(string Op, long Bytes) : Condition
{
    public override bool Eval(IReadOnlyDictionary<string, List<int>> f, long s) => Op switch
    {
        "<" => s < Bytes, "<=" => s <= Bytes, ">" => s > Bytes, ">=" => s >= Bytes, _ => s == Bytes,
    };
}

/// <summary>Recursive-descent parser for 'when' conditions: or &lt; and &lt; not &lt; atoms.</summary>
internal sealed class ConditionParser(string text, string where, IReadOnlySet<string> ids)
{
    private readonly List<string> _t = Tokenize(text);
    private int _i;

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        for (var i = 0; i < s.Length;)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            else if (s[i] is '(' or ')')
            {
                tokens.Add(s[i].ToString());
                i++;
            }
            else if (s[i] is '<' or '>' or '=')
            {
                var two = i + 1 < s.Length && s[i + 1] == '=';
                tokens.Add(s.Substring(i, two ? 2 : 1));
                i += two ? 2 : 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '<' or '>' or '='))
                {
                    i++;
                }

                tokens.Add(s[start..i]);
            }
        }

        return tokens;
    }

    private string? Peek => _i < _t.Count ? _t[_i] : null;

    private string Next() => _i < _t.Count ? _t[_i++] : throw new FormatException($"{where}: condition ends too early");

    public Condition Parse()
    {
        var c = Or();
        return Peek is null ? c : throw new FormatException($"{where}: unexpected '{Peek}' in condition");
    }

    private Condition Or()
    {
        var c = And();
        while (Peek == "or")
        {
            Next();
            c = new OrCond(c, And());
        }

        return c;
    }

    private Condition And()
    {
        var c = Not();
        while (Peek == "and")
        {
            Next();
            c = new AndCond(c, Not());
        }

        return c;
    }

    private Condition Not()
    {
        if (Peek == "not")
        {
            Next();
            return new NotCond(Not());
        }

        return Atom();
    }

    private Condition Atom()
    {
        var t = Next();
        if (t == "(")
        {
            var c = Or();
            return Next() == ")" ? c : throw new FormatException($"{where}: missing ')'");
        }

        if (t == "any")
        {
            return new CountCond(1);
        }

        if (t == "all")
        {
            return new CountCond(-1);
        }

        if (t == "filesize")
        {
            var op = Next();
            if (op is not ("<" or "<=" or ">" or ">=" or "=="))
            {
                throw new FormatException($"{where}: filesize needs a comparison");
            }

            return new SizeCond(op, Size(Next()));
        }

        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return Next() == "of" && Next() == "them" ? new CountCond(n) : throw new FormatException($"{where}: expected 'N of them'");
        }

        if (t.StartsWith('$'))
        {
            var id = t[1..];
            if (!ids.Contains(id))
            {
                throw new FormatException($"{where}: unknown string ${id}");
            }

            if (Peek == "at")
            {
                Next();
                return int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? new StringCond(id, at) : throw new FormatException($"{where}: 'at' needs an offset");
            }

            return new StringCond(id, null);
        }

        throw new FormatException($"{where}: unexpected '{t}' in condition");
    }

    private long Size(string s)
    {
        long mul = 1;
        if (s.EndsWith("KB", StringComparison.Ordinal))
        {
            mul = 1024;
            s = s[..^2];
        }
        else if (s.EndsWith("MB", StringComparison.Ordinal))
        {
            mul = 1024 * 1024;
            s = s[..^2];
        }

        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v * mul : throw new FormatException($"{where}: bad size '{s}'");
    }
}

/// <summary>
/// Layer 8 scanner: built-in rules plus the operators' rules (directory of *.rules, reloaded on change). The file
/// and each file inside a ZIP (one level) are matched.
/// </summary>
public sealed class PatternRuleScanner : IFileScanner
{
    private volatile PatternRuleSet _rules;

    public PatternRuleScanner(PatternRuleSet? extra = null) => _rules = BuiltIn.Merge(extra ?? PatternRuleSet.Empty);

    public static PatternRuleSet BuiltIn { get; } = LoadBuiltIn();

    public PatternRuleSet Rules => _rules;

    public void Replace(PatternRuleSet extra) => _rules = BuiltIn.Merge(extra);

    private static PatternRuleSet LoadBuiltIn()
    {
        using var s = typeof(PatternRuleScanner).Assembly.GetManifestResourceStream("BlueMail.Scanner.Data.builtin.rules")
                      ?? throw new InvalidOperationException("built-in rules missing");
        using var r = new StreamReader(s);
        return PatternRuleSet.Parse(r.ReadToEnd(), "builtin.rules");
    }

    public async Task<ScanResult> ScanAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        var findings = new List<ScanFinding>();
        Stream? owned = null;
        try
        {
            var s = content;
            if (!content.CanSeek)
            {
                owned = await KnownHashScanner.Buffer(content, ct);
                s = owned;
            }

            s.Position = 0;
            Match(await Head(s, s.Length, ct), s.Length, fileName ?? "", findings);
            s.Position = 0;
            if (KnownHashScanner.IsZip(s))
            {
                try
                {
                    using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
                    foreach (var e in zip.Entries.Take(10_000).Where(e => e.Length > 0))
                    {
                        ct.ThrowIfCancellationRequested();
                        await using var es = await e.OpenAsync(ct);
                        Match(await Head(es, e.Length, ct), e.Length, (fileName ?? "") + "/" + e.FullName, findings);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException) // ZLibException is an IOException (BUG-073)
                {
                    // layer 3 reports unreadable archives
                }
            }

            s.Position = 0;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }

        return findings.Count == 0 ? ScanResult.Clean("") : new ScanResult(findings.Max(f => f.Severity), findings, "");
    }

    private void Match(byte[] head, long size, string path, List<ScanFinding> findings)
    {
        foreach (var rule in _rules.Match(head, size))
        {
            findings.Add(new ScanFinding(ScanCodes.PatternRule, rule.Severity, path, rule.Name));
        }
    }

    private static async Task<byte[]> Head(Stream s, long length, CancellationToken ct)
    {
        var buf = new byte[(int)Math.Clamp(length, 0, PatternRuleSet.MaxScanBytes)];
        var n = 0;
        int r;
        while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0)
        {
            n += r;
        }

        return n == buf.Length ? buf : buf[..n];
    }
}
