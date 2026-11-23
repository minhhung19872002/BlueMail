using System.Globalization;
using System.Text;

namespace BlueMail.Protocols.Smtp;

/// <summary>An SMTP reply (RFC 5321 4.2) with optional enhanced status code (RFC 3463, advertised per RFC 2034).</summary>
public sealed record SmtpReply(int Code, string? Enhanced, params string[] Lines)
{
    public bool IsPositive => Code is >= 200 and < 400;
    public bool IsTransient => Code is >= 400 and < 500;

    public byte[] ToBytes(bool enhancedCodes)
    {
        var sb = new StringBuilder();
        var lines = Lines.Length == 0 ? [""] : Lines;
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(Code.ToString(CultureInfo.InvariantCulture)).Append(i < lines.Length - 1 ? '-' : ' ');
            if (enhancedCodes && Enhanced is not null && Code != 220 && Code != 221 && !(Code == 250 && i > 0))
            {
                sb.Append(Enhanced).Append(' ');
            }

            sb.Append(lines[i]).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public override string ToString() => $"{Code} {Enhanced} {string.Join(" / ", Lines)}";

    // Common replies (texts stay in English: other software reads them, PROMPT principle 4).
    public static readonly SmtpReply Ok = new(250, "2.0.0", "OK");
    public static readonly SmtpReply SyntaxError = new(500, "5.5.2", "Syntax error, command unrecognized");
    public static readonly SmtpReply ArgumentError = new(501, "5.5.4", "Syntax error in parameters or arguments");
    public static readonly SmtpReply NotImplemented = new(502, "5.5.1", "Command not implemented");
    public static readonly SmtpReply BadSequence = new(503, "5.5.1", "Bad sequence of commands");
    public static readonly SmtpReply LineTooLong = new(500, "5.5.6", "Line too long");
    public static readonly SmtpReply TooManyRecipients = new(452, "4.5.3", "Too many recipients");
    public static readonly SmtpReply MessageTooBig = new(552, "5.3.4", "Message size exceeds fixed limit");
    public static readonly SmtpReply ShuttingDown = new(421, "4.3.2", "Service shutting down, try again later");
    public static readonly SmtpReply TooManyErrors = new(421, "4.7.0", "Too many errors, closing connection");
    public static readonly SmtpReply TlsRequired = new(530, "5.7.0", "Must issue a STARTTLS command first");
    public static readonly SmtpReply TemporaryFailure = new(451, "4.3.0", "Temporary failure, please try again later");
}

/// <summary>A reverse-path or forward-path (RFC 5321 4.1.2) with its ESMTP parameters.</summary>
public sealed record SmtpPath(string Address, IReadOnlyDictionary<string, string?> Parameters)
{
    public bool IsNull => Address.Length == 0;

    public string Domain => Address.Contains('@', StringComparison.Ordinal) ? Address[(Address.LastIndexOf('@') + 1)..] : "";

    public string LocalPart => Address.Contains('@', StringComparison.Ordinal) ? Address[..Address.LastIndexOf('@')] : Address;
}

public enum SmtpVerb { Unknown, Helo, Ehlo, Mail, Rcpt, Data, Bdat, Rset, Noop, Quit, Vrfy, Expn, Help, StartTls, Auth }

public sealed record SmtpCommand(SmtpVerb Verb, string Argument);

/// <summary>Command line parser (RFC 5321 4.1). Pure: no I/O, no state.</summary>
public static class SmtpParser
{
    public static SmtpCommand Parse(string line)
    {
        var sp = line.IndexOf(' ');
        var verb = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
        var arg = sp < 0 ? "" : line[(sp + 1)..];
        return new SmtpCommand(verb switch
        {
            "HELO" => SmtpVerb.Helo,
            "EHLO" => SmtpVerb.Ehlo,
            "MAIL" => SmtpVerb.Mail,
            "RCPT" => SmtpVerb.Rcpt,
            "DATA" => SmtpVerb.Data,
            "BDAT" => SmtpVerb.Bdat,
            "RSET" => SmtpVerb.Rset,
            "NOOP" => SmtpVerb.Noop,
            "QUIT" => SmtpVerb.Quit,
            "VRFY" => SmtpVerb.Vrfy,
            "EXPN" => SmtpVerb.Expn,
            "HELP" => SmtpVerb.Help,
            "STARTTLS" => SmtpVerb.StartTls,
            "AUTH" => SmtpVerb.Auth,
            _ => SmtpVerb.Unknown,
        }, arg);
    }

    /// <summary>
    /// Parses "FROM:&lt;path&gt; PARAM=value ..." or "TO:&lt;path&gt; ...". Returns null on a syntax error.
    /// Paths without angle brackets are accepted (common in the wild); source routes are dropped (RFC 5321 4.1.2).
    /// </summary>
    public static SmtpPath? ParsePath(string argument, string keyword, bool allowNull, bool allowUtf8)
    {
        if (!argument.StartsWith(keyword + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = argument[(keyword.Length + 1)..].TrimStart();
        string address;
        string paramText;
        if (rest.StartsWith('<'))
        {
            var close = FindPathEnd(rest);
            if (close < 0)
            {
                return null;
            }

            address = rest[1..close];
            paramText = rest[(close + 1)..];
            if (paramText.Length > 0 && paramText[0] != ' ')
            {
                return null;
            }
        }
        else
        {
            var sp = rest.IndexOf(' ');
            address = sp < 0 ? rest : rest[..sp];
            paramText = sp < 0 ? "" : rest[sp..];
        }

        if (address.StartsWith('@'))
        {
            var colon = address.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            address = address[(colon + 1)..]; // obsolete source route
        }

        if (address.Length == 0)
        {
            if (!allowNull)
            {
                return null;
            }
        }
        else if (!ValidMailbox(address, allowUtf8))
        {
            return null;
        }

        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paramText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = p.IndexOf('=');
            var key = eq < 0 ? p : p[..eq];
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || !parameters.TryAdd(key, eq < 0 ? null : p[(eq + 1)..]))
            {
                return null;
            }
        }

        return new SmtpPath(address, parameters);
    }

    private static int FindPathEnd(string s)
    {
        var inQuote = false;
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\' && inQuote)
            {
                i++;
                continue;
            }

            if (s[i] == '"')
            {
                inQuote = !inQuote;
            }
            else if (s[i] == '>' && !inQuote)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Mailbox = Local-part "@" ( Domain / address-literal ) (RFC 5321 4.1.2; UTF-8 per RFC 6531 when allowed).</summary>
    public static bool ValidMailbox(string address, bool allowUtf8)
    {
        if (address.Length > 320 || (!allowUtf8 && !Ascii.IsValid(address)))
        {
            return false;
        }

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return false;
        }

        var local = address[..at];
        var domain = address[(at + 1)..];
        if (local.Length > 64 && !(local.StartsWith('"') && local.Length <= 130))
        {
            return false;
        }

        if (local.StartsWith('"'))
        {
            if (!local.EndsWith('"') || local.Length < 2)
            {
                return false;
            }
        }
        else if (local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal)
                 || local.Any(c => c < 128 && !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c))))
        {
            return false;
        }

        if (domain.StartsWith('[') && domain.EndsWith(']'))
        {
            var lit = domain[1..^1];
            return System.Net.IPAddress.TryParse(lit.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase) ? lit[5..] : lit, out _);
        }

        return domain.Length <= 253 && domain.Split('.').All(l => l.Length is > 0 and <= 63 && !l.StartsWith('-') && !l.EndsWith('-')
                   && l.All(c => c > 127 || char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
