using System.Globalization;
using System.Net;

namespace BlueMail.Protocols.Smtp;

public sealed record SmtpServerOptions
{
    public required string Hostname { get; init; }
    public long MaxMessageBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxRecipients { get; init; } = 100;
    public int MaxCommandLineBytes { get; init; } = 4096;
    public int MaxErrors { get; init; } = 10;
    public bool StartTlsAvailable { get; init; }

    /// <summary>Submission (RFC 6409): MAIL is refused before authentication; AUTH offered only over TLS.</summary>
    public bool RequireAuthentication { get; init; }

    /// <summary>Checks SASL credentials (RFC 4954). Null: AUTH is not offered (MX).</summary>
    public Func<SaslCredentials, CancellationToken, ValueTask<SaslResult>>? Authenticate { get; init; }
}

/// <summary>Envelope of the transaction in progress.</summary>
public sealed class SmtpEnvelope
{
    public required IPAddress RemoteAddress { get; init; }
    public string Helo { get; set; } = "";
    public bool Esmtp { get; set; }
    public bool Tls { get; set; }
    public string? AuthenticatedUser { get; set; }
    public SmtpPath? From { get; set; }
    public List<SmtpPath> Recipients { get; } = [];
    public bool SmtpUtf8 { get; set; }
    public bool EightBitMime { get; set; }
    public long? DeclaredSize { get; set; }

    public void Reset()
    {
        From = null;
        Recipients.Clear();
        SmtpUtf8 = false;
        EightBitMime = false;
        DeclaredSize = null;
    }
}

/// <summary>Business decisions behind the protocol (implemented by BlueMail.Mx and BlueMail.Submission).</summary>
public interface ISmtpHandler
{
    /// <summary>Null to greet, or a reply that refuses the connection (e.g. 421/554).</summary>
    ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope session, CancellationToken ct);

    ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope session, SmtpPath from, CancellationToken ct);

    /// <summary>Null accepts the recipient; otherwise the rejection (550 5.1.1 for unknown users - never accept-then-bounce).</summary>
    ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope session, SmtpPath recipient, CancellationToken ct);

    /// <summary>
    /// Called with the complete message (dot-unstuffed, on a seekable stream). Returns the final reply; it must be
    /// positive only after the message is durably stored for every accepted recipient (PROMPT 2.1 step 8).
    /// </summary>
    ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope session, Stream message, CancellationToken ct);
}

public enum SmtpAction
{
    /// <summary>Send the replies and read the next command line.</summary>
    Continue,

    /// <summary>Send the replies, then read message data up to "." (dot-unstuffing) and call <see cref="SmtpServerSession.CompleteMessageAsync"/>.</summary>
    ReadData,

    /// <summary>Send the replies (if any), then read exactly <see cref="SmtpStep.ChunkSize"/> bytes (BDAT).</summary>
    ReadChunk,

    /// <summary>Send the replies, discard any buffered input, then negotiate TLS (RFC 3207: pipelined plaintext is dropped).</summary>
    StartTls,

    /// <summary>Send the replies and close.</summary>
    Close,
}

public sealed record SmtpStep(SmtpAction Action, IReadOnlyList<SmtpReply> Replies, long ChunkSize = 0, bool LastChunk = false)
{
    public static SmtpStep Reply(SmtpReply r) => new(SmtpAction.Continue, [r]);
}

/// <summary>
/// SMTP server state machine (RFC 5321) with PIPELINING (RFC 2920), SIZE (RFC 1870), 8BITMIME (RFC 6152),
/// SMTPUTF8 (RFC 6531), STARTTLS (RFC 3207), ENHANCEDSTATUSCODES (RFC 2034) and CHUNKING/BDAT (RFC 3030).
/// It owns no socket: the transport feeds it lines and message bytes and performs the returned actions.
/// </summary>
public sealed class SmtpServerSession(SmtpServerOptions options, ISmtpHandler handler, SmtpEnvelope envelope)
{
    private enum State { New, Greeted, Mail, Data, Bdat }

    private State _state = State.New;
    private int _errors;
    private bool _bdatFailed;
    private SmtpReply? _bdatError;
    private long _bdatBytes;
    private string? _authMechanism;
    private string? _authLoginUser;
    private bool _authBearerFailed;

