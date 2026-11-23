using System.Globalization;
using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// mbox files (RFC 4155, "mboxrd" quoting): messages separated by a "From " line; body lines matching <c>^&gt;*From </c>
/// get one more '&gt;' when written and lose one when read. Files are LF-separated; messages inside BlueMail use CRLF.
/// Reading also accepts mboxo files (Gmail Takeout, Thunderbird) - the only difference is quoting depth.
/// </summary>
public static class Mbox
{
    /// <summary>The "From " separator line: envelope sender (or MAILER-DAEMON) and the date in asctime format, UTC.</summary>
    public static string Separator(string? envelopeFrom, DateTimeOffset date)
    {
        var from = string.IsNullOrWhiteSpace(envelopeFrom) || envelopeFrom.Any(char.IsWhiteSpace) ? "MAILER-DAEMON" : envelopeFrom;
        return "From " + from + " " + date.UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture).Replace("  ", " ", StringComparison.Ordinal);
    }

    /// <summary>Writes one message (CRLF or LF input) as an mboxrd entry, followed by an empty line.</summary>
    public static async Task WriteMessageAsync(Stream output, string? envelopeFrom, DateTimeOffset date, Stream message, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes(Separator(envelopeFrom, date) + "\n"), ct);
        await foreach (var line in Lines(message, ct))
        {
            if (IsQuotedFrom(line.Span))
            {
                await output.WriteAsync(">"u8.ToArray(), ct);
            }

            await output.WriteAsync(line, ct);
            await output.WriteAsync("\n"u8.ToArray(), ct);
        }

        // An empty line ends the entry (RFC 4155 appendix A); the reader drops it again.
        await output.WriteAsync("\n"u8.ToArray(), ct);
    }

    /// <summary>
    /// Reads the messages of an mbox file one by one, with their separator line. Each message is returned with CRLF
    /// line ends and quoting removed. A message larger than <paramref name="maxMessageBytes"/> is returned as too large
    /// (Content empty) so the caller can report it and go on.
    /// </summary>
    public static async IAsyncEnumerable<MboxEntry> ReadAsync(Stream input, long maxMessageBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? separator = null;
        long offset = 0;
        long entryOffset = 0;
        var body = new MemoryStream();
        var tooLarge = false;
        var previousBlank = true;
        var pendingBlank = 0;

        await foreach (var raw in Lines(input, ct))
        {
            var lineOffset = offset;
            offset += raw.Length + 1;
            if (previousBlank && raw.Span.StartsWith("From "u8))
            {
                if (separator is not null)
                {
                    yield return Finish(separator, entryOffset, body, tooLarge);
                    body = new MemoryStream();
                }

                separator = Encoding.UTF8.GetString(raw.Span);
                entryOffset = lineOffset;
                tooLarge = false;
                pendingBlank = 0;
                previousBlank = false;
                continue;
            }

            previousBlank = raw.Length == 0;
            if (separator is null)
            {
                // Text before the first "From " line is not part of any message.
                continue;
            }

            if (raw.Length == 0)
            {
                // Blank lines are held back: the one before the next separator belongs to the file format, not the message.
                pendingBlank++;
                continue;
            }

            if (tooLarge)
            {
                continue;
            }

            for (; pendingBlank > 0; pendingBlank--)
            {
                body.Write("\r\n"u8);
            }

            body.Write(IsQuotedFrom(raw.Span) && raw.Span[0] == '>' ? raw.Span[1..] : raw.Span);
            body.Write("\r\n"u8);
            if (body.Length > maxMessageBytes)
            {
                tooLarge = true;
                body = new MemoryStream();
            }
        }

        if (separator is not null)
        {
            yield return Finish(separator, entryOffset, body, tooLarge);
        }
    }

    private static MboxEntry Finish(string separator, long offset, MemoryStream body, bool tooLarge) =>
        new(separator, offset, tooLarge ? [] : body.ToArray(), tooLarge);

    /// <summary>"From " possibly preceded by '&gt;' characters (mboxrd).</summary>
    private static bool IsQuotedFrom(ReadOnlySpan<byte> line)
    {
        var i = 0;
        while (i < line.Length && line[i] == '>')
        {
            i++;
        }

        return line[i..].StartsWith("From "u8);
    }

    /// <summary>Lines without their CR/LF.</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Lines(Stream input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var acc = new MemoryStream();
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != '\n')
                {
                    continue;
                }

                acc.Write(buffer, start, i - start);
                start = i + 1;
                var bytes = acc.ToArray();
                acc.SetLength(0);
                yield return bytes.Length > 0 && bytes[^1] == '\r' ? bytes.AsMemory(0, bytes.Length - 1) : bytes;
            }

            acc.Write(buffer, start, n - start);
        }

        if (acc.Length > 0)
        {
            var bytes = acc.ToArray();
            yield return bytes.Length > 0 && bytes[^1] == '\r' ? bytes.AsMemory(0, bytes.Length - 1) : bytes;
        }
    }
}

/// <summary>One message of an mbox file: its separator line, byte offset in the file, content (CRLF).</summary>
public sealed record MboxEntry(string Separator, long Offset, byte[] Content, bool TooLarge)
{
    /// <summary>The date of the separator line, if it parses (asctime, RFC 4155).</summary>
    public DateTimeOffset? SeparatorDate
    {
        get
        {
            var parts = Separator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7)
            {
                return null;
            }

            var text = string.Join(' ', parts[^5..]);
            return DateTime.TryParseExact(text, ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? new DateTimeOffset(d, TimeSpan.Zero) : null;
        }
    }
}
