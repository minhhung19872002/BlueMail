using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueMail.Mime;

namespace BlueMail.InteropTests;

/// <summary>
/// BlueMail.Mime against Python's email package on the whole corpus (PROMPT 11, Phase 2 acceptance):
/// subject, addresses, date, Message-ID, part tree (types), file names, decoded payload (SHA-256), text.
/// Intended differences are listed with a reason in tests/BlueMail.Corpus/python-differences.json;
/// any other difference fails and prints the message.
/// </summary>
public sealed class MimeCorpusTests
{
    private static readonly string CorpusDir = Path.Combine(ToolsContainer.RepoRoot, "tests", "BlueMail.Corpus", "messages");

    [Fact]
    public void Every_corpus_message_parses_without_throwing()
    {
        var files = Directory.EnumerateFiles(CorpusDir, "*.eml", SearchOption.AllDirectories).ToList();
        files.Should().HaveCountGreaterThanOrEqualTo(40);
        foreach (var f in files)
        {
            using var s = File.OpenRead(f);
            var m = MimeMessage.Parse(s);
            foreach (var e in m.Root.Walk().Where(x => x.IsLeaf))
            {
                _ = e.ReadDecodedBody(s);
                if (e.MediaType.StartsWith("text/", StringComparison.Ordinal))
                {
                    _ = e.ReadText(s);
                }
            }

            _ = m.Subject;
            _ = m.From;
            _ = m.Date;
        }
    }

    [Fact]
    public void Matches_python_email_on_the_whole_corpus()
    {
        var (code, stdout, stderr) = ToolsContainer.Exec("python", "/work/tests/interop/mime_dump.py", "/work/tests/BlueMail.Corpus/messages");
        code.Should().Be(0, stderr);
        var reference = JsonNode.Parse(stdout)!.AsObject();
        var exceptions = LoadExceptions();
        var unexpected = new List<string>();
        var usedExceptions = new HashSet<string>();

        foreach (var (file, refNode) in reference)
        {
            var ours = Summarize(Path.Combine(CorpusDir, file.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var field in new[] { "subject", "from", "to", "cc", "date", "message_id", "parts" })
            {
                var a = Canon(refNode![field]);
                var b = Canon(ours[field]);
                if (a == b)
                {
                    continue;
                }

                var key = file + "#" + field;
                if (exceptions.ContainsKey(key))
                {
                    usedExceptions.Add(key);
                    continue;
                }

                unexpected.Add($"{key}\n   python : {Trim(a)}\n   bluemail: {Trim(b)}");
            }
        }

        if (unexpected.Count > 0)
        {
            Assert.Fail($"{unexpected.Count} unexpected difference(s) from Python's email:\n" + string.Join("\n", unexpected));
        }

        exceptions.Keys.Except(usedExceptions).Should().BeEmpty("stale entries in python-differences.json must be removed");
    }

    private static Dictionary<string, string> LoadExceptions()
    {
        var path = Path.Combine(ToolsContainer.RepoRoot, "tests", "BlueMail.Corpus", "python-differences.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    private static string Trim(string s) => s.Length <= 600 ? s : s[..600] + "…";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON text with object keys sorted, so key order does not matter.</summary>
    private static string Canon(JsonNode? n) => Sorted(n)?.ToJsonString(Relaxed) ?? "null";

    private static JsonNode? Sorted(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(n.ToJsonString()),
    };

    /// <summary>Same shape as tests/interop/mime_dump.py.</summary>
    internal static JsonObject Summarize(string path)
    {
        using var s = File.OpenRead(path);
        var m = MimeMessage.Parse(s);
        var parts = new JsonArray();
        foreach (var e in m.Root.Walk())
        {
            var entry = new JsonObject { ["filename"] = e.FileName, ["type"] = e.MediaType };
            var container = e.IsMultipart || e.IsMessage;
            if (!container)
            {
                var payload = e.ReadDecodedBody(s);
                entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload));
                if (e.MediaType.StartsWith("text/", StringComparison.Ordinal))
                {
                    entry["text"] = Charsets.Decode(payload, e.Charset).Replace("\r\n", "\n", StringComparison.Ordinal);
                }
            }

            parts.Add(entry);
        }

        static JsonNode Addr(IReadOnlyList<MailboxAddress> list) => new JsonArray(list.Select(a => (JsonNode)new JsonArray(a.Name, a.Address)).ToArray());
        return new JsonObject
        {
            ["subject"] = m.Headers["Subject"] is null ? null : m.Subject,
            ["from"] = Addr(m.From),
            ["to"] = Addr(m.To),
            ["cc"] = Addr(m.Cc),
            ["date"] = m.Date?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["message_id"] = m.Headers["Message-ID"] is null ? null : m.MessageId ?? m.Headers["Message-ID"]!.Value().Trim('<', '>', ' '),
            ["parts"] = parts,
        };
    }
}