    public SmtpEnvelope Envelope => envelope;
    public SmtpServerOptions Options => options;

    public async ValueTask<SmtpStep> ConnectAsync(CancellationToken ct)
    {
        var refusal = await handler.OnConnectAsync(envelope, ct);
        return refusal is not null
            ? new SmtpStep(SmtpAction.Close, [refusal])
            : SmtpStep.Reply(new SmtpReply(220, null, options.Hostname + " ESMTP BlueMail"));
    }

    /// <summary>Handles one command line (without CRLF). <paramref name="tooLong"/> when the transport cut it at the limit.</summary>
    public async ValueTask<SmtpStep> HandleLineAsync(string line, bool tooLong, CancellationToken ct)
    {
        if (tooLong)
        {
            return Error(SmtpReply.LineTooLong);
        }

        if (_authMechanism is not null)
        {
            return await AuthResponseAsync(line, ct); // a SASL response line, not a command (RFC 4954 4)
        }

        if (line.Contains('\0', StringComparison.Ordinal))
        {
            return Error(SmtpReply.SyntaxError);
        }

        var cmd = SmtpParser.Parse(line);
        if (_state == State.Bdat && cmd.Verb is not (SmtpVerb.Bdat or SmtpVerb.Rset or SmtpVerb.Quit or SmtpVerb.Noop))
        {
            return Error(SmtpReply.BadSequence); // RFC 3030 2: only BDAT, RSET, QUIT or NOOP during a BDAT transaction
        }

        switch (cmd.Verb)
        {
            case SmtpVerb.Ehlo or SmtpVerb.Helo:
                return Hello(cmd);
            case SmtpVerb.Mail:
                return await MailAsync(cmd, ct);
            case SmtpVerb.Rcpt:
                return await RcptAsync(cmd, ct);
            case SmtpVerb.Data:
                if (cmd.Argument.Length > 0)
                {
                    return Error(SmtpReply.ArgumentError);
                }

                if (_state != State.Mail || envelope.Recipients.Count == 0)
                {
                    return Error(envelope.From is null ? SmtpReply.BadSequence : new SmtpReply(554, "5.5.1", "No valid recipients"));
                }

                _state = State.Data;
                return new SmtpStep(SmtpAction.ReadData, [new SmtpReply(354, null, "End data with <CR><LF>.<CR><LF>")]);
            case SmtpVerb.Bdat:
                return Bdat(cmd);
            case SmtpVerb.Rset:
                envelope.Reset();
                _state = _state == State.New ? State.New : State.Greeted;
                ResetBdat();
                return SmtpStep.Reply(SmtpReply.Ok);
            case SmtpVerb.Noop:
                return SmtpStep.Reply(SmtpReply.Ok);
            case SmtpVerb.Quit:
                return new SmtpStep(SmtpAction.Close, [new SmtpReply(221, "2.0.0", options.Hostname + " closing connection")]);
            case SmtpVerb.Vrfy:
                return SmtpStep.Reply(new SmtpReply(252, "2.5.0", "Cannot VRFY user, but will accept message and attempt delivery"));
            case SmtpVerb.Expn:
                return SmtpStep.Reply(new SmtpReply(502, "5.5.1", "EXPN not available"));
            case SmtpVerb.Help:
                return SmtpStep.Reply(new SmtpReply(214, "2.0.0", "See RFC 5321"));
            case SmtpVerb.Auth:
                return await AuthAsync(cmd.Argument, ct);
            case SmtpVerb.StartTls:
                if (!options.StartTlsAvailable || envelope.Tls)
                {
                    return Error(envelope.Tls ? SmtpReply.BadSequence : SmtpReply.NotImplemented);
                }

                if (cmd.Argument.Length > 0)
                {
                    return Error(SmtpReply.ArgumentError);
                }

                return new SmtpStep(SmtpAction.StartTls, [new SmtpReply(220, "2.0.0", "Ready to start TLS")]);
            default:
                return Error(SmtpReply.SyntaxError);
        }
    }

    /// <summary>After TLS is established the session restarts from scratch (RFC 3207 4.2).</summary>
    public void TlsEstablished()
    {
        envelope.Tls = true;
        envelope.Helo = "";
        envelope.Esmtp = false;
        envelope.Reset();
        _state = State.New;
    }

