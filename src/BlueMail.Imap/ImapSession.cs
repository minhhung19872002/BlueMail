using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;
using BlueMail.Protocols.Smtp;

namespace BlueMail.Imap;

public sealed record ImapServerOptions
{
    public required string Hostname { get; init; }
    public long MaxAppendBytes { get; init; } = 50L * 1024 * 1024;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxBadCommands { get; init; } = 20;
}

/// <summary>An IMAP folder: a label of the mailbox (Gmail-like: one message can be in many folders).</summary>
internal sealed record Folder(Guid LabelId, string? SystemKey, string Name, string? SpecialUse, bool Subscribed, bool HasChildren)
{
    public string Wire => ModifiedUtf7.Encode(Name);
    public bool IsTrashOrSpam => SystemKey is SystemLabels.Trash or SystemLabels.Spam;
}

internal sealed class Selected(Folder folder, bool readOnly)
{
    public Folder Folder { get; } = folder;
    public bool ReadOnly { get; } = readOnly;
    public List<ImapMessageRow> Rows { get; set; } = [];
    public long Modseq { get; set; }
    public long UidValidity { get; set; }
    public long UidNext { get; set; }

    /// <summary>\Recent for this session (RFC 3501 2.3.2): messages that arrived since another session last selected the folder.</summary>
    public HashSet<long> Recent { get; } = [];

    /// <summary>Keywords announced in FLAGS; a new one is announced before it is used (RFC 3501 7.2.6).</summary>
    public SortedSet<string> Keywords { get; } = new(StringComparer.Ordinal);
}

internal sealed class ImapBadException(string message) : Exception(message);

internal sealed class ImapNoException(string text, string? code = null) : Exception(text)
{
    public string? Code { get; } = code;
}

