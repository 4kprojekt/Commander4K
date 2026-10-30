namespace Commander4K.Core.Web;

/// <param name="UrlTemplate">Search URL with <c>{0}</c> for the escaped query; without <c>{0}</c> the page cannot take a query.</param>
/// <param name="IsAssistant">An AI chat (asking it is the "find a solution" path) rather than a classic search engine.</param>
public sealed record SearchEngine(string Id, string Name, string UrlTemplate, bool IsAssistant)
{
    public bool AcceptsQuery => UrlTemplate.Contains("{0}", StringComparison.Ordinal);

    public string BuildUrl(string query) =>
        AcceptsQuery ? string.Format(System.Globalization.CultureInfo.InvariantCulture, UrlTemplate, Uri.EscapeDataString(query.Trim())) : UrlTemplate;
}

/// <summary>Search engines and AI assistants the internal browser can send a question to.</summary>
public static class SearchEngines
{
    public const string DefaultSearch = "google";
    public const string DefaultAssistant = "chatgpt";

    public static IReadOnlyList<SearchEngine> All { get; } =
    [
        new("google", "Google", "https://www.google.com/search?q={0}", false),
        new("bing", "Bing", "https://www.bing.com/search?q={0}", false),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={0}", false),
        new("chatgpt", "ChatGPT", "https://chatgpt.com/?q={0}", true),
        new("claude", "Claude", "https://claude.ai/new?q={0}", true),
        new("perplexity", "Perplexity", "https://www.perplexity.ai/search?q={0}", true),

        // Gemini has no query parameter: the browser opens it and puts the question on the clipboard.
        new("gemini", "Gemini", "https://gemini.google.com/app", true),
    ];

    public static SearchEngine Get(string? id, string fallback = DefaultSearch) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(e => e.Id == fallback);

    /// <summary>
    /// What the address bar does with typed text: URLs (with or without scheme) are opened, anything else is searched.
    /// Returns <c>null</c> for blank input.
    /// </summary>
    public static string? ResolveAddress(string? text, SearchEngine engine)
    {
        var input = text?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "file" or "about" or "edge")
        {
            return absolute.ToString();
        }

        // "github.com/x", "localhost:8080", "192.168.1.1": a host-looking first word without spaces.
        // Local servers usually have no TLS, so localhost and bare IPv4 addresses get http.
        var local = input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(input.Split('/')[0].Split(':')[0], out _);
        if (!input.Contains(' ', StringComparison.Ordinal)
            && (local || input.Split('/')[0].Contains('.', StringComparison.Ordinal))
            && Uri.TryCreate((local ? "http://" : "https://") + input, UriKind.Absolute, out var withScheme)
            && withScheme.Host.Length > 0 && !withScheme.Host.EndsWith('.'))
        {
            return withScheme.ToString();
        }

        return engine.BuildUrl(input);
    }
}
