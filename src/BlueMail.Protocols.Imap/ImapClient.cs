using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace BlueMail.Protocols.Imap;

public enum ImapTls { Implicit, StartTls, None }

public sealed record ImapClientOptions
{
    public ImapTls Tls { get; init; } = ImapTls.Implicit;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per command; a FETCH of a large batch is one command, so this is generous.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Null: the system's certificate validation. Only test set-ups replace it.</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    public ImapReaderOptions Reader { get; init; } = new();
}

public sealed class ImapCommandException(string command, ImapResponse response)
    : ImapProtocolException($"{command}: {response.Status?.ToString().ToUpperInvariant()} {(response.Code is null ? "" : "[" + response.Code + "] ")}{response.Text}")
{
    public ImapResponse Response { get; } = response;

    /// <summary>NO [AUTHENTICATIONFAILED] / [AUTHORIZATIONFAILED] (RFC 5530): wrong credentials, not a network problem.</summary>
    public bool IsAuthenticationFailure => Response.Code is "AUTHENTICATIONFAILED" or "AUTHORIZATIONFAILED" or "LOGINDISABLED";
}

/// <summary>A folder from LIST (RFC 3501 6.3.8) with RFC 6154 special-use attributes.</summary>
public sealed record ImapFolder(string Name, string RawName, string? Delimiter, IReadOnlyList<string> Attributes)
{
    public bool Selectable => !Attributes.Any(a => a.Equals("\\Noselect", StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("\\NonExistent", StringComparison.OrdinalIgnoreCase));

    /// <summary>\Sent, \Drafts, \Junk, \Trash, \Archive, \All, \Flagged, \Important — or null.</summary>
    public string? SpecialUse => Attributes.FirstOrDefault(a => a.ToUpperInvariant() is "\\SENT" or "\\DRAFTS" or "\\JUNK" or "\\TRASH" or "\\ARCHIVE"
        or "\\ALL" or "\\FLAGGED" or "\\IMPORTANT")?.ToUpperInvariant();
}

public sealed record ImapSelected(string Folder, uint UidValidity, long Exists, uint? UidNext);

/// <summary>One message of a UID FETCH. <see cref="Body"/> is valid only inside the callback.</summary>
public sealed record ImapFetched(uint Uid, IReadOnlyList<string> Flags, DateTimeOffset? InternalDate, long? Size, ImapString? Body, IReadOnlyList<string> GmailLabels);

/// <summary>
/// IMAP client for importing mail (PROMPT 2.6.5): TLS (implicit or STARTTLS), LOGIN, AUTHENTICATE PLAIN (SASL-IR when
/// offered), CAPABILITY, LIST with SPECIAL-USE, SELECT/EXAMINE, UID SEARCH, UID FETCH (FLAGS, INTERNALDATE, RFC822.SIZE,
/// BODY.PEEK[], X-GM-LABELS on Gmail), LOGOUT. Literals are read as streams. Not thread-safe: one command at a time.
/// </summary>
public sealed class ImapClient(ImapClientOptions options) : IAsyncDisposable
{
    private TcpClient? _tcp;
    private Stream _stream = Stream.Null;
    private ImapReader? _reader;
    private int _tag;

    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool TlsActive { get; private set; }
    public bool IsGmail => Capabilities.Contains("X-GM-EXT-1");

    public Task ConnectAsync(string host, int port, CancellationToken ct) => ConnectAsync(host, null, port, ct);

    /// <summary>
    /// Connects to <paramref name="address"/> when given (the caller resolved and checked it, anti-SSRF) while TLS still
    /// verifies the certificate for <paramref name="host"/>.
    /// </summary>
    public async Task ConnectAsync(string host, System.Net.IPAddress? address, int port, CancellationToken ct)
    {
        _tcp = address is null ? new TcpClient() : new TcpClient(address.AddressFamily);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(options.ConnectTimeout);
            if (address is null)
            {
                await _tcp.ConnectAsync(host, port, connect.Token);
            }
            else
            {
                await _tcp.ConnectAsync(address, port, connect.Token);
            }
        }

        _stream = _tcp.GetStream();
        if (options.Tls == ImapTls.Implicit)
        {
            await StartTlsOnStreamAsync(host, ct);
        }

        _reader = new ImapReader(_stream, options.Reader);
        using var greeting = await ReadResponseAsync(ct);
        if (greeting.Status is ImapStatus.Bye or null)
        {
            throw new ImapProtocolException("Server refused the connection: " + greeting.Text);
        }