/// <summary>
/// One IMAP4rev1 connection (RFC 3501) with IDLE, UIDPLUS, MOVE, SPECIAL-USE, LIST-EXTENDED, CONDSTORE/QRESYNC,
/// LITERAL+, NAMESPACE, ID, ENABLE, UNSELECT, CHILDREN, COMPRESS=DEFLATE, SASL-IR. Labels are folders; mail changes go
/// through the mail store like every other client.
/// </summary>
internal sealed partial class ImapSession(
    Stream stream, bool implicitTls, IPAddress ip, IServiceProvider services, ImapServerOptions options, Func<System.Security.Cryptography.X509Certificates.X509Certificate2> certificate,
    MailLoginService logins, ImapSessionRegistry registry, ILogger log) : IDisposable
{
    private Stream _stream = stream;
    private ImapReader _reader = null!;
    private ImapResponseWriter _w = null!;
    private bool _tls = implicitTls;
    private MailLogin? _login;
    private MailboxAccess? _access;
    private Selected? _sel;
    private bool _condstore;
    private bool _qresync;
    private int _bad;
    private volatile bool _revoked;
    private readonly SemaphoreSlim _mailboxChanged = new(0);

    /// <summary>Per folder, the highest UID some session has already seen (\Recent belongs to the first one, process-wide).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> RecentSeen = new();

    /// <summary>Makes sure the folder has a "seen up to" mark (before mail is added to it, so the new mail counts as recent).</summary>
    internal static void ObserveFolder(Guid label, long highestUid) => RecentSeen.TryAdd(label, highestUid);

    private string Capabilities()
    {
        var caps = new List<string> { "IMAP4rev1", "LITERAL+", "SASL-IR", "ID", "ENABLE", "IDLE", "NAMESPACE", "UNSELECT", "CHILDREN" };
        if (_login is null)
        {
            if (!_tls)
            {
                caps.Add("STARTTLS");
                caps.Add("LOGINDISABLED"); // RFC 3501 6.2.3 / RFC 8314: no passwords over plaintext
            }
            else
            {
                caps.AddRange(Sasl.Mechanisms.Select(m => "AUTH=" + m));
            }
        }
        else
        {
            caps.AddRange(["UIDPLUS", "MOVE", "SPECIAL-USE", "LIST-EXTENDED", "CONDSTORE", "QRESYNC"]);
            if (!_w.Compressed)
            {
                caps.Add("COMPRESS=DEFLATE");
            }
        }

        return string.Join(' ', caps);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_tls)
        {
            _stream = await TlsAsync(_stream, ct);
        }

        _reader = new ImapReader(_stream, new ImapReaderOptions { MaxLiteralLength = options.MaxAppendBytes, SpoolThreshold = 1024 * 1024 });
        _w = new ImapResponseWriter(_stream);
        try
        {
            _w.Line("* OK [CAPABILITY " + Capabilities() + "] " + options.Hostname + " BlueMail IMAP ready");
            await _w.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                if (_revoked)
                {
                    _w.Line("* BYE Access revoked");
                    await _w.FlushAsync(ct);
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_login is null ? options.AuthTimeout : options.CommandTimeout);
                using var line = await _reader.ReadLineAsync(BeforeLiteralAsync, timeout.Token);
                if (line is null)
                {
                    return;
                }

                if (!await HandleAsync(line, ct))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TrySayAsync("* BYE Autologout; idle for too long");
        }
        catch (ImapProtocolException e)
        {
            await TrySayAsync("* BYE " + e.Message);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException)
        {
            log.LogDebug(e, "IMAP connection from {Ip} ended", ip);
        }
        finally
        {
            registry.Remove(this);
            Dispose();
        }
    }

    public void Dispose()
    {
        _w?.Dispose();
        _mailboxChanged.Dispose();
    }

    private async Task TrySayAsync(string line)
    {
        try
        {
            _w.Line(line);
            await _w.FlushAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // gone already
        }
    }

    private async ValueTask BeforeLiteralAsync(long size, bool nonSync)
    {
        if (_login is null && size > 64 * 1024)
        {
            throw new ImapProtocolException("Literal too large before authentication");
        }

        if (!nonSync)
        {
            _w.Line("+ Ready for literal data");
            await _w.FlushAsync(CancellationToken.None);
        }
    }

    private async Task<Stream> TlsAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate(),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        return ssl;
    }

    /// <summary>Runs one command; false ends the connection.</summary>
    private async Task<bool> HandleAsync(ImapLine line, CancellationToken ct)
    {
        var p = new ImapParser(line);
        var tag = p.ReadWord();
        p.SkipSpaces();
        var command = p.ReadAtom(stopAtBracket: false).ToUpperInvariant();
        if (tag.Length == 0 || command.Length == 0 || tag.Any(c => c is '+' or '*' or '(' or ')' or '{' or '%' or '"' or '\\'))
        {
            _w.Line("* BAD Invalid command");
            await _w.FlushAsync(ct);
            return ++_bad < options.MaxBadCommands;
        }

        var uid = false;
        if (command == "UID")
        {
            uid = true;
            p.SkipSpaces();
            command = p.ReadAtom(stopAtBracket: false).ToUpperInvariant();
        }

        try
        {
            var keepOpen = await DispatchAsync(tag, command, uid, p, ct);
            await _w.FlushAsync(ct);
            return keepOpen;
        }
        catch (ImapBadException e)
        {
            _w.Line(tag + " BAD " + e.Message);
            await _w.FlushAsync(ct);
            return ++_bad < options.MaxBadCommands;
        }
        catch (ImapProtocolException e)
        {
            _w.Line(tag + " BAD " + e.Message);
            await _w.FlushAsync(ct);
            return ++_bad < options.MaxBadCommands;
        }
        catch (ImapNoException e)
        {
            _w.Line(tag + " NO " + (e.Code is null ? "" : "[" + e.Code + "] ") + e.Message);
            await _w.FlushAsync(ct);
            return true;
        }
        catch (MailboxAccessDeniedException)
        {
            _w.Line(tag + " NO [NOPERM] Permission denied");
            await _w.FlushAsync(ct);
            return true;
        }
    }

    private async Task<bool> DispatchAsync(string tag, string cmd, bool uid, ImapParser p, CancellationToken ct)
    {
        // Commands valid in any state.
        switch (cmd)
        {
            case "CAPABILITY":
                End(p);
                _w.Line("* CAPABILITY " + Capabilities());
                Ok(tag, "CAPABILITY completed");
                return true;
            case "NOOP":
                End(p);
                await RefreshAsync(allowExpunge: true, ct);
                Ok(tag, "NOOP completed");
                return true;
            case "LOGOUT":
                End(p);
                _w.Line("* BYE " + options.Hostname + " logging out");
                Ok(tag, "LOGOUT completed");
                return false;
            case "ID":
                p.SkipSpaces();
                _ = p.ReadValue(stopAtBracket: false);
                End(p);
                _w.Line("* ID (\"name\" \"BlueMail\" \"vendor\" \"BlueMail\" \"support-url\" \"https://" + options.Hostname + "\")");
                Ok(tag, "ID completed");
                return true;
        }

        if (_login is null)
        {
            switch (cmd)
            {
                case "STARTTLS":
                    End(p);
                    if (_tls)
                    {
                        throw new ImapBadException("TLS already active");
                    }

                    Ok(tag, "Begin TLS negotiation now");
                    await _w.FlushAsync(ct);
                    if (_reader.HasBufferedData)
                    {
                        throw new ImapProtocolException("Data received before TLS started"); // STARTTLS command injection
                    }

                    _stream = await TlsAsync(_stream, ct);
                    _reader.Stream = _stream;
                    _w.ReplaceStream(_stream);
                    _tls = true;
                    return true;
                case "LOGIN":
                    {
                        var user = AString(p);
                        var pass = AString(p);
                        End(p);
                        if (!_tls)
                        {
                            throw new ImapNoException("Use STARTTLS or port 993 first", "PRIVACYREQUIRED");
                        }

                        await SignInAsync(tag, new SaslCredentials("LOGIN", user, pass, false), ct);
                        return true;
                    }

                case "AUTHENTICATE":
                    await AuthenticateAsync(tag, p, ct);
                    return true;
                default:
                    throw new ImapBadException(cmd is "SELECT" or "EXAMINE" or "LIST" or "FETCH" ? "Not authenticated" : "Unknown command " + cmd);
            }
        }

        switch (cmd)
        {
            case "ENABLE":
                {
                    var enabled = new List<string>();
                    while (!p.AtEnd)
                    {
                        var ext = AString(p).ToUpperInvariant();
                        if (ext is "CONDSTORE" or "QRESYNC")
                        {
                            _condstore = true;
                            _qresync |= ext == "QRESYNC";
                            enabled.Add(ext);
                        }
                    }

                    _w.Line("* ENABLED" + string.Concat(enabled.Select(e => " " + e)));
                    Ok(tag, "ENABLE completed");
                    return true;
                }

            case "NAMESPACE":
                End(p);
                _w.Line("* NAMESPACE ((\"\" \"/\")) NIL NIL");
                Ok(tag, "NAMESPACE completed");
                return true;
            case "COMPRESS":
                {
                    var alg = AString(p).ToUpperInvariant();
                    End(p);
                    if (alg != "DEFLATE")
                    {
                        throw new ImapBadException("Only DEFLATE");
                    }

                    if (_w.Compressed)
                    {
                        throw new ImapNoException("Compression already active", "COMPRESSIONACTIVE");
                    }

                    Ok(tag, "DEFLATE active");
                    await _w.FlushAsync(ct);
                    if (_reader.HasBufferedData)
                    {
                        throw new ImapProtocolException("Data received before compression started");
                    }

                    _w.StartDeflate();
                    _reader.Stream = new DeflateStream(_stream, CompressionMode.Decompress, leaveOpen: true);
                    return true;
                }

            case "LIST" or "LSUB":
                await ListAsync(tag, cmd == "LSUB", p, ct);
                return true;
            case "STATUS":
                await StatusAsync(tag, p, ct);
                return true;
            case "SELECT" or "EXAMINE":
                await SelectAsync(tag, cmd == "EXAMINE", p, ct);
                return true;
            case "CREATE":
                await CreateAsync(tag, p, ct);
                return true;
            case "DELETE":
                await DeleteAsync(tag, p, ct);
                return true;
            case "RENAME":
                await RenameAsync(tag, p, ct);
                return true;
            case "SUBSCRIBE" or "UNSUBSCRIBE":
                {
                    var name = MailboxName(p);
                    End(p);
                    var f = await FindFolderAsync(name, ct) ?? throw new ImapNoException("No such mailbox", "NONEXISTENT");
                    await WithStoreAsync(s => s.SetSubscribedAsync(_access!, f.LabelId, cmd == "SUBSCRIBE", ct));
                    Ok(tag, cmd + " completed");
                    return true;
                }

            case "APPEND":
                await AppendAsync(tag, p, ct);
                return true;
            case "IDLE":
                End(p);
                await IdleAsync(tag, ct);
                return true;
        }

        if (_sel is null)
        {
            throw new ImapBadException(cmd is "FETCH" or "STORE" or "SEARCH" or "COPY" or "MOVE" or "EXPUNGE" or "CLOSE" or "UNSELECT" or "CHECK"
                ? "No mailbox selected" : "Unknown command " + cmd);
        }

        switch (cmd)
        {
            case "CHECK":
                End(p);
                await RefreshAsync(allowExpunge: true, ct);
                Ok(tag, "CHECK completed");
                return true;
            case "CLOSE" or "UNSELECT":
                End(p);
                if (cmd == "CLOSE" && !_sel.ReadOnly)
                {
                    await ExpungeCoreAsync(null, report: false, ct); // RFC 3501 6.4.2: silent expunge
                }

                _sel = null;
                registry.Unwatch(this);
                if (_qresync)
                {
                    _w.Line("* OK [CLOSED] Previous mailbox closed"); // RFC 7162 3.2.11
                }

                Ok(tag, cmd + " completed");
                return true;
            case "EXPUNGE":
                {
                    ImapSequenceSet? set = null;
                    if (uid)
                    {
                        set = SequenceSet(p);
                    }

                    End(p);
                    RequireWritable();
                    await ExpungeCoreAsync(set, report: true, ct);
                    Ok(tag, "EXPUNGE completed");
                    return true;
                }

            case "FETCH":
                await FetchAsync(tag, uid, p, ct);
                return true;
            case "STORE":
                await StoreAsync(tag, uid, p, ct);
                return true;
            case "SEARCH":
                await SearchAsync(tag, uid, p, ct);
                return true;
            case "COPY" or "MOVE":
                await CopyAsync(tag, uid, cmd == "MOVE", p, ct);
                return true;
            default:
                throw new ImapBadException("Unknown command " + cmd);
        }
    }

    // ---- helpers ----

    private void Ok(string tag, string text) => _w.Line(tag + " OK " + text);

    private static void End(ImapParser p)
    {
        p.SkipSpaces();
        if (!p.AtEnd)
        {
            throw new ImapBadException("Unexpected arguments");
        }
    }

    private static string AString(ImapParser p)
    {
        p.SkipSpaces();
        if (p.AtEnd)
        {
            throw new ImapBadException("Missing argument");
        }

        return p.ReadValue(stopAtBracket: false) switch
        {
            ImapList => throw new ImapBadException("String expected"),
            ImapNil => "NIL",
            var v => v.AsText() ?? throw new ImapBadException("String too large"),
        };
    }

    private static string MailboxName(ImapParser p)
    {
        var raw = AString(p);
        var name = ModifiedUtf7.Decode(raw);
        return name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : name;
    }

    private static ImapSequenceSet SequenceSet(ImapParser p)
    {
        p.SkipSpaces();
        var text = p.ReadAtom(stopAtBracket: false);
        return ImapSequenceSet.TryParse(text, out var set) ? set : throw new ImapBadException("Invalid sequence set");
    }

    private void RequireWritable()
    {
        if (_sel!.ReadOnly)
        {
            throw new ImapNoException("Mailbox is read-only", "READ-ONLY");
        }
    }

    private async Task<T> WithStoreAsync<T>(Func<PgMailStore, Task<T>> f)
    {
        await using var scope = services.CreateAsyncScope();
        return await f(scope.ServiceProvider.GetRequiredService<PgMailStore>());
    }

    private async Task WithStoreAsync(Func<PgMailStore, Task> f)
    {
        await using var scope = services.CreateAsyncScope();
        await f(scope.ServiceProvider.GetRequiredService<PgMailStore>());
    }

    // ---- authentication ----

    private async Task AuthenticateAsync(string tag, ImapParser p, CancellationToken ct)
    {
        var mechanism = AString(p).ToUpperInvariant();
        string? initial = null;
        p.SkipSpaces();
        if (!p.AtEnd)
        {
            initial = AString(p);
        }

        End(p);
        if (!_tls)
        {
            throw new ImapNoException("Use STARTTLS or port 993 first", "PRIVACYREQUIRED");
        }

        if (!Sasl.Mechanisms.Contains(mechanism))
        {
            throw new ImapNoException("Unsupported mechanism", "CANNOT");
        }

        async Task<byte[]> NextAsync(string challenge)
        {
            _w.Line("+ " + challenge);
            await _w.FlushAsync(ct);
            using var resp = await _reader.ReadLineAsync(null, ct) ?? throw new IOException("Connection closed during AUTHENTICATE");
            var text = Encoding.ASCII.GetString(resp.Text[0]);
            if (text == "*")
            {
                throw new ImapBadException("AUTHENTICATE cancelled");
            }

            return Sasl.FromBase64(text) ?? throw new ImapBadException("Invalid base64");
        }

        var first = initial is null ? await NextAsync(mechanism == "LOGIN" ? "VXNlcm5hbWU6" : "") : Sasl.FromBase64(initial) ?? throw new ImapBadException("Invalid base64");
        SaslCredentials? creds = mechanism switch
        {
            "PLAIN" => Sasl.Plain(first),
            "LOGIN" => new SaslCredentials("LOGIN", Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(await NextAsync("UGFzc3dvcmQ6")), false),
            "XOAUTH2" => Sasl.XOAuth2(first),
            _ => Sasl.OAuthBearer(first),
        };
        if (creds is null)
        {
            throw new ImapBadException("Malformed authentication data");
        }

        await SignInAsync(tag, creds, ct, async () =>
        {
            // RFC 7628 3.2.2: error challenge, the client answers with a dummy response.
            await NextAsync(Sasl.BearerError);
        });
    }

    private async Task SignInAsync(string tag, SaslCredentials creds, CancellationToken ct, Func<Task>? bearerFailure = null)
    {
        var (login, failure) = await logins.AuthenticateAsync(creds.User, creds.Secret, creds.IsBearerToken, LoginProtocol.Imap, ip, ct);
        if (login is null)
        {
            if (creds.IsBearerToken && bearerFailure is not null)
            {
                await bearerFailure();
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct); // slows down guessing
            throw failure switch
            {
                MailLoginFailure.Locked => new ImapNoException("Too many failed attempts, try later", "UNAVAILABLE"),
                MailLoginFailure.Disabled => new ImapNoException("IMAP access is not available for this account", "AUTHORIZATIONFAILED"),
                _ => new ImapNoException("Authentication failed", "AUTHENTICATIONFAILED"),
            };
        }

        _access = await WithStoreAsync(s => s.OpenAsync(login.TenantId, login.UserId, null, ct));
        _login = login;
        registry.Add(this, login.UserId);
        _w.Line(tag + " OK [CAPABILITY " + Capabilities() + "] Logged in");
    }

    public Guid? UserId => _login?.UserId;

    /// <summary>
    /// Called by the registry when the user's access changed (suspension, password change, app password revoked): the
    /// connection is closed now, also in the middle of IDLE (PROMPT 7.3). Only the stream is touched from this thread.
    /// </summary>
    public async Task RecheckAsync()
    {
        if (_login is not null && !await logins.StillValidAsync(_login, CancellationToken.None))
        {
            _revoked = true;
            _mailboxChanged.Release();
            try
            {
                await _stream.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // already closing
            }
        }
    }

    public Guid? WatchedMailbox => _sel is null ? null : _access?.MailboxId;

    public void MailboxChanged() => _mailboxChanged.Release();
}
