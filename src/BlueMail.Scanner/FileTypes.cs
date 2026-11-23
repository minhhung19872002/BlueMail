using System.Text;

namespace BlueMail.Scanner;

/// <summary>Content type by byte signature (layer 1). Extension and Content-Type are never trusted.</summary>
public static class FileTypes
{
    public const string WindowsExecutable = "exe";
    public const string Elf = "elf";
    public const string MachO = "macho";
    public const string Zip = "zip";
    public const string Ooxml = "ooxml";
    public const string Jar = "jar";
    public const string Ole2 = "ole2";
    public const string Msi = "msi";
    public const string Pdf = "pdf";
    public const string Rar = "rar";
    public const string SevenZip = "7z";
    public const string Gzip = "gzip";
    public const string Tar = "tar";
    public const string Cab = "cab";
    public const string Iso = "iso";
    public const string Vhd = "vhd";
    public const string Lnk = "lnk";
    public const string Chm = "chm";
    public const string Png = "png";
    public const string Jpeg = "jpeg";
    public const string Gif = "gif";
    public const string Webp = "webp";
    public const string Html = "html";
    public const string Svg = "svg";
    public const string Rtf = "rtf";
    public const string Text = "text";
    public const string Unknown = "unknown";

    /// <summary>Extensions that a detected type may legitimately carry (for mismatch detection).</summary>
    private static readonly Dictionary<string, string[]> ExpectedExtensions = new()
    {
        [Pdf] = ["pdf"],
        [Png] = ["png"],
        [Jpeg] = ["jpg", "jpeg", "jpe", "jfif"],
        [Gif] = ["gif"],
        [Webp] = ["webp"],
        [Zip] = ["zip"],
        [Ooxml] = ["docx", "xlsx", "pptx", "docm", "xlsm", "pptm", "dotx", "dotm", "xltx", "xltm", "potx", "ppsx", "vsdx", "zip"],
        [Ole2] = ["doc", "xls", "ppt", "dot", "xlt", "pot", "msg", "vsd", "pub"],
        [Rar] = ["rar"],
        [SevenZip] = ["7z"],
        [Gzip] = ["gz", "tgz"],
        [Tar] = ["tar"],
    };

    public static string Detect(ReadOnlySpan<byte> head, out bool isZipBased)
    {
        isZipBased = false;
        if (Starts(head, "MZ"u8))
        {
            return WindowsExecutable;
        }

        if (Starts(head, [0x7F, (byte)'E', (byte)'L', (byte)'F']))
        {
            return Elf;
        }

        if (Starts(head, [0xCF, 0xFA, 0xED, 0xFE]) || Starts(head, [0xCE, 0xFA, 0xED, 0xFE]) || Starts(head, [0xCA, 0xFE, 0xBA, 0xBE]))
        {
            return MachO;
        }

        if (Starts(head, "PK\x03\x04"u8) || Starts(head, "PK\x05\x06"u8))
        {
            isZipBased = true;
            return Zip; // refined to ooxml / jar after looking inside
        }

        if (Starts(head, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return Ole2; // refined to msi after reading the directory
        }

        if (head.IndexOf("%PDF-"u8) is >= 0 and < 1024)
        {
            return Pdf;
        }

        if (Starts(head, "Rar!\x1A\x07"u8))
        {
            return Rar;
        }

        if (Starts(head, [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return SevenZip;
        }

        if (Starts(head, [0x1F, 0x8B]))
        {
            return Gzip;
        }

        if (Starts(head, "MSCF"u8))
        {
            return Cab;
        }

        if (Starts(head, [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00]))
        {
            return Lnk;
        }

        if (Starts(head, "ITSF"u8))
        {
            return Chm;
        }

        if (Starts(head, "conectix"u8) || Starts(head, "vhdxfile"u8))
        {
            return Vhd;
        }

        if (head.Length >= 0x8006 && head.Slice(0x8001, 5).SequenceEqual("CD001"u8))
        {
            return Iso;
        }

        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return Tar;
        }

        if (Starts(head, [0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            return Png;
        }

        if (Starts(head, [0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (Starts(head, "GIF87a"u8) || Starts(head, "GIF89a"u8))
        {
            return Gif;
        }

        if (Starts(head, "RIFF"u8) && head.Length >= 12 && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        if (Starts(head, "{\\rtf"u8))
        {
            return Rtf;
        }

        var text = LooksLikeText(head);
        if (text)
        {
            var sample = Encoding.UTF8.GetString(head[..Math.Min(head.Length, 4096)]).TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
            if (sample.Contains("<svg", StringComparison.Ordinal))
            {
                return Svg;
            }

            if (sample.StartsWith("<!doctype html", StringComparison.Ordinal) || sample.StartsWith("<html", StringComparison.Ordinal)
                || sample.Contains("<script", StringComparison.Ordinal) || sample.Contains("<body", StringComparison.Ordinal) || sample.Contains("<head", StringComparison.Ordinal))
            {
                return Html;
            }

            return Text;
        }

        return Unknown;
    }

    private static bool Starts(ReadOnlySpan<byte> head, ReadOnlySpan<byte> signature) => head.StartsWith(signature);

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        var sample = head[..Math.Min(head.Length, 8192)];
        if (sample.Length == 0)
        {
            return true;
        }

        if (Starts(sample, [0xFF, 0xFE]) || Starts(sample, [0xFE, 0xFF]))
        {
            return true; // UTF-16 BOM
        }

        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 9 || (b > 13 && b < 32))
            {
                control++;
            }
        }

        return control * 100 < sample.Length;
    }

    public static bool ExtensionFits(string type, string extension) =>
        !ExpectedExtensions.TryGetValue(type, out var exts) || exts.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>All extensions of a name, last first ("hoadon.pdf.exe" -> exe, pdf).</summary>
    public static IReadOnlyList<string> Extensions(string name)
    {
        var parts = name.Trim().TrimEnd('.', ' ').Split('.');
        return parts.Length <= 1 ? [] : parts.Skip(1).Reverse().Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length is > 0 and <= 20).ToList();
    }

    /// <summary>True when the name contains Unicode bidirectional controls (RLO trick: "invoice‮fdp.exe").</summary>
    public static bool HasBidiControl(string name) =>
        name.Any(c => c is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‎' or '‏' or '؜');
}
