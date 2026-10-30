namespace Commander4K.PluginSdk;

/// <param name="NamePattern">Wildcard pattern (<c>*</c>, <c>?</c>) matched against entry names.</param>
/// <param name="ContainsText">Optional content filter; providers decide how to match it (bytes, registry data, ...).</param>
/// <param name="MaxDegreeOfParallelism">0 means <see cref="Environment.ProcessorCount"/>.</param>
public sealed record SearchQuery(
    string RootPath,
    string NamePattern = "*",
    string? ContainsText = null,
    bool CaseSensitive = false,
    bool Recursive = true,
    int MaxDegreeOfParallelism = 0);

/// <param name="Offset">Byte offset (files) or position of the first content match, when applicable.</param>
public readonly record struct SearchHit(VfsEntry Entry, long? Offset = null, string? Snippet = null);

/// <summary>Search engine (Alt+F7) streaming hits as they are found.</summary>
public interface ISearchProviderPlugin : IPlugin
{
    bool CanSearch(string rootPath);

    IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
