using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlueMail.Mime;

namespace BlueMail.InteropTests;

/// <summary>
/// BlueMail's mbox writer and reader against Python's mailbox module (Phase 10; PROMPT 11 "mbox xuất ra mở được bằng
/// mailbox của Python"), over the well-formed part of the mail corpus.
/// </summary>
public sealed class MboxInteropTests
{
    private static readonly string Out = Path.Combine(ToolsContainer.RepoRoot, "tests", "interop", "out", "mbox");

    private static List<string> Corpus() =>
        Directory.EnumerateFiles(Path.Combine(ToolsContainer.RepoRoot, "tests", "BlueMail.Corpus", "messages", "wellformed"), "*.eml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>Messages as stored by BlueMail: CRLF line ends.</summary>
    private static byte[] Crlf(byte[] raw) =>
        Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public async Task Our_mbox_is_read_by_python_mailbox_byte_for_byte()
    {
        Directory.CreateDirectory(Out);
        var files = Corpus();
        var expected = new List<string>();
        await using (var mbox = File.Create(Path.Combine(Out, "ours.mbox")))
        {
            // Plus one message with "From " and ">From " lines in its body (the quoting case).
            var tricky = Encoding.ASCII.GetBytes("From: a@x.test\r\nSubject: quoting\r\n\r\nFrom here\r\n>From there\r\n>>From far\r\n\r\nFrom the end");
            foreach (var raw in files.Select(File.ReadAllBytes).Append(tricky))
            {
                var crlf = Crlf(raw);
                // A message's final line break and trailing blank lines are not kept by any mbox reader.
                var trimmed = Encoding.Latin1.GetString(crlf).TrimEnd('\r', '\n') + "\r\n";
                expected.Add(Sha(Encoding.Latin1.GetBytes(trimmed)));
                await Mbox.WriteMessageAsync(mbox, "sender@x.test", DateTimeOffset.UnixEpoch, new MemoryStream(crlf), default);
            }
        }

        var (code, stdout, stderr) = ToolsContainer.Exec("python", "/work/tests/interop/mbox_check.py", "read", "/work/tests/interop/out/mbox/ours.mbox");
        code.Should().Be(0, stderr);
        var python = JsonNode.Parse(stdout)!.AsArray().Select(m => m!["sha256"]!.GetValue<string>()).ToList();
        python.Should().HaveCount(expected.Count, "Python sees every message, no more, no less");
        python.Should().Equal(expected.Select(e => e), "and the same bytes");

        // Our reader gives the same messages back.
        var ours = new List<string>();
        await using var input = File.OpenRead(Path.Combine(Out, "ours.mbox"));
        await foreach (var e in Mbox.ReadAsync(input, 100_000_000))
        {
            ours.Add(Sha(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(e.Content).TrimEnd('\r', '\n') + "\r\n")));
        }

        ours.Should().Equal(expected);
    }

    [Fact]
    public async Task Python_written_mbox_is_read_by_ours()
    {
        Directory.CreateDirectory(Out);
        // mboxo (Python's format) cannot tell an original ">From " line from a quoted "From ": such messages are left out.
        var files = Corpus().Where(f => !Encoding.Latin1.GetString(File.ReadAllBytes(f)).Contains("\n>From ", StringComparison.Ordinal)).ToList();
        File.Delete(Path.Combine(Out, "python.mbox")); // Python's mailbox appends to an existing file
        var (code, _, stderr) = ToolsContainer.Exec(["python", "/work/tests/interop/mbox_check.py", "write", "/work/tests/interop/out/mbox/python.mbox",
            .. files.Select(f => "/work/" + Path.GetRelativePath(ToolsContainer.RepoRoot, f).Replace('\\', '/'))]);
        File.Delete(Path.Combine(Out, "python.mbox.lock"));
        code.Should().Be(0, stderr);

        var expected = files.Select(f => Sha(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Crlf(File.ReadAllBytes(f))).TrimEnd('\r', '\n') + "\r\n"))).ToList();
        var ours = new List<string>();
        await using var input = File.OpenRead(Path.Combine(Out, "python.mbox"));
        await foreach (var e in Mbox.ReadAsync(input, 100_000_000))
        {
            ours.Add(Sha(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(e.Content).TrimEnd('\r', '\n') + "\r\n")));
        }

        ours.Should().Equal(expected);
    }
}
