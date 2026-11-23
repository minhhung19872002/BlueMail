using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BlueMail.Application.Features.Admin.Operations;

/// <summary>
/// A minimal Office Open XML spreadsheet (ECMA-376) writer: one sheet, inline strings and numbers, a bold header row.
/// Enough for report exports, opened by Excel, LibreOffice and Google Sheets; no third-party package.
/// </summary>
public static class Xlsx
{
    public static byte[] Write(string sheetName, IReadOnlyList<string> preamble, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Entry(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            Entry(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="{Escape(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Entry(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                <borders count="1"><border/></borders>
                <cellStyleXfs count="1"><xf/></cellStyleXfs>
                <cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs>
                </styleSheet>
                """);

            var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            var r = 0;
            foreach (var line in preamble)
            {
                Row(sheet, ++r, [line], bold: false);
            }

            if (preamble.Count > 0)
            {
                r++;
            }

            Row(sheet, ++r, header.Cast<object?>().ToList(), bold: true);
            foreach (var row in rows)
            {
                Row(sheet, ++r, row, bold: false);
            }

            sheet.Append("</sheetData></worksheet>");
            Entry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return ms.ToArray();
    }

    private static void Row(StringBuilder sb, int index, IReadOnlyList<object?> cells, bool bold)
    {
        sb.Append("<row r=\"").Append(index).Append("\">");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = Column(c) + index.ToString(CultureInfo.InvariantCulture);
            var style = bold ? " s=\"1\"" : "";
            switch (cells[c])
            {
                case null:
                    break;
                case int or long or double or decimal:
                    sb.Append("<c r=\"").Append(reference).Append('"').Append(style).Append("><v>")
                        .Append(Convert.ToString(cells[c], CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;
                case DateTimeOffset d:
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"").Append(style).Append("><is><t>")
                        .Append(Escape(d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append("</t></is></c>");
                    break;
                default:
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"").Append(style).Append("><is><t xml:space=\"preserve\">")
                        .Append(Escape(Convert.ToString(cells[c], CultureInfo.InvariantCulture) ?? "")).Append("</t></is></c>");
                    break;
            }
        }

        sb.Append("</row>");
    }

    public static string Column(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26)
        {
            s = (char)('A' + ((index - 1) % 26)) + s;
        }

        return s;
    }

    /// <summary>XML text, without the characters XML 1.0 cannot carry; at most Excel's 32 767 characters per cell.</summary>
    private static string Escape(string s)
    {
        var clean = new string(s.Where(XmlConvert.IsXmlChar).Take(32_767).ToArray());
        return clean.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string SheetName(string s) => new string(s.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).Take(31).ToArray()) is { Length: > 0 } n ? n : "Sheet1";

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(content.Trim());
    }
}
