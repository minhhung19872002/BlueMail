using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace BlueMail.Domain.Common;

/// <summary>
/// Address normalisation for storage and comparison (PROMPT 0.2): local part in Unicode NFC (RFC 6531
/// allows UTF-8) folded to lower case, domain converted to its A-label (xn--) form with IDNA (UTS #46
/// via <see cref="IdnMapping"/>) and lower-cased. Display converts the domain back to Unicode.
/// </summary>
public readonly record struct EmailAddress(string LocalPart, string Domain)
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    public string Value => LocalPart + "@" + Domain;

    public string DisplayDomain => Idn.GetUnicode(Domain);

    public override string ToString() => Value;

    public static bool TryParse(string? input, [NotNullWhen(true)] out EmailAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 320)
        {
            return false;
        }

        input = input.Trim().Normalize(NormalizationForm.FormC);
        var at = input.LastIndexOf('@');
        if (at <= 0 || at == input.Length - 1)
        {
            return false;
        }

        var local = input[..at];
        var domain = input[(at + 1)..].TrimEnd('.');
        if (local.Length > 64 || local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in local)
        {
            // Dot-atom text (RFC 5322 3.2.3) plus any non-ASCII (RFC 6531 UTF8-non-ascii). Quoted local parts are refused.
            var ok = c > 127 ? !char.IsControl(c) && !char.IsWhiteSpace(c) : char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c);
            if (!ok)
            {
                return false;
            }
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ascii.Length > 253 || !ascii.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        address = new EmailAddress(local.ToLowerInvariant(), ascii);
        return true;
    }

    public static EmailAddress Parse(string input) =>
        TryParse(input, out var a) ? a.Value : throw new FormatException("Invalid email address.");

    public static string NormalizeDomain(string domain) => Idn.GetAscii(domain.Trim().TrimEnd('.').Normalize(NormalizationForm.FormC)).ToLowerInvariant();
}
