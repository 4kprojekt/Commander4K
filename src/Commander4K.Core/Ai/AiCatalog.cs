using System.Text.Json;
using System.Text.Json.Serialization;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Ai;

/// <summary>How a provider is signed in to.</summary>
public static class AiAuthKinds
{
    /// <summary>An API key pasted by the user (or read from <see cref="AiProviderInfo.EnvVar"/>).</summary>
    public const string Key = "key";

    /// <summary>OpenRouter's browser login (OAuth PKCE): the key is created for the app, nothing to copy.</summary>
    public const string OpenRouterOAuth = "oauth-openrouter";

    /// <summary>No credential (a local server such as Ollama); used once the user enables it.</summary>
    public const string None = "none";
}

/// <summary>One OpenAI-compatible chat provider as described by <c>ai-providers.json</c>.</summary>
/// <remarks>Collection and default properties tolerate <c>null</c>: the JSON source generator passes it for missing keys.</remarks>
public sealed record AiProviderInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Base URL; <c>/chat/completions</c> and <c>/models</c> are appended.</summary>
    public required string BaseUrl { get; init; }

    public string Auth { get; init => field = value ?? AiAuthKinds.Key; } = AiAuthKinds.Key;

    /// <summary>Where the user gets a key (opened by "Get key").</summary>
    public string? KeyUrl { get; init; }

    /// <summary>Environment variable that signs in without storing anything.</summary>
    public string? EnvVar { get; init; }

    /// <summary>The GitHub CLI's token (<c>gh auth token</c>) can be used.</summary>
    public bool GitHubCli { get; init; }

    /// <summary><c>free</c>, <c>credits</c> (free monthly/trial credit) or <c>local</c>.</summary>
    public string Tier { get; init => field = value ?? "free"; } = "free";

    /// <summary><c>none</c> (use <see cref="Models"/>), <c>all</c> (every model from <c>/models</c>) or <c>openrouter-free</c> (zero-priced models).</summary>
    public string DiscoverModels { get; init => field = value ?? "none"; } = "none";

    /// <summary>Preferred models, tried in order (after discovered ones are filtered to these when possible).</summary>
    public IReadOnlyList<string> Models { get; init => field = value ?? []; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    /// <summary>Description per language code (<c>pl</c>, <c>en</c>…).</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    /// <summary>How to install a local provider (Ollama, LM Studio); <c>null</c> for online services.</summary>
    public AiInstallInfo? Install { get; init; }

    [JsonIgnore]
    public string Note => Notes.TryGetValue(Loc.Language, out var note) || Notes.TryGetValue("en", out note) ? note : Notes.Values.FirstOrDefault() ?? string.Empty;
}

/// <summary>Installation help for a local provider. The list comes from the internet, so <see cref="SafeWingetId"/> and <see cref="IsWebUrl"/> guard what gets run or opened.</summary>
public sealed record AiInstallInfo
{
    /// <summary>winget package id (<c>winget install --id … -e</c>).</summary>
    public string? Winget { get; init; }

    public string? DownloadUrl { get; init; }

    /// <summary>Markdown steps per language code.</summary>
    public IReadOnlyDictionary<string, string> Steps { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    [JsonIgnore]
    public string? LocalizedSteps => Steps.TryGetValue(Loc.Language, out var steps) || Steps.TryGetValue("en", out steps) ? steps : Steps.Values.FirstOrDefault();

    /// <summary>The winget id when it is a plain package id (letters, digits, dots, dashes) — nothing that a shell would interpret.</summary>
    [JsonIgnore]
    public string? SafeWingetId => Winget is { Length: > 0 and <= 100 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? id : null;

    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

public sealed record AiCatalogFile
{
    public int Version { get; init; } = 1;

    public string? Updated { get; init; }

    public IReadOnlyList<AiProviderInfo> Providers { get; init => field = value ?? []; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AiCatalogFile))]
[JsonSerializable(typeof(AiCredentialsFile))]
internal sealed partial class AiJsonContext : JsonSerializerContext;

/// <summary>
/// The provider list: built-in copy → last copy downloaded from the repository (<c>data/ai-providers.json</c>) →
/// user additions/overrides from <c>config/ai/providers.json</c> (matched by id). <see cref="RefreshAsync"/> downloads
/// the live list; a broken download never replaces a working one.
/// </summary>
public sealed class AiCatalogService(HttpClient http, string cachePath, string userPath)
{
    private AiCatalogFile _base = LoadBuiltIn();

    /// <summary>Raised (on any thread) after the list changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<AiProviderInfo> Providers { get; private set; } = [];

    /// <summary>Where the current list came from (for the providers window).</summary>
    public string Source { get; private set; } = "built-in";

    public string? Updated => _base.Updated;

    public AiProviderInfo? Find(string id) => Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the cached and user files (call off the UI thread).</summary>
    public void Load()
    {
        if (TryRead(cachePath) is { Providers.Count: > 0 } cached)
        {
            _base = cached;
            Source = "cache";
        }

        Merge();
    }

    /// <summary>Downloads the live list; returns an error message, or <c>null</c> on success.</summary>
    public async Task<string?> RefreshAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var catalog = Parse(json);
            if (catalog.Providers.Count == 0)
            {
                return "The downloaded list has no providers.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(cachePath + ".tmp", json, cancellationToken).ConfigureAwait(false);
            File.Move(cachePath + ".tmp", cachePath, overwrite: true);
            _base = catalog;
            Source = url;
            Merge();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    public static AiCatalogFile Parse(string json) =>
        JsonSerializer.Deserialize(json, AiJsonContext.Default.AiCatalogFile) ?? new AiCatalogFile();

    public static AiCatalogFile LoadBuiltIn()
    {
        using var stream = typeof(AiCatalogService).Assembly.GetManifestResourceStream("ai-providers.json")
            ?? throw new InvalidOperationException("The built-in AI provider list is missing.");
        return JsonSerializer.Deserialize(stream, AiJsonContext.Default.AiCatalogFile) ?? new AiCatalogFile();
    }

    private void Merge()
    {
        var providers = _base.Providers.ToList();
        if (TryRead(userPath) is { } user)
        {
            foreach (var provider in user.Providers)
            {
                var index = providers.FindIndex(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    providers[index] = provider;
                }
                else
                {
                    providers.Add(provider);
                }
            }
        }

        Providers = providers;
        Changed?.Invoke();
    }

    private static AiCatalogFile? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
