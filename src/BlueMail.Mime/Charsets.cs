using System.Collections.Concurrent;
using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// Character set handling (PROMPT 2.6.1, 0.2). Every encoding of System.Text.Encoding.CodePages is available.
/// Decoding never throws: a wrong or unknown label falls back with a controlled guess - valid UTF-8 first,
/// then the declared charset, then windows-1252 - so no message is lost because of its encoding.
/// </summary>
public static class Charsets
{
    // Must be the first static initializer: later initializers (Latin1Fallback) need the code pages.
    private static readonly bool ProviderRegistered = RegisterProvider();
    private static readonly ConcurrentDictionary<string, Encoding?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Common mislabels and aliases seen in the wild that .NET does not know.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf8"] = "utf-8",
        ["unicode-1-1-utf-8"] = "utf-8",
        ["latin1"] = "iso-8859-1",
        ["latin-1"] = "iso-8859-1",
        ["cp1252"] = "windows-1252",
        ["cp1258"] = "windows-1258",
        ["cp1251"] = "windows-1251",
        ["cp1250"] = "windows-1250",
        ["cp1256"] = "windows-1256",
        ["cp936"] = "gbk",
        ["gb_2312-80"] = "gb2312",
        ["x-gbk"] = "gbk",
        ["sjis"] = "shift_jis",
        ["x-sjis"] = "shift_jis",
        ["ks_c_5601-1987"] = "ks_c_5601-1987",
        ["tis620"] = "windows-874",
        ["tis-620"] = "windows-874",
        ["iso-8859-11"] = "windows-874",
        ["x-mac-roman"] = "macintosh",
        ["ansi_x3.4-1968"] = "us-ascii",
    };

    private static bool RegisterProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    /// <summary>True once the code-page provider is registered (always, after type initialisation).</summary>
    public static bool CodePagesAvailable => ProviderRegistered;

    public static Encoding Utf8 => Encoding.UTF8;

    public static Encoding Latin1Fallback { get; } = GetEncoding("windows-1252")
        ?? throw new InvalidOperationException("windows-1252 is not available.");

    /// <summary>Encoding for a MIME charset label, or null when unknown.</summary>
    public static Encoding? GetEncoding(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return Cache.GetOrAdd(label.Trim().Trim('"'), static name =>
        {
            if (Aliases.TryGetValue(name, out var real))
            {
                name = real;
            }

            try
            {
                // Replacement fallbacks: invalid bytes become U+FFFD instead of throwing.
                return Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null; // unknown label, or disabled by .NET (UTF-7)
            }
        });
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    public static bool IsAscii(ReadOnlySpan<byte> bytes) => System.Text.Ascii.IsValid(bytes);

    /// <summary>
    /// Decodes bytes labelled with <paramref name="label"/>. Bytes that are valid UTF-8 (and not pure ASCII) are
    /// decoded as UTF-8 when the label is missing, unknown or a single-byte/ASCII label they contradict.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string? label, out bool guessed)
    {
        guessed = false;
        var declared = GetEncoding(label);
        var ascii = IsAscii(bytes);
        if (ascii)
        {
            if (declared is not null && !IsAsciiCompatible(declared))
            {
                return declared.GetString(bytes); // e.g. ISO-2022-JP, UTF-7: 7-bit but not ASCII text
            }

            return Encoding.ASCII.GetString(bytes);
        }

        if (declared is not null && declared.CodePage == 65001)
        {
            return declared.GetString(bytes);
        }

        var utf8 = IsValidUtf8(bytes);
        if (declared is null || (utf8 && (declared.IsSingleByte || declared.CodePage == 20127)))
        {
            guessed = declared is null || declared.CodePage == 20127 || !string.IsNullOrEmpty(label);
            return utf8 ? StrictUtf8.GetString(bytes) : Latin1Fallback.GetString(bytes);
        }

        return declared.GetString(bytes);
    }

    public static string Decode(ReadOnlySpan<byte> bytes, string? label) => Decode(bytes, label, out _);

    private static bool IsAsciiCompatible(Encoding e) => e.CodePage is not (50220 or 50221 or 50222 or 65000 or 1200 or 1201 or 12000 or 12001);
}
