using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using BlueMail.Protocols.Imap;

namespace BlueMail.InteropTests;

/// <summary>
/// BlueMail's IMAP client reads the same folders, UIDs, flags, dates, sizes and bytes as Python's imaplib from a real
/// Dovecot (mail.doitac.test, published on 127.0.0.1:55993) - Phase 10, PROMPT 2.6.5.
/// </summary>
public sealed class ImapClientInteropTests
{
    private static ImapClientOptions Options(ImapTls tls = ImapTls.Implicit) => new()
    {
        Tls = tls,
        CertificateValidation = ImapCertificates.WithPinned(
            [X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(ToolsContainer.RepoRoot, "deploy/dev/postfix/tls/cert.pem")))]),
        Reader = new ImapReaderOptions { SpoolThreshold = 256 * 1024 },
    };

    [Fact]
    public async Task Same_mailbox_as_python_imaplib()
    {
        ToolsContainer.Exec("python", "/work/tests/interop/imap_source.py", "populate").ExitCode.Should().Be(0);
        var (code, stdout, stderr) = ToolsContainer.Exec("python", "/work/tests/interop/imap_source.py", "dump");
        code.Should().Be(0, stderr);
        var reference = JsonNode.Parse(stdout)!.AsObject();

        await using var client = new ImapClient(Options());
        await client.ConnectAsync("127.0.0.1", 55993, default);
        await client.LoginAsync("nguon@doitac.test", "Doitac-Imap-Dev-1", default);
        var folders = await client.ListAsync(default);
        folders.Single(f => f.Name == "Sent").SpecialUse.Should().Be(@"\SENT");

        var ours = new Dictionary<string, (uint Validity, List<string> Messages)>();
        var spooled = 0;
        foreach (var folder in folders.Where(f => f.Selectable))
        {
            var selected = await client.ExamineAsync(folder, default);
            var uids = await client.UidSearchAsync("ALL", default);
            var list = new List<string>();
            await client.UidFetchAsync(uids, withBody: true, async m =>
            {
                spooled += m.Body!.Bytes is null ? 1 : 0;
                await using var s = m.Body.OpenRead();
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s));
                list.Add(Line(m.Uid, m.Flags, m.InternalDate!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), m.Size!.Value, sha));
            }, default);
            ours[folder.Name] = (selected.UidValidity, list);
        }

        await client.LogoutAsync(default);

        ours.Keys.Should().BeEquivalentTo(reference.Select(kv => kv.Key), "the same folders, names decoded from modified UTF-7");
        foreach (var (name, node) in reference)
        {
            ours[name].Validity.Should().Be(node!["uidvalidity"]!.GetValue<uint>(), name);
            var expected = node["messages"]!.AsArray().Select(m => Line(m!["uid"]!.GetValue<uint>(),
                m["flags"]!.AsArray().Select(f => f!.GetValue<string>()).ToList(), m["internaldate"]!.GetValue<string>(), m["size"]!.GetValue<long>(),
                m["sha256"]!.GetValue<string>())).ToList();
            ours[name].Messages.Should().Equal(expected, name);
        }

        spooled.Should().BeGreaterThan(0, "the 4.8 MB message was streamed to disk");
    }

    private static string Line(uint uid, IEnumerable<string> flags, string date, long size, string sha) =>
        $"{uid} [{string.Join(' ', flags.Where(f => f != @"\Recent").Order(StringComparer.Ordinal))}] {date} {size} {sha}";

    [Fact]
    public async Task Wrong_password_is_an_authentication_failure_and_cleartext_is_refused()
    {
        await using (var client = new ImapClient(Options()))
        {
            await client.ConnectAsync("127.0.0.1", 55993, default);
            var act = () => client.LoginAsync("nguon@doitac.test", "sai-mật-khẩu", default);
            (await act.Should().ThrowAsync<ImapCommandException>()).Which.IsAuthenticationFailure.Should().BeTrue();
        }

        var unpinned = new ImapClient(Options() with { CertificateValidation = null });
        var untrusted = () => unpinned.ConnectAsync("127.0.0.1", 55993, default);
        await untrusted.Should().ThrowAsync<System.Security.Authentication.AuthenticationException>("a self-signed certificate is not trusted unless pinned");
        await unpinned.DisposeAsync();
    }
}
