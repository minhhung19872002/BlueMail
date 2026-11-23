using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BlueMail.Antispam;
using BlueMail.UnitTests.Conventions;
using Xunit.Abstractions;

namespace BlueMail.UnitTests.Antispam;

/// <summary>
/// Quality of the spam scorer on the labelled corpus (PROMPT 2.3): two-fold cross-validation - Bayes learns on one
/// half and scores the other - then the report of hit rate and false positives per kind. False positives are worse
/// than missed spam: the gate is zero false positives on this corpus (target &lt; 0.1 %).
/// </summary>
public class SpamQualityTests(ITestOutputHelper output)
{
    public const double SpamThreshold = 5.0;

    internal sealed record Row(string Label, string Kind, SpamInput Input, IReadOnlyList<string> Tokens);

    internal static List<Row> Load()
    {
        var path = Path.Combine(Repo.Root, "tests", "BlueMail.SpamCorpus", "corpus.jsonl");
        var rows = new List<Row>();
        var now = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var j = doc.RootElement;
            string? S(string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool B(string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var links = j.TryGetProperty("links", out var l) ? l.EnumerateArray().Select(x => (x.GetProperty("href").GetString()!, x.GetProperty("text").GetString()!)).ToList() : [];
            var dkim = j.TryGetProperty("dkim", out var d) ? d.EnumerateArray().Select(x => (x.GetProperty("result").GetString()!, (string?)x.GetProperty("domain").GetString())).ToList() : [];
            var input = new SpamInput
            {
                RemoteIp = IPAddress.Parse("198.51.100.7"),
                Helo = S("helo") ?? "mail." + SpamRules.DomainOf(S("fromAddress")),
                ReverseDns = S("ptr"),
                Spf = S("spf") ?? "none",
                Dkim = dkim,
                Dmarc = S("dmarc") ?? "none",
                DmarcPolicy = S("dmarcPolicy") ?? "none",
                FromAddress = S("fromAddress"),
                FromName = S("fromName"),
                ReplyTo = S("replyTo"),
                Subject = S("subject"),
                Date = B("missingDate") ? null : now.AddMinutes(-5),
                HasMessageId = !B("missingMsgId"),
                TextBody = S("text"),
                HtmlBody = S("html"),
                Images = j.TryGetProperty("images", out var im) ? im.GetInt32() : 0,
                Links = links,
                SuspiciousAttachmentCodes = j.TryGetProperty("attachments", out var at) ? at.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
                OurDomains = ["bluemail.test", "acme.test"],
                OurHostname = "mx.bluemail.test",
                SenderKnown = B("known"),
                SenderDomainFirstSeen = B("firstSeen"),
                Now = now,
            };
            rows.Add(new Row(S("label")!, S("kind")!, input, SpamTokens.Of(input).ToList()));
        }

        return rows;
    }

    [Fact]
    public void Cross_validated_quality_meets_the_false_positive_target()
    {
        var rows = Load();
        rows.Should().HaveCount(1400);
        var results = new List<(Row Row, SpamVerdict Verdict)>();
        var folds = new[] { rows.Where((_, i) => i % 2 == 0).ToList(), rows.Where((_, i) => i % 2 == 1).ToList() };
        for (var f = 0; f < 2; f++)
        {
            var model = new InMemoryBayesModel(BayesScope.Global);
            foreach (var r in folds[1 - f])
            {
                model.Learn(r.Tokens, r.Label == "spam");
            }

            foreach (var r in folds[f])
            {
                results.Add((r, SpamRules.Score(r.Input, Bayes.Classify(r.Tokens, [model]))));
            }
        }

        var ham = results.Where(x => x.Row.Label == "ham").ToList();
        var spam = results.Where(x => x.Row.Label == "spam").ToList();
        var fp = ham.Where(x => x.Verdict.IsSpam(SpamThreshold)).ToList();
        var tp = spam.Count(x => x.Verdict.IsSpam(SpamThreshold));

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Threshold {SpamThreshold}: spam caught {tp}/{spam.Count} ({100.0 * tp / spam.Count:0.0} %), false positives {fp.Count}/{ham.Count} ({100.0 * fp.Count / ham.Count:0.00} %)");
        foreach (var g in results.GroupBy(x => (x.Row.Label, x.Row.Kind)).OrderBy(g => g.Key))
        {
            var flagged = g.Count(x => x.Verdict.IsSpam(SpamThreshold));
            report.AppendLine(CultureInfo.InvariantCulture, $"  {g.Key.Label,-4} {g.Key.Kind,-9} {flagged,4}/{g.Count(),-4} flagged, mean score {g.Average(x => x.Verdict.Score):0.0}");
        }

        var worstHam = ham.OrderByDescending(x => x.Verdict.Score).First();
        report.AppendLine(CultureInfo.InvariantCulture, $"  highest ham score {worstHam.Verdict.Score:0.0} ({worstHam.Row.Kind}: {worstHam.Row.Input.Subject}) :: {string.Join(", ", worstHam.Verdict.Hits.Select(h => h.Code + "=" + h.Score.ToString(CultureInfo.InvariantCulture)))}");
        foreach (var x in fp.Take(5))
        {
            report.AppendLine("  FP: " + x.Row.Input.Subject + " :: " + string.Join(", ", x.Verdict.Hits.Select(h => h.Code + "=" + h.Score.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (var x in spam.Where(x => !x.Verdict.IsSpam(SpamThreshold)).Take(5))
        {
            report.AppendLine("  missed: " + x.Row.Input.Subject + " :: " + string.Join(", ", x.Verdict.Hits.Select(h => h.Code + "=" + h.Score.ToString(CultureInfo.InvariantCulture))));
        }

        output.WriteLine(report.ToString());
        File.WriteAllText(Path.Combine(Repo.Root, "tests", "BlueMail.SpamCorpus", "last-report.txt"), report.ToString());

        fp.Should().BeEmpty("false positives are worse than missed spam (target < 0.1 %)");
        ((double)tp / spam.Count).Should().BeGreaterThanOrEqualTo(0.95, "spam caught");
    }
}
