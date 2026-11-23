using System.Globalization;
using System.Text;

namespace BlueMail.Protocols.Imap;

/// <summary>A parsed IMAP value (RFC 3501 section 4 / RFC 9051 section 4): atom, number, string, NIL or parenthesised list.</summary>
public abstract record ImapValue
{
    /// <summary>The value as text: atoms and numbers as written, strings decoded as UTF-8, NIL as null.</summary>
    public virtual string? AsText() => null;

    public long? AsNumber() => this switch
    {
        ImapNumber n => n.Value,
        ImapAtom a when long.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public IReadOnlyList<ImapValue> AsList() => this is ImapList l ? l.Items : [];
}

public sealed record ImapAtom(string Value) : ImapValue
{
    public override string? AsText() => Value;
    public bool Is(string text) => string.Equals(Value, text, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Value;
}

public sealed record ImapNumber(long Value) : ImapValue
{
    public override string? AsText() => Value.ToString(CultureInfo.InvariantCulture);
    public override string ToString() => AsText()!;
}

public sealed record ImapNil : ImapValue
{
    public static readonly ImapNil Instance = new();
    public override string ToString() => "NIL";
}

/// <summary>A quoted string or a literal. Large literals are spooled to a temporary file and read as a stream.</summary>
public sealed record ImapString : ImapValue
{
    public ImapString(byte[] bytes, bool literal = false)
    {
        Bytes = bytes;
        IsLiteral = literal;
    }

    public ImapString(ImapLiteralData data)
    {
        Data = data;
        IsLiteral = true;
        Bytes = data.Inline;
    }

    /// <summary>The bytes when held in memory; null for a spooled literal (use <see cref="OpenRead"/>).</summary>
    public byte[]? Bytes { get; }
    public ImapLiteralData? Data { get; }
    public bool IsLiteral { get; }
    public long Length => Bytes?.LongLength ?? Data!.Length;

    public Stream OpenRead() => Bytes is { } b ? new MemoryStream(b, writable: false) : Data!.OpenRead();

    /// <summary>Decoded as UTF-8 (RFC 6855 / 9051 allow UTF-8 strings; plain US-ASCII is a subset).</summary>
    public override string? AsText() => Bytes is { } b ? Encoding.UTF8.GetString(b) : null;

    public override string ToString() => IsLiteral ? $"{{{Length}}}" : "\"" + AsText() + "\"";
}

public sealed record ImapList(IReadOnlyList<ImapValue> Items) : ImapValue
{
    public override string ToString() => "(" + string.Join(' ', Items) + ")";
}

/// <summary>Literal bytes read from the wire: in memory when small, otherwise in a temporary file deleted on dispose.</summary>
public sealed class ImapLiteralData : IDisposable
{
    private readonly string? _path;

    internal ImapLiteralData(byte[] inline)
    {
        Inline = inline;
        Length = inline.LongLength;
    }

    internal ImapLiteralData(string path, long length)
    {
        _path = path;
        Length = length;
    }

    public byte[]? Inline { get; }
    public long Length { get; }

    public Stream OpenRead() => Inline is { } b
        ? new MemoryStream(b, writable: false)
        : new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

    public void Dispose()
    {
        if (_path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Still open elsewhere; the temp folder is cleaned by the OS.
            }
        }
    }
}

public enum ImapStatus { Ok, No, Bad, Bye, PreAuth }

/// <summary>
/// One server response (RFC 3501 7). <see cref="Tag"/> is "*" for untagged, "+" for a continuation request, otherwise the
/// command tag. Status responses carry <see cref="Status"/>, an optional response code and the human text; untagged data
/// responses carry their tokens in <see cref="Data"/> (e.g. <c>5 EXISTS</c> → [5, EXISTS]).
/// </summary>
public sealed record ImapResponse(string Tag, ImapStatus? Status, string? Code, IReadOnlyList<ImapValue> CodeArgs, string Text, IReadOnlyList<ImapValue> Data)
    : IDisposable
{
    public bool IsUntagged => Tag == "*";
    public bool IsContinuation => Tag == "+";

    /// <summary>The first data atom (CAPABILITY, LIST, SEARCH…) or, for <c>n EXISTS</c> / <c>n FETCH</c>, the second.</summary>
    public string? Kind => Data.Count switch
    {
        0 => null,
        _ when Data[0] is ImapNumber && Data.Count > 1 => (Data[1] as ImapAtom)?.Value.ToUpperInvariant(),
        _ => (Data[0] as ImapAtom)?.Value.ToUpperInvariant(),
    };

    public void Dispose()
    {
        foreach (var s in Strings(Data))
        {
            s.Data?.Dispose();
        }
    }

    private static IEnumerable<ImapString> Strings(IEnumerable<ImapValue> values)
    {
        foreach (var v in values)
        {
            if (v is ImapString s)
            {
                yield return s;
            }
            else if (v is ImapList l)
            {
                foreach (var inner in Strings(l.Items))
                {
                    yield return inner;
                }
            }
        }
    }

    public override string ToString() => Status is { } st
        ? $"{Tag} {st.ToString().ToUpperInvariant()}{(Code is null ? "" : " [" + Code + "]")} {Text}"
        : $"{Tag} {string.Join(' ', Data)}";
}

public class ImapProtocolException(string message) : Exception(message);