    private SmtpStep Hello(SmtpCommand cmd)
    {
        var name = cmd.Argument.Trim();
        if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal))
        {
            return Error(new SmtpReply(501, "5.5.4", "HELO/EHLO requires a domain or address literal"));
        }

        envelope.Helo = name;
        envelope.Esmtp = cmd.Verb == SmtpVerb.Ehlo;
        envelope.Reset();
        ResetBdat();
        _state = State.Greeted;
        if (!envelope.Esmtp)
        {
            return SmtpStep.Reply(new SmtpReply(250, null, options.Hostname));
        }

        var lines = new List<string>
        {
            options.Hostname + " Hello " + name,
            "PIPELINING",
            "SIZE " + options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture),
            "8BITMIME",
            "SMTPUTF8",
            "ENHANCEDSTATUSCODES",
            "CHUNKING",
        };
        if (options.StartTlsAvailable && !envelope.Tls)
        {
            lines.Add("STARTTLS");
        }

        if (options.Authenticate is not null && envelope.Tls)
        {
            lines.Add("AUTH " + string.Join(' ', Sasl.Mechanisms)); // RFC 4954 4: never offered over plaintext
        }

        return SmtpStep.Reply(new SmtpReply(250, null, [.. lines]));
    }

    private async ValueTask<SmtpStep> MailAsync(SmtpCommand cmd, CancellationToken ct)
    {
        if (_state == State.New)
        {
            return Error(new SmtpReply(503, "5.5.1", "Send HELO/EHLO first"));
        }

        if (_state != State.Greeted)
        {
            return Error(new SmtpReply(503, "5.5.1", "Nested MAIL command"));
        }

        if (options.RequireAuthentication && envelope.AuthenticatedUser is null)
        {
            return Error(new SmtpReply(530, "5.7.0", "Authentication required"));
        }

        var path = SmtpParser.ParsePath(cmd.Argument, "FROM", allowNull: true, allowUtf8: true);
        if (path is null)
        {
            return Error(SmtpReply.ArgumentError);
        }

        foreach (var (key, value) in path.Parameters)
        {
            switch (key.ToUpperInvariant())
            {
                case "SIZE":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                    {
                        return Error(SmtpReply.ArgumentError);
                    }

                    if (size > options.MaxMessageBytes)
                    {
                        return Error(SmtpReply.MessageTooBig);
                    }

                    envelope.DeclaredSize = size;
                    break;
                case "BODY":
                    if (value is null || !(value.Equals("7BIT", StringComparison.OrdinalIgnoreCase) || value.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase)
                                           || value.Equals("BINARYMIME", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Error(new SmtpReply(555, "5.5.4", "Unsupported BODY value"));
                    }

                    envelope.EightBitMime = !value.Equals("7BIT", StringComparison.OrdinalIgnoreCase);
                    break;
                case "SMTPUTF8":
                    envelope.SmtpUtf8 = true;
                    break;
                case "AUTH" or "RET" or "ENVID":
                    break; // accepted, not used
                default:
                    return Error(new SmtpReply(555, "5.5.4", "Unsupported parameter " + key));
            }
        }

        if (!envelope.SmtpUtf8 && !System.Text.Ascii.IsValid(path.Address))
        {
            return Error(new SmtpReply(553, "5.6.7", "Non-ASCII address requires SMTPUTF8"));
        }

        if (!envelope.Esmtp && path.Parameters.Count > 0)
        {
            return Error(new SmtpReply(555, "5.5.4", "Parameters require EHLO"));
        }

        var refusal = await handler.OnMailFromAsync(envelope, path, ct);
        if (refusal is not null)
        {
            return refusal.IsPositive ? Error(SmtpReply.TemporaryFailure) : RefusalStep(refusal);
        }

        envelope.From = path;
        _state = State.Mail;
        return SmtpStep.Reply(new SmtpReply(250, "2.1.0", "Sender OK"));
    }

    private async ValueTask<SmtpStep> RcptAsync(SmtpCommand cmd, CancellationToken ct)
    {
        if (_state != State.Mail)
        {
            return Error(new SmtpReply(503, "5.5.1", "Need MAIL before RCPT"));
        }

        var path = SmtpParser.ParsePath(cmd.Argument, "TO", allowNull: false, allowUtf8: envelope.SmtpUtf8);
        if (path is null)
        {
            return Error(envelope.SmtpUtf8 ? SmtpReply.ArgumentError : new SmtpReply(553, "5.6.7", "Invalid or non-ASCII address without SMTPUTF8"));
        }

        if (path.Parameters.Keys.Any(k => !k.Equals("NOTIFY", StringComparison.OrdinalIgnoreCase) && !k.Equals("ORCPT", StringComparison.OrdinalIgnoreCase)))
        {
            return Error(new SmtpReply(555, "5.5.4", "Unsupported RCPT parameter"));
        }

        if (envelope.Recipients.Count >= options.MaxRecipients)
        {
            return SmtpStep.Reply(SmtpReply.TooManyRecipients); // RFC 5321 4.5.3.1.10: 452, the client sends the rest later
        }

        if (envelope.Recipients.Any(r => string.Equals(r.Address, path.Address, StringComparison.OrdinalIgnoreCase)))
        {
            return SmtpStep.Reply(new SmtpReply(250, "2.1.5", "Recipient OK (duplicate)"));
        }

        var refusal = await handler.OnRecipientAsync(envelope, path, ct);
        if (refusal is not null)
        {
            return RefusalStep(refusal);
        }

        envelope.Recipients.Add(path);
        return SmtpStep.Reply(new SmtpReply(250, "2.1.5", "Recipient OK"));
    }

    private SmtpStep Bdat(SmtpCommand cmd)
    {
        var parts = cmd.Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || (parts.Length == 2 && !parts[1].Equals("LAST", StringComparison.OrdinalIgnoreCase)))
        {
            return Error(SmtpReply.ArgumentError);
        }

        var last = parts.Length == 2;
        if (_state is not (State.Mail or State.Bdat))
        {
            // The chunk must still be read off the wire to stay in sync (RFC 3030 2), then refused.
            _bdatFailed = true;
            _bdatError = SmtpReply.BadSequence;
        }
        else if (envelope.Recipients.Count == 0)
        {
            _bdatFailed = true;
            _bdatError = new SmtpReply(554, "5.5.1", "No valid recipients");
        }
        else if (_bdatBytes + size > options.MaxMessageBytes)
        {
            _bdatFailed = true;
            _bdatError = SmtpReply.MessageTooBig;
        }

        if (_state == State.Mail)
        {
            _state = State.Bdat;
        }

        _bdatBytes += size;
        return new SmtpStep(SmtpAction.ReadChunk, [], size, last);
    }

    /// <summary>
    /// After a BDAT chunk was read. For a non-final chunk returns the "250 chunk received" reply; for the final one
    /// the transport then calls <see cref="CompleteMessageAsync"/> unless the transaction already failed.
    /// </summary>
    public SmtpStep ChunkReceived(long size, bool last)
    {
        if (_bdatFailed)
        {
            var err = _bdatError ?? SmtpReply.TemporaryFailure;
            if (last)
            {
                envelope.Reset();
                _state = _state == State.New ? State.New : State.Greeted;
                ResetBdat();
            }

            return SmtpStep.Reply(err);
        }

        return last
            ? new SmtpStep(SmtpAction.Continue, []) // caller completes the message
            : SmtpStep.Reply(new SmtpReply(250, "2.0.0", size.ToString(CultureInfo.InvariantCulture) + " octets received"));
    }

    /// <summary>True when the final BDAT chunk arrived for a valid transaction (the message should be delivered).</summary>
    public bool BdatReadyToDeliver => _state == State.Bdat && !_bdatFailed;

    /// <summary>The message (DATA or all BDAT chunks) is complete; <paramref name="tooBig"/> when the transport stopped storing it.</summary>
    public async ValueTask<SmtpStep> CompleteMessageAsync(Stream message, bool tooBig, CancellationToken ct)
    {
        try
        {
            if (tooBig)
            {
                return SmtpStep.Reply(SmtpReply.MessageTooBig);
            }

            message.Position = 0;
            var reply = await handler.OnMessageAsync(envelope, message, ct);
            return SmtpStep.Reply(reply);
        }
        finally
        {
            envelope.Reset();
            _state = State.Greeted;
            ResetBdat();
        }
    }

    private void ResetBdat()
    {
        _bdatFailed = false;
        _bdatError = null;
        _bdatBytes = 0;
    }

    private static SmtpStep RefusalStep(SmtpReply refusal) =>
        refusal.Code == 421 ? new SmtpStep(SmtpAction.Close, [refusal]) : SmtpStep.Reply(refusal);

    // ---- AUTH (RFC 4954) ----

    private async ValueTask<SmtpStep> AuthAsync(string argument, CancellationToken ct)
    {
        if (options.Authenticate is null)
        {
            return Error(SmtpReply.NotImplemented);
        }

        if (_state == State.New)
        {
            return Error(new SmtpReply(503, "5.5.1", "Send EHLO first"));
        }

        if (envelope.AuthenticatedUser is not null || _state != State.Greeted)
        {
            return Error(SmtpReply.BadSequence);
        }

        if (!envelope.Tls)
        {
            return Error(new SmtpReply(538, "5.7.11", "Encryption required for requested authentication mechanism"));
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        if (!Sasl.Mechanisms.Contains(mechanism))
        {
            return Error(new SmtpReply(504, "5.5.4", "Unrecognized authentication type"));
        }

        _authMechanism = mechanism;
        _authLoginUser = null;
        _authBearerFailed = false;
        if (parts.Length == 2)
        {
            return await AuthResponseAsync(parts[1], ct);
        }

        return SmtpStep.Reply(new SmtpReply(334, null, mechanism == "LOGIN" ? "VXNlcm5hbWU6" : ""));
    }

    private async ValueTask<SmtpStep> AuthResponseAsync(string line, CancellationToken ct)
    {
        var mechanism = _authMechanism!;
        if (_authBearerFailed)
        {
            // RFC 7628 3.2.3: after the error challenge the client sends a dummy response; then the failure.
            _authMechanism = null;
            return Error(new SmtpReply(535, "5.7.8", "Authentication credentials invalid"));
        }

        if (line == "*")
        {
            _authMechanism = null;
            return Error(new SmtpReply(501, "5.0.0", "Authentication cancelled"));
        }

        var bytes = Sasl.FromBase64(line);
        if (bytes is null)
        {
            _authMechanism = null;
            return Error(new SmtpReply(501, "5.5.2", "Cannot decode response"));
        }

        SaslCredentials? creds;
        switch (mechanism)
        {
            case "LOGIN" when _authLoginUser is null:
                _authLoginUser = System.Text.Encoding.UTF8.GetString(bytes);
                return SmtpStep.Reply(new SmtpReply(334, null, "UGFzc3dvcmQ6"));
            case "LOGIN":
                creds = new SaslCredentials("LOGIN", _authLoginUser, System.Text.Encoding.UTF8.GetString(bytes), false);
                break;
            case "PLAIN":
                creds = Sasl.Plain(bytes);
                break;
            case "XOAUTH2":
                creds = Sasl.XOAuth2(bytes);
                break;
            default:
                creds = Sasl.OAuthBearer(bytes);
                break;
        }

        if (creds is null)
        {
            _authMechanism = null;
            return Error(new SmtpReply(501, "5.5.2", "Malformed authentication data"));
        }

        var result = await options.Authenticate!(creds, ct);
        if (result.Ok)
        {
            _authMechanism = null;
            envelope.AuthenticatedUser = result.Account;
            return SmtpStep.Reply(new SmtpReply(235, "2.7.0", "Authentication successful"));
        }

        if (result.TemporaryFailure)
        {
            _authMechanism = null;
            return Error(new SmtpReply(454, "4.7.0", "Temporary authentication failure"));
        }

        if (creds.IsBearerToken)
        {
            _authBearerFailed = true;
            return SmtpStep.Reply(new SmtpReply(334, null, Sasl.BearerError));
        }

        _authMechanism = null;
        return Error(new SmtpReply(535, "5.7.8", "Authentication credentials invalid"));
    }

    private SmtpStep Error(SmtpReply reply)
    {
        if (++_errors >= options.MaxErrors)
        {
            return new SmtpStep(SmtpAction.Close, [reply, SmtpReply.TooManyErrors]);
        }

        return SmtpStep.Reply(reply);
    }
}
