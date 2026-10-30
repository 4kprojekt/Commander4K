namespace Commander4K.PluginSdk;

/// <summary>
/// Thrown when a provider needs credentials for <see cref="Path"/>; the host shows a login dialog and retries.
/// </summary>
public sealed class VfsAuthenticationRequiredException(string path, string? realm = null, Exception? innerException = null)
    : IOException($"Authentication required for '{path}'.", innerException)
{
    public string Path { get; } = path;

    /// <summary>Server, share or host the credentials are for.</summary>
    public string? Realm { get; } = realm;
}

public sealed class VfsProviderNotFoundException(string path)
    : InvalidOperationException($"No file-system provider handles '{path}'.")
{
    public string Path { get; } = path;
}
