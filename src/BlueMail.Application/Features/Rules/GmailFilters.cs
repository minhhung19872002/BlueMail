using System.Globalization;
using System.Text;
using System.Xml;

namespace BlueMail.Application.Features.Rules;

/// <summary>A filter in Gmail's export form: a query plus actions, labels by name.</summary>
public sealed record PortableFilter(string Criteria, FilterActions Actions, IReadOnlyList<string> LabelNames);

/// <summary>
/// Gmail "mailFilters.xml" (Atom feed with apps:property elements) import and export (PROMPT 6.8). Criteria
/// properties become one query in our operator language; unknown properties are reported, never guessed.
/// </summary>
public static class GmailFilters
{
    private const string Atom = "http://www.w3.org/2005/Atom";
    private const string Apps = "http://schemas.google.com/apps/2006";

    private static readonly Dictionary<string, string> SmartLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["^smartlabel_personal"] = "PRIMARY", ["^smartlabel_social"] = "SOCIAL", ["^smartlabel_promo"] = "PROMOTIONS",
        ["^smartlabel_notification"] = "UPDATES", ["^smartlabel_group"] = "FORUMS",
    };

    public static string Export(IEnumerable<PortableFilter> filters, string ownerName, string ownerEmail, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            var stamp = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            w.WriteStartElement("feed", Atom);
            w.WriteAttributeString("xmlns", "apps", null, Apps);
            w.WriteElementString("title", Atom, "Mail Filters");
            w.WriteElementString("id", Atom, "tag:bluemail,2026:filters:" + now.ToUnixTimeSeconds());
            w.WriteElementString("updated", Atom, stamp);
            w.WriteStartElement("author", Atom);
            w.WriteElementString("name", Atom, ownerName);
            w.WriteElementString("email", Atom, ownerEmail);
            w.WriteEndElement();
            var i = 0;
            foreach (var f in filters)
            {
                w.WriteStartElement("entry", Atom);
                w.WriteStartElement("category", Atom);
                w.WriteAttributeString("term", "filter");
                w.WriteEndElement();
                w.WriteElementString("title", Atom, "Mail Filter");
                w.WriteElementString("id", Atom, "tag:bluemail,2026:filter:" + i++);
                w.WriteElementString("updated", Atom, stamp);
                w.WriteElementString("content", Atom, "");
                // Our criteria are already Gmail queries: hasTheWord carries them whole, nothing is lost.
                Prop(w, "hasTheWord", f.Criteria);
                foreach (var l in f.LabelNames)
                {
                    Prop(w, "label", l);
                }

                var a = f.Actions;
                if (a.SkipInbox) Prop(w, "shouldArchive", "true");
                if (a.MarkRead) Prop(w, "shouldMarkAsRead", "true");
                if (a.Star) Prop(w, "shouldStar", "true");
                if (a.Trash) Prop(w, "shouldTrash", "true");
                if (a.NeverSpam) Prop(w, "shouldNeverSpam", "true");
                if (a.Important == "ALWAYS") Prop(w, "shouldAlwaysMarkAsImportant", "true");
                if (a.Important == "NEVER") Prop(w, "shouldNeverMarkAsImportant", "true");
                if (a.ForwardTo is { } fw) Prop(w, "forwardTo", fw);
                if (a.Category is { } c && SmartLabels.FirstOrDefault(kv => kv.Value == c).Key is { } smart) Prop(w, "smartLabelToApply", smart);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal);
    }

    private static void Prop(XmlWriter w, string name, string value)
    {
        w.WriteStartElement("apps", "property", Apps);
        w.WriteAttributeString("name", name);
        w.WriteAttributeString("value", value);
        w.WriteEndElement();
    }

    /// <summary>Parses an export. Entries that cannot be represented are returned in <c>Problems</c> with a reason code.</summary>
    public static (IReadOnlyList<PortableFilter> Filters, IReadOnlyList<(int Entry, string Code, string? Detail)> Problems) Import(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // no XXE, no entity expansion
            XmlResolver = null,
            MaxCharactersInDocument = 5_000_000,
            IgnoreComments = true,
        };
        var doc = new XmlDocument { XmlResolver = null };
        using (var r = XmlReader.Create(xml, settings))
        {
            doc.Load(r);
        }

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("a", Atom);
        ns.AddNamespace("apps", Apps);
        var filters = new List<PortableFilter>();
        var problems = new List<(int, string, string?)>();
        var entries = doc.SelectNodes("/a:feed/a:entry", ns);
        if (doc.DocumentElement?.LocalName != "feed" || doc.DocumentElement.NamespaceURI != Atom || entries is null)
        {
            throw new FormatException("not_gmail_filters");
        }

        var n = 0;
        foreach (XmlElement e in entries)
        {
            n++;
            var props = new List<(string Name, string Value)>();
            foreach (XmlElement p in e.SelectNodes("apps:property", ns)!)
            {
                props.Add((p.GetAttribute("name"), p.GetAttribute("value")));
            }

            var terms = new List<string>();
            var labels = new List<string>();
            var a = new FilterActions();
            string? sizeOp = null, size = null, sizeUnit = null;
            var unknown = new List<string>();
            foreach (var (name, value) in props)
            {
                switch (name)
                {
                    case "from": terms.Add($"from:({value})"); break;
                    case "to": terms.Add($"to:({value})"); break;
                    case "subject": terms.Add($"subject:({value})"); break;
                    case "hasTheWord": terms.Add($"({value})"); break;
                    case "doesNotHaveTheWord": terms.Add($"-({value})"); break;
                    case "hasAttachment" when value == "true": terms.Add("has:attachment"); break;
                    case "sizeOperator": sizeOp = value; break;
                    case "size": size = value; break;
                    case "sizeUnit": sizeUnit = value; break;
                    case "excludeChats": break;
                    case "label": labels.Add(value); break;
                    case "shouldArchive": a = a with { SkipInbox = value == "true" }; break;
                    case "shouldMarkAsRead": a = a with { MarkRead = value == "true" }; break;
                    case "shouldStar": a = a with { Star = value == "true" }; break;
                    case "shouldTrash": a = a with { Trash = value == "true" }; break;
                    case "shouldNeverSpam": a = a with { NeverSpam = value == "true" }; break;
                    case "shouldAlwaysMarkAsImportant" when value == "true": a = a with { Important = "ALWAYS" }; break;
                    case "shouldNeverMarkAsImportant" when value == "true": a = a with { Important = "NEVER" }; break;
                    case "forwardTo": a = a with { ForwardTo = value.Trim().ToLowerInvariant() }; break;
                    case "smartLabelToApply" when SmartLabels.TryGetValue(value, out var cat): a = a with { Category = cat }; break;
                    default: unknown.Add(name); break;
                }
            }

            if (size is not null)
            {
                var unit = sizeUnit switch { "s_sk" => "K", "s_smb" => "M", _ => "" };
                terms.Add((sizeOp == "s_sl" ? "larger:" : "smaller:") + size + unit);
            }

            if (unknown.Count > 0)
            {
                problems.Add((n, "unknown_property", string.Join(", ", unknown)));
            }

            if (terms.Count == 0)
            {
                problems.Add((n, "no_criteria", null));
                continue;
            }

            if (a.IsEmpty && labels.Count == 0)
            {
                problems.Add((n, "no_action", null));
                continue;
            }

            filters.Add(new PortableFilter(string.Join(' ', terms), a, labels));
        }

        return (filters, problems);
    }
}
