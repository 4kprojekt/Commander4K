namespace Commander4K.Core.Web;

/// <summary>Guards for addresses that come from files or plugins before the app opens them.</summary>
public static class WebUrl
{
    /// <summary>An absolute http(s) address — the only kind the app hands to a browser.</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
