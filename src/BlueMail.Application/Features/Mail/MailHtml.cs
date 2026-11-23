using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace BlueMail.Application.Features.Mail;

public sealed record SanitizedHtml(string Html, bool HasRemoteImages, bool RemoteImagesShown);

/// <summary>
/// Incoming HTML (PROMPT 6.3): HtmlSanitizer on the server removes scripts, event handlers, javascript:, forms,
/// meta refresh, CSS expression() and every CSS url(). Then:
/// - cid: images point to signed internal URLs;
/// - remote images are blocked (src moved to data-bm-blocked) unless the reader allowed them, in which case they go
///   through the image proxy;
/// - links open in a new tab with rel="noopener noreferrer", and a link whose text shows a different host than its
///   target gets a warning title.
/// The page is then wrapped in a document with a strict CSP for an iframe sandbox without allow-scripts and
/// without allow-same-origin.
/// </summary>
public static partial class MailHtml
{
    private static readonly string[] Forbidden =
        ["script", "style", "form", "input", "button", "select", "textarea", "option", "iframe", "frame", "frameset", "object", "embed", "applet",
         "meta", "link", "base", "svg", "math", "template", "audio", "video", "source", "track", "portal", "dialog"];

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();
        foreach (var tag in Forbidden)
        {
            s.AllowedTags.Remove(tag);
        }

        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "cid", "data", "tel" })
        {
            s.AllowedSchemes.Add(scheme);
        }

        s.AllowedAttributes.Add("bgcolor");
        s.AllowedAttributes.Add("background"); // dropped below: a URL fetch
        s.AllowedAtRules.Clear();
        s.AllowDataAttributes = false;
        return s;
    }

    public static SanitizedHtml Sanitize(string html, Func<string, string?> cidUrl, Func<string, string> proxyUrl, bool showRemote)
    {
        var sanitizer = Create();
        var hasRemote = false;
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el)
            {
                return;
            }

            el.RemoveAttribute("background");
            if (el.GetAttribute("style") is { } style && style.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                el.SetAttribute("style", CssUrl().Replace(style, ""));
            }

            switch (el.LocalName)
            {
                case "img":
                    var src = el.GetAttribute("src") ?? "";
                    el.RemoveAttribute("srcset");
                    if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = cidUrl(WebUtility.UrlDecode(src[4..]).Trim('<', '>'));
                        if (url is null)
                        {
                            el.RemoveAttribute("src");
                        }
                        else
                        {
                            el.SetAttribute("src", url);
                        }
                    }
                    else if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !src.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
                    {
                        // inline images are kept
                    }
                    else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRemote = true;
                        if (showRemote)
                        {
                            el.SetAttribute("src", proxyUrl(src));
                        }
                        else
                        {
                            el.RemoveAttribute("src");
                            el.SetAttribute("alt", el.GetAttribute("alt") ?? "");
                            el.ClassList.Add("bm-blocked-image");
                        }
                    }
                    else
                    {
                        el.RemoveAttribute("src");
                    }

                    break;
                case "a":
                    var href = el.GetAttribute("href");
                    if (href is null)
                    {
                        break;
                    }

                    if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                    {
                        el.RemoveAttribute("href");
                        break;
                    }

                    el.SetAttribute("target", "_blank");
                    el.SetAttribute("rel", "noopener noreferrer");
                    if (LooksDeceptive(el.TextContent, href))
                    {
                        el.SetAttribute("title", "⚠ " + href);
                        el.ClassList.Add("bm-link-mismatch");
                    }
                    else
                    {
                        el.SetAttribute("title", href);
                    }

                    break;
            }
        };

        var clean = sanitizer.Sanitize(html);
        return new SanitizedHtml(clean, hasRemote, showRemote && hasRemote);
    }

    /// <summary>The visible text names a host (or is a URL) different from the real target host.</summary>
    public static bool LooksDeceptive(string text, string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var m = HostInText().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        var shown = m.Groups["host"].Value.TrimEnd('.').ToLowerInvariant();
        var real = target.IdnHost.ToLowerInvariant();
        var shownAscii = shown;
        try
        {
            shownAscii = new System.Globalization.IdnMapping().GetAscii(shown);
        }
        catch (ArgumentException)
        {
        }

        return !(real == shownAscii || real.EndsWith("." + shownAscii, StringComparison.Ordinal));
    }

    /// <summary>A full HTML document for iframe srcdoc. CSP allows images only from BlueMail itself and data:.</summary>
    public static string Document(string body, string allowedImageOrigin, bool rtl) =>
        "<!doctype html><html" + (rtl ? " dir=\"rtl\"" : "") + "><head><meta charset=\"utf-8\">"
        + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src " + allowedImageOrigin
        + " data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'\">"
        + "<meta name=\"referrer\" content=\"no-referrer\">"
        + "<style>body{margin:0;padding:8px;font-family:system-ui,-apple-system,'Segoe UI',Roboto,'Noto Sans',sans-serif;overflow-wrap:anywhere}"
        + "img{max-width:100%;height:auto}.bm-blocked-image{display:inline-block;min-width:16px;min-height:16px;background:#e8eaed}"
        + ".bm-link-mismatch{outline:1px dashed #d93025}pre{white-space:pre-wrap}</style></head><body>"
        + body + "</body></html>";

    [GeneratedRegex(@"url\s*\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?<host>(?:[\p{L}\p{N}-]+\.)+[\p{L}]{2,63})(?:[/:?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostInText();
}
