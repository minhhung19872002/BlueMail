using System.Text;

namespace BlueMail.Protocols.Smtp;

/// <summary>What a client proved in SASL: a user and either a password or a bearer token.</summary>
public sealed record SaslCredentials(string Mechanism, string User, string Secret, bool IsBearerToken);

/// <summary>Result of checking credentials: the account name to record, or a refusal (wrong, or temporarily locked).</summary>
public sealed record SaslResult(bool Ok, string? Account, bool TemporaryFailure = false)
{
    public static readonly SaslResult Wrong = new(false, null);
    public static readonly SaslResult TryLater = new(false, null, true);
}

/// <summary>
/// SASL message decoding for PLAIN (RFC 4616), LOGIN (draft-murchison-sasl-login), XOAUTH2 (Google) and OAUTHBEARER
/// (RFC 7628). Pure functions over the decoded client responses.
/// </summary>
public static class Sasl
{
    public static readonly string[] Mechanisms = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"];

    public static byte[]? FromBase64(string text)
    {
        if (text == "=")
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>PLAIN: authzid NUL authcid NUL passwd. A different authzid (acting as someone else) is refused.</summary>
    public static SaslCredentials? Plain(byte[] message)
    {
        var parts = Encoding.UTF8.GetString(message).Split('\0');
        if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return null;
        }

        if (parts[0].Length > 0 && !parts[0].Equals(parts[1], StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SaslCredentials("PLAIN", parts[1], parts[2], false);
    }

    /// <summary>XOAUTH2: "user=" u ^A "auth=Bearer " token ^A ^A.</summary>
    public static SaslCredentials? XOAuth2(byte[] message)
    {
        string? user = null, token = null;
        foreach (var kv in Encoding.UTF8.GetString(message).Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (kv.StartsWith("user=", StringComparison.Ordinal))
            {
                user = kv[5..];
            }
            else if (kv.StartsWith("auth=Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = kv[12..];
            }
        }

        return user is { Length: > 0 } && token is { Length: > 0 } ? new SaslCredentials("XOAUTH2", user, token, true) : null;
    }

    /// <summary>OAUTHBEARER (RFC 7628 3.1): gs2 header "n,a=user," then ^A-separated key=value pairs with auth=Bearer.</summary>
    public static SaslCredentials? OAuthBearer(byte[] message)
    {
        var text = Encoding.UTF8.GetString(message);
        var first = text.IndexOf('\u0001', StringComparison.Ordinal);
        if (first < 0)
        {
            return null;
        }

        var gs2 = text[..first].Split(',');
        var user = gs2.FirstOrDefault(p => p.StartsWith("a=", StringComparison.Ordinal))?[2..].Replace("=2C", ",", StringComparison.Ordinal)
            .Replace("=3D", "=", StringComparison.Ordinal);
        string? token = null;
        foreach (var kv in text[(first + 1)..].Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (kv.StartsWith("auth=Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = kv[12..];
            }
        }

        return token is { Length: > 0 } ? new SaslCredentials("OAUTHBEARER", user ?? "", token, true) : null;
    }

    /// <summary>The error a client gets for a refused token (RFC 7628 3.2.2), base64 in a 334 continuation.</summary>
    public static string BearerError => Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"status\":\"invalid_token\",\"schemes\":\"bearer\",\"scope\":\"bluemail-api\"}"));
}
