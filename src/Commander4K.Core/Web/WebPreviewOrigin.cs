using System.Security.Cryptography;
using System.Text;

namespace Commander4K.Core.Web;

/// <summary>
/// The origin a plugin's web preview is served from: <c>https://&lt;label&gt;.plugin.c4k</c>. All previews share one
/// WebView2 profile, so the origin is the only wall between two plugins' storage; the label is therefore one-to-one
/// with the exact plugin id.
/// </summary>
public static class WebPreviewOrigin
{
    public const string HostSuffix = ".plugin.c4k";

    private const int MaxLabelLength = 63;
    private const int HashChars = 16; // 64 bits: a colliding id cannot be found by brute force.

    /// <summary>The host name (label + <see cref="HostSuffix"/>) for <paramref name="pluginId"/>.</summary>
    public static string HostName(string pluginId) => Label(pluginId) + HostSuffix;

    /// <summary>
    /// An id that already is a plain label (<c>a-z</c>, <c>0-9</c>, single inner hyphens, at most 63 characters) is used as is.
    /// Any other id gets a readable part (lower case, other characters as <c>-</c>, shortened), then <c>--</c> and the first
    /// 16 hex digits of the SHA-256 of the exact id. Plain labels never contain <c>--</c>, so the two forms cannot meet.
    /// </summary>
    public static string Label(string pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        if (IsPlainLabel(pluginId))
        {
            return pluginId;
        }

        var readable = new StringBuilder(pluginId.Length);
        foreach (var c in pluginId.ToLowerInvariant())
        {
            var keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (keep)
            {
                readable.Append(c);
            }
            else if (readable.Length > 0 && readable[^1] != '-')
            {
                readable.Append('-');
            }
        }

        var maxReadable = MaxLabelLength - 2 - HashChars;
        var text = readable.ToString();
        text = (text.Length > maxReadable ? text[..maxReadable] : text).Trim('-');

        // Two characters before "--" would read as an IDNA prefix ("xn--…"); keep the readable part longer than that.
        if (text.Length < 3)
        {
            text = ("plugin-" + text).TrimEnd('-');
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pluginId)))[..HashChars];
        return text + "--" + hash;
    }

    private static bool IsPlainLabel(string id)
    {
        if (id.Length is 0 or > MaxLabelLength || id[0] == '-' || id[^1] == '-' || id.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