        TakeCapabilities(greeting);
        if (options.Tls == ImapTls.StartTls)
        {
            if (Capabilities.Count == 0)
            {
                await CapabilityAsync(ct);
            }

            if (!Capabilities.Contains("STARTTLS"))
            {
                throw new ImapProtocolException("The server does not offer STARTTLS.");
            }

            await RunAsync("STARTTLS", ct);
            if (_reader.HasBufferedData)
            {
                // Bytes sent before the TLS handshake would be read as if protected (STARTTLS command injection).
                throw new ImapProtocolException("Data received before TLS started.");
            }

            await StartTlsOnStreamAsync(host, ct);
            _reader.Stream = _stream;
            Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // RFC 3501 6.2.1: forget pre-TLS capabilities
        }

        if (Capabilities.Count == 0)
        {
            await CapabilityAsync(ct);
        }
    }

    private async Task StartTlsOnStreamAsync(string host, CancellationToken ct)
    {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false, options.CertificateValidation);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        _stream = ssl;
        TlsActive = true;
    }

    public async Task CapabilityAsync(CancellationToken ct) => await RunAsync("CAPABILITY", ct);

    /// <summary>Signs in: AUTHENTICATE PLAIN when offered (UTF-8 safe), otherwise LOGIN.</summary>
    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        if (!TlsActive)
        {
            throw new ImapProtocolException("Refusing to send a password without TLS.");
        }

        if (Capabilities.Contains("AUTH=PLAIN"))
        {
            // RFC 4616: authzid NUL authcid NUL passwd.
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + user + "\0" + password));
            if (Capabilities.Contains("SASL-IR"))
            {
                await RunAsync("AUTHENTICATE PLAIN " + token, ct, redact: true);
            }
            else
            {
                await RunAsync([Encoding.ASCII.GetBytes("AUTHENTICATE PLAIN")], ct, continuation: token);
            }
        }
        else if (Capabilities.Contains("LOGINDISABLED"))
        {
            throw new ImapProtocolException("The server does not allow LOGIN.");
        }
        else
        {
            await RunAsync([Encoding.ASCII.GetBytes("LOGIN "), .. StringArg(user), Encoding.ASCII.GetBytes(" "), .. StringArg(password)], ct, redact: true);
        }

        // Capabilities may change after authentication (RFC 3501 7.2.1); servers often send them in the OK code.
        if (Capabilities.Count == 0)
        {
            await CapabilityAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ImapFolder>> ListAsync(CancellationToken ct)
    {
        var folders = new List<ImapFolder>();
        var cmd = Capabilities.Contains("SPECIAL-USE") && Capabilities.Contains("LIST-EXTENDED")
            ? "LIST \"\" \"*\" RETURN (SPECIAL-USE)"
            : "LIST \"\" \"*\"";
        await RunAsync([Encoding.ASCII.GetBytes(cmd)], ct, untagged: r =>
        {
            if (r.Kind == "LIST" && r.Data.Count >= 4)
            {
                var attrs = r.Data[1].AsList().Select(a => a.AsText() ?? "").ToList();
                var delimiter = r.Data[2].AsText();
                var raw = r.Data[3].AsText() ?? "";
                folders.Add(new ImapFolder(ModifiedUtf7.Decode(raw), raw, delimiter, attrs));
            }
        });
        return folders;
    }

    /// <summary>EXAMINE (read-only SELECT): the import never changes the source mailbox.</summary>
    public async Task<ImapSelected> ExamineAsync(ImapFolder folder, CancellationToken ct)
    {
        uint validity = 0;
        uint? next = null;
        long exists = 0;
        await RunAsync([Encoding.ASCII.GetBytes("EXAMINE "), .. StringArg(folder.RawName)], ct, untagged: r =>
        {
            if (r.Status == ImapStatus.Ok && r.Code == "UIDVALIDITY" && r.CodeArgs.FirstOrDefault()?.AsNumber() is { } v)
            {
                validity = (uint)v;
            }
            else if (r.Status == ImapStatus.Ok && r.Code == "UIDNEXT" && r.CodeArgs.FirstOrDefault()?.AsNumber() is { } n)
            {
                next = (uint)n;
            }
            else if (r.Kind == "EXISTS" && r.Data[0].AsNumber() is { } e)
            {
                exists = e;
            }
        });
        return new ImapSelected(folder.Name, validity, exists, next);
    }

    /// <summary>UID SEARCH with criteria such as "ALL" or "UID 120:*" (RFC 3501 6.4.4).</summary>
    public async Task<IReadOnlyList<uint>> UidSearchAsync(string criteria, CancellationToken ct)
    {
        var uids = new List<uint>();
        await RunAsync([Encoding.ASCII.GetBytes("UID SEARCH " + criteria)], ct, untagged: r =>
        {
            if (r.Kind == "SEARCH")
            {
                uids.AddRange(r.Data.Skip(1).Select(v => v.AsNumber()).Where(v => v is > 0 and <= uint.MaxValue).Select(v => (uint)v!.Value));
            }
        });
        return uids.Distinct().Order().ToList();
    }

    /// <summary>
    /// UID FETCH of the given UIDs. <paramref name="withBody"/> adds BODY.PEEK[] (never sets \Seen). Each message is handed
    /// to <paramref name="onMessage"/> while its literal is still open, then released.
    /// </summary>
    public async Task UidFetchAsync(IReadOnlyCollection<uint> uids, bool withBody, Func<ImapFetched, Task> onMessage, CancellationToken ct)
    {
        if (uids.Count == 0)
        {
            return;
        }

        var items = "UID FLAGS INTERNALDATE RFC822.SIZE" + (withBody ? " BODY.PEEK[]" : "") + (IsGmail ? " X-GM-LABELS" : "");
        await RunAsync([Encoding.ASCII.GetBytes($"UID FETCH {ImapArgs.UidSet(uids)} ({items})")], ct, untaggedAsync: async r =>
        {
            if (r.Kind != "FETCH" || r.Data.Count < 3)
            {
                return;
            }

            var fetched = ParseFetch(r.Data[2].AsList());
            if (fetched is not null && (!withBody || fetched.Body is not null))
            {
                await onMessage(fetched);
            }
        });
    }

    internal static ImapFetched? ParseFetch(IReadOnlyList<ImapValue> attrs)
    {
        uint? uid = null;
        var flags = new List<string>();
        var labels = new List<string>();
        DateTimeOffset? date = null;
        long? size = null;
        ImapString? body = null;
        for (var i = 0; i + 1 < attrs.Count; i += 2)
        {
            var name = (attrs[i].AsText() ?? "").ToUpperInvariant();
            var value = attrs[i + 1];
            switch (name)
            {
                case "UID":
                    uid = value.AsNumber() is { } u and > 0 and <= uint.MaxValue ? (uint)u : null;
                    break;
                case "FLAGS":
                    flags.AddRange(value.AsList().Select(f => f.AsText() ?? ""));
                    break;
                case "INTERNALDATE":
                    date = ImapArgs.TryParseInternalDate(value.AsText(), out var d) ? d : null;
                    break;
                case "RFC822.SIZE":
                    size = value.AsNumber();
                    break;
                case "X-GM-LABELS":
                    labels.AddRange(value.AsList().Select(l => l.AsText()).OfType<string>().Select(ModifiedUtf7.Decode));
                    break;
                default:
                    if (name.StartsWith("BODY[]", StringComparison.Ordinal) || name.StartsWith("BINARY[]", StringComparison.Ordinal) || name == "RFC822")
                    {
                        body = value as ImapString;
                    }

                    break;
            }
        }

        return uid is null ? null : new ImapFetched(uid.Value, flags, date, size, body, labels);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync("LOGOUT", ct);
        }
        catch (Exception e) when (e is IOException or ImapProtocolException or ObjectDisposedException)
        {
            // Already gone: nothing to tell the server.
        }
    }

    // ---- command machinery ----

    private Task RunAsync(string command, CancellationToken ct, bool redact = false) =>
        RunAsync([Encoding.ASCII.GetBytes(command)], ct, redact: redact);

    /// <summary>
    /// Sends one command made of text parts and literal parts (<see cref="StringArg"/> marks literals with a leading null
    /// part), handles continuation requests and returns once its tagged response arrives; NO/BAD throw.
    /// </summary>
    private async Task RunAsync(IReadOnlyList<byte[]?> parts, CancellationToken ct, Action<ImapResponse>? untagged = null,
        Func<ImapResponse, Task>? untaggedAsync = null, string? continuation = null, bool redact = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.CommandTimeout);
        var t = timeout.Token;
        var tag = "A" + (++_tag).ToString("D4", CultureInfo.InvariantCulture);
        var name = redact ? "(credentials)" : Encoding.ASCII.GetString(parts[0] ?? []);
        await WriteAsync(Encoding.ASCII.GetBytes(tag + " "), t);
        var literalPlus = Capabilities.Contains("LITERAL+");
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not null)
            {
                await WriteAsync(parts[i]!, t);
                continue;
            }

            // A literal: "{n}" then wait for "+" (or "{n+}" with LITERAL+, RFC 7888).
            var literal = parts[++i]!;
            await WriteAsync(Encoding.ASCII.GetBytes($"{{{literal.Length}{(literalPlus ? "+" : "")}}}\r\n"), t);
            if (!literalPlus)
            {
                await WaitContinuationAsync(tag, name, t);
            }

            await WriteAsync(literal, t);
        }

        await WriteAsync("\r\n"u8.ToArray(), t);
        await _stream.FlushAsync(t);

        while (true)
        {
            using var r = await ReadResponseAsync(t);
            if (r.IsContinuation)
            {
                if (continuation is null)
                {
                    throw new ImapProtocolException($"{name}: unexpected continuation request.");
                }

                await WriteAsync(Encoding.ASCII.GetBytes(continuation + "\r\n"), t);
                await _stream.FlushAsync(t);
                continuation = null;
                continue;
            }

            if (r.IsUntagged)
            {
                TakeCapabilities(r);
                if (r.Status == ImapStatus.Bye && name != "LOGOUT")
                {
                    throw new ImapCommandException(name, r);
                }

                untagged?.Invoke(r);
                if (untaggedAsync is not null)
                {
                    await untaggedAsync(r);
                }

                continue;
            }

            if (r.Tag != tag)
            {
                throw new ImapProtocolException($"{name}: response for unknown tag {r.Tag}.");
            }

            TakeCapabilities(r);
            if (r.Status != ImapStatus.Ok)
            {
                throw new ImapCommandException(name, r);
            }

            return;
        }
    }

    private async Task WaitContinuationAsync(string tag, string name, CancellationToken ct)
    {
        await _stream.FlushAsync(ct);
        while (true)
        {
            using var r = await ReadResponseAsync(ct);
            if (r.IsContinuation)
            {
                return;
            }

            if (r.Tag == tag)
            {
                throw new ImapCommandException(name, r);
            }

            TakeCapabilities(r);
        }
    }

    /// <summary>A string argument: quoted when possible, otherwise a literal (null marker + bytes).</summary>
    private static IEnumerable<byte[]?> StringArg(string value)
    {
        if (ImapArgs.TryQuote(value) is { } quoted)
        {
            yield return Encoding.ASCII.GetBytes(quoted);
            yield break;
        }

        yield return null;
        yield return Encoding.UTF8.GetBytes(value);
    }

    private async Task<ImapResponse> ReadResponseAsync(CancellationToken ct)
    {
        var line = await _reader!.ReadLineAsync(null, ct) ?? throw new IOException("The IMAP server closed the connection.");
        try
        {
            return ImapResponseParser.Parse(line);
        }
        catch
        {
            line.Dispose();
            throw;
        }
    }

    private void TakeCapabilities(ImapResponse r)
    {
        if (r.Code == "CAPABILITY")
        {
            Capabilities = r.CodeArgs.Select(a => a.AsText() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else if (r.Kind == "CAPABILITY")
        {
            Capabilities = r.Data.Skip(1).Select(a => a.AsText() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private ValueTask WriteAsync(byte[] bytes, CancellationToken ct) => _stream.WriteAsync(bytes, ct);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp?.Dispose();
    }
}

/// <summary>Certificate checks for the import client.</summary>
public static class ImapCertificates
{
    /// <summary>
    /// The system's validation, plus certificates pinned by exact match (SHA-256 of the DER). Pinning exists for test
    /// servers with their own certificate (BM_IMPORT_TRUSTED_CERTS in dev); a pinned certificate is trusted as such.
    /// </summary>
    public static RemoteCertificateValidationCallback WithPinned(IReadOnlyCollection<System.Security.Cryptography.X509Certificates.X509Certificate2> pinned)
    {
        var hashes = pinned.Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c.RawData))).ToHashSet(StringComparer.Ordinal);
        return (_, cert, _, errors) => errors == SslPolicyErrors.None
                                       || (cert is not null && hashes.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData()))));
    }
}
