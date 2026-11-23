using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BlueMail.Protocols.Smtp;

/// <summary>A server reply: code, enhanced status (if any) and text lines.</summary>
public sealed record SmtpResponse(int Code, string? Enhanced, IReadOnlyList<string> Lines)
{
    public bool IsPositive => Code is >= 200 and < 400;
    public bool IsTransient => Code is >= 400 and < 500;
    public bool IsPermanent => Code >= 500;
    public string Text => string.Join(' ', Lines);

    /// <summary>"250 2.0.0 Ok" - the enhanced code is already the start of the first line, so it is not repeated.</summary>
    public override string ToString() => Code.ToString(CultureInfo.InvariantCulture) + " " + Text;
}

public enum TlsRequirement { Opportunistic, Required }

public sealed record SmtpClientOptions
{
    public required string Helo { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);   // RFC 5321 4.5.3.2
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TlsRequirement Tls { get; init; } = TlsRequirement.Opportunistic;

    /// <summary>When TLS is required (MTA-STS enforce), the certificate must be valid for one of these names.</summary>
    public IReadOnlyList<string> ValidCertificateNames { get; init; } = [];

    public IPAddress? LocalAddress { get; init; }
}

public sealed class SmtpClientException(string message, SmtpResponse? response = null, bool transient = true) : Exception(message)
{
    public SmtpResponse? Response { get; } = response;
    public bool Transient { get; } = transient;
}

/// <summary>Result per recipient of one transaction.</summary>
public sealed record RecipientResult(string Recipient, SmtpResponse Response);

/// <summary>
/// SMTP client for delivery to other MTAs (RFC 5321) with ESMTP extensions it uses when offered: STARTTLS (RFC 3207),
/// SIZE (RFC 1870), 8BITMIME (RFC 6152), SMTPUTF8 (RFC 6531), PIPELINING is not required, CHUNKING/BDAT (RFC 3030).
/// One connection may carry several transactions (RSET between them).
/// </summary>
public sealed class SmtpClient : IAsyncDisposable
{
    private readonly SmtpClientOptions _options;
    private TcpClient? _tcp;
    private Stream _stream = Stream.Null;
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public SmtpClient(SmtpClientOptions options) => _options = options;

    public IReadOnlySet<string> Extensions { get; private set; } = new HashSet<string>();
    public long? MaxSize { get; private set; }
    public bool TlsActive { get; private set; }
    public string? TlsProtocol { get; private set; }
    public string? RemoteHost { get; private set; }
    public IPAddress? RemoteAddress { get; private set; }

    public async Task<SmtpResponse> ConnectAsync(string host, IPAddress address, int port, CancellationToken ct)
    {
        RemoteHost = host;
        RemoteAddress = address;
        _tcp = new TcpClient(address.AddressFamily);
        if (_options.LocalAddress is { } local)
        {
            _tcp.Client.Bind(new IPEndPoint(local, 0));
        }

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_options.ConnectTimeout);
            try
            {
                await _tcp.ConnectAsync(address, port, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SmtpClientException("connect timeout");
            }
            catch (SocketException e)
            {
                throw new SmtpClientException("connect failed: " + e.SocketErrorCode);
            }
        }

        _stream = _tcp.GetStream();
        var greeting = await ReadAsync(_options.CommandTimeout, ct);
        if (greeting.Code != 220)
        {
            throw new SmtpClientException("refused at greeting", greeting, !greeting.IsPermanent);
        }

        await HelloAsync(ct);
        if (Extensions.Contains("STARTTLS"))
        {
            await StartTlsAsync(host, ct);
        }
        else if (_options.Tls == TlsRequirement.Required)
        {
            throw new SmtpClientException("TLS required by policy but STARTTLS not offered", transient: true);
        }

