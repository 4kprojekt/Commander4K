using System.Text;
using System.Text.RegularExpressions;
using Commander4K.Core.Localization;
using MimeKit.Text;

namespace Commander4K.Core.Mail;

/// <summary>
/// Plain text out of hostile HTML: no script/style/head content, no image sources, only http(s)/mailto links.
/// Nothing in the result can be fetched by the viewer.
/// </summary>
public static partial class HtmlText
{
    public static string ToText(string html)
    {
        var sb = new StringBuilder();
        var skipDepth = 0;
        string? href = null;
        var linkStart = 0;
        var pre = 0;

        using var reader = new StringReader(html ?? "");
        var tokenizer = new HtmlTokenizer(reader);
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var id = tag.Id;
                if (id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head or HtmlTagId.Title)
                {
                    if (!tag.IsEndTag) { if (!tag.IsEmptyElement) skipDepth++; }
                    else if (skipDepth > 0) skipDepth--;
                    continue;
                }

                if (skipDepth > 0)
                {
                    continue;
                }

                if (id == HtmlTagId.Image)
                {
                    var alt = tag.Attributes.FirstOrDefault(a => a.Id == HtmlAttributeId.Alt)?.Value?.Trim();
                    sb.Append(string.IsNullOrEmpty(alt) ? Loc.T("[obraz]") : Loc.T("[obraz: {0}]", alt));
                }
                else if (id == HtmlTagId.A)
                {
                    if (tag.IsEndTag)
                    {
                        if (href is not null)
                        {
                            var text = sb.ToString(linkStart, sb.Length - linkStart).Trim();
                            if (text.Length == 0)
                            {
                                sb.Append(href);
                            }
                            else if (!string.Equals(text, href, StringComparison.OrdinalIgnoreCase))
                            {
                                sb.Append(" (").Append(href).Append(')');
                            }
                        }

                        href = null;
                    }
                    else
                    {
                        var value = tag.Attributes.FirstOrDefault(a => a.Id == HtmlAttributeId.Href)?.Value?.Trim();
                        href = IsSafeLink(value) ? value : null;
                        linkStart = sb.Length;
                    }
                }
                else if (id == HtmlTagId.Pre)
                {
                    pre = Math.Max(0, pre + (tag.IsEndTag ? -1 : 1));
                    Newline(sb);
                }
                else if (id is HtmlTagId.TD or HtmlTagId.TH)
                {
                    if (tag.IsEndTag) sb.Append(' ');
                }
                else if (IsBlock(id))
                {
                    Newline(sb);
                }

                continue;
            }

            if (token is HtmlDataToken data && skipDepth == 0 && data.Kind == HtmlTokenKind.Data)
            {
                var d = data.Data.Replace(' ', ' ');
                sb.Append(pre > 0 ? d : Whitespace().Replace(d, " "));
            }
        }

        var lines = sb.ToString().Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToArray();
        return ExtraBlank().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static bool IsSafeLink(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme is "http" or "https" or "mailto");

    private static void Newline(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }

    private static bool IsBlock(HtmlTagId id) => id is HtmlTagId.P or HtmlTagId.Div or HtmlTagId.Br or HtmlTagId.LI
        or HtmlTagId.TR or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6
        or HtmlTagId.BlockQuote or HtmlTagId.Table or HtmlTagId.UL or HtmlTagId.OL or HtmlTagId.HR or HtmlTagId.Body;

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlank();
}