        return greeting;
    }

    private async Task HelloAsync(CancellationToken ct)
    {
        var r = await CommandAsync("EHLO " + _options.Helo, ct);
        if (!r.IsPositive)
        {
            r = await CommandAsync("HELO " + _options.Helo, ct);
            if (!r.IsPositive)
            {
                throw new SmtpClientException("HELO refused", r, !r.IsPermanent);
            }

            Extensions = new HashSet<string>();
            return;
        }

        var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MaxSize = null;
        foreach (var line in r.Lines.Skip(1))
        {
            var parts = line.Split(' ', 2);
            ext.Add(parts[0].ToUpperInvariant());
            if (parts[0].Equals("SIZE", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0)
            {
                MaxSize = max;
            }
        }

        Extensions = ext;
    }

    private async Task StartTlsAsync(string host, CancellationToken ct)
    {
        var r = await CommandAsync("STARTTLS", ct);
        if (r.Code != 220)
        {
            if (_options.Tls == TlsRequirement.Required)
            {
                throw new SmtpClientException("STARTTLS refused", r);
            }

            return; // opportunistic: continue in plaintext
        }

        var required = _options.Tls == TlsRequirement.Required;
        var names = _options.ValidCertificateNames;
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false, (_, cert, chain, errors) =>
        {
            if (!required)
            {
                return true; // RFC 7435 opportunistic TLS: encrypt, do not authenticate
            }

            // MTA-STS (RFC 8461 4.2): a trusted chain and a name matching the MX host.
            if ((errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None || cert is null)
            {
                return false;
            }

            var c2 = cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            return names.Count == 0 ? errors == SslPolicyErrors.None : names.Any(n => c2.MatchesHostname(n));
        });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
        }
        catch (AuthenticationException e)
        {
            throw new SmtpClientException("TLS handshake failed: " + e.Message);
        }

        _stream = ssl;
        _start = _end = 0;
        TlsActive = true;
        TlsProtocol = ssl.SslProtocol.ToString();
        await HelloAsync(ct); // RFC 3207 4.2: forget what was learned before TLS
    }

    /// <summary>
    /// One transaction. Returns per-recipient results; the final reply applies to every accepted recipient.
    /// Throws for failures of the whole transaction (MAIL refused, DATA refused, connection lost).
    /// </summary>
    public async Task<(IReadOnlyList<RecipientResult> Rcpts, SmtpResponse? Final)> SendAsync(string from, IReadOnlyList<string> recipients, byte[] message,
        bool smtpUtf8, bool eightBit, CancellationToken ct)
    {
        if (smtpUtf8 && !Extensions.Contains("SMTPUTF8"))
        {
            throw new SmtpClientException("recipient server does not support SMTPUTF8", transient: false);
        }

        if (MaxSize is { } max && message.LongLength > max)
        {
            throw new SmtpClientException($"message too big for this server ({message.LongLength} > {max})", transient: false);
        }

        var mail = new StringBuilder("MAIL FROM:<").Append(from).Append('>');
        if (Extensions.Contains("SIZE"))
        {
            mail.Append(" SIZE=").Append(message.LongLength.ToString(CultureInfo.InvariantCulture));
        }

        if (eightBit && Extensions.Contains("8BITMIME"))
        {
            mail.Append(" BODY=8BITMIME");
        }

        if (smtpUtf8)
        {
            mail.Append(" SMTPUTF8");
        }

        var m = await CommandAsync(mail.ToString(), ct);
        if (!m.IsPositive)
        {
            throw new SmtpClientException("MAIL FROM refused", m, !m.IsPermanent);
        }

        var results = new List<RecipientResult>();
        foreach (var rcpt in recipients)
        {
            results.Add(new RecipientResult(rcpt, await CommandAsync("RCPT TO:<" + rcpt + ">", ct)));
        }

        if (!results.Any(r => r.Response.IsPositive))
        {
            await CommandAsync("RSET", ct);
            return (results, null);
        }

        SmtpResponse final;
        if (Extensions.Contains("CHUNKING"))
        {
            await WriteAsync(Encoding.ASCII.GetBytes("BDAT " + message.LongLength.ToString(CultureInfo.InvariantCulture) + " LAST\r\n"), ct);
            await _stream.WriteAsync(message, ct);
            await _stream.FlushAsync(ct);
            final = await ReadAsync(_options.DataTimeout, ct);
        }
        else
        {
            var d = await CommandAsync("DATA", ct);
            if (d.Code != 354)
            {
                throw new SmtpClientException("DATA refused", d, !d.IsPermanent);
            }

            await WriteDotStuffedAsync(message, ct);
            final = await ReadAsync(_options.DataTimeout, ct);
        }

        return (results, final);
    }

    /// <summary>RFC 5321 4.5.2: a leading "." gets doubled; the message ends with CRLF.CRLF. Bare LF becomes CRLF.</summary>
    private async Task WriteDotStuffedAsync(byte[] message, CancellationToken ct)
    {
        using var ms = new MemoryStream(message.Length + 1024);
        var lineStart = true;
        for (var i = 0; i < message.Length; i++)
        {
            var b = message[i];
            if (lineStart && b == (byte)'.')
            {
                ms.WriteByte((byte)'.');
            }

            if (b == (byte)'\n' && (i == 0 || message[i - 1] != (byte)'\r'))
            {
                ms.WriteByte((byte)'\r');
            }

            ms.WriteByte(b);
            lineStart = b == (byte)'\n';
        }

        if (!lineStart)
        {
            ms.Write("\r\n"u8);
        }

        ms.Write(".\r\n"u8);
        await _stream.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
        await _stream.FlushAsync(ct);
    }

    public async Task ResetAsync(CancellationToken ct) => await CommandAsync("RSET", ct);

    public async Task QuitAsync(CancellationToken ct)
    {
        try
        {
            await CommandAsync("QUIT", ct);
        }
        catch (Exception e) when (e is SmtpClientException or IOException)
        {
            // already closed
        }
    }

    private async Task<SmtpResponse> CommandAsync(string line, CancellationToken ct)
    {
        await WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct);
        return await ReadAsync(_options.CommandTimeout, ct);
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        catch (IOException e)
        {
            throw new SmtpClientException("connection lost: " + e.Message);
        }
    }

    private async Task<SmtpResponse> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var lines = new List<string>();
        int code = 0;
        string? enhanced = null;
        try
        {
            while (true)
            {
                var line = await ReadLineAsync(cts.Token);
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var c))
                {
                    throw new SmtpClientException("malformed reply: " + Truncate(line));
                }

                code = c;
                var more = line.Length > 3 && line[3] == '-';
                var text = line.Length > 4 ? line[4..] : "";
                if (enhanced is null && text.Length >= 5 && char.IsAsciiDigit(text[0]) && text[1] == '.')
                {
                    var sp = text.IndexOf(' ');
                    var candidate = sp < 0 ? text : text[..sp];
                    if (candidate.Count(ch => ch == '.') == 2)
                    {
                        enhanced = candidate;
                    }
                }

                lines.Add(text);
                if (!more)
                {
                    return new SmtpResponse(code, enhanced, lines);
                }

                if (lines.Count > 200)
                {
                    throw new SmtpClientException("reply too long");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmtpClientException("timeout waiting for reply");
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] == (byte)'\n')
                {
                    sb.Append(Encoding.UTF8.GetString(_buffer, _start, i - _start));
                    _start = i + 1;
                    return sb.ToString().TrimEnd('\r');
                }
            }

            sb.Append(Encoding.UTF8.GetString(_buffer, _start, _end - _start));
            if (sb.Length > 4096)
            {
                throw new SmtpClientException("reply line too long");
            }

            int n;
            try
            {
                n = await _stream.ReadAsync(_buffer, ct);
            }
            catch (IOException e)
            {
                throw new SmtpClientException("connection lost: " + e.Message);
            }

            if (n == 0)
            {
                throw new SmtpClientException("connection closed by server");
            }

            _start = 0;
            _end = n;
        }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp?.Dispose();
    }
}
