using System.Text.Json;

namespace Commander4K.Core.Ai;

/// <summary>What the user configured for one provider (<c>config/ai/credentials.json</c>).</summary>
public sealed record AiCredential
{
    /// <summary>The API key, encrypted for the current Windows user (<c>dpapi:</c> + Base64), or plain text if the user wrote it by hand.</summary>
    public string? Key { get; init; }

    /// <summary><c>false</c> keeps the key but skips the provider when rotating.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Rotation order (lower first); providers without one follow in catalogue order.</summary>
    public int? Priority { get; init; }

    /// <summary>Model tried first instead of the catalogue's preference.</summary>
    public string? Model { get; init; }

    /// <summary>How the key was obtained (<c>oauth</c>, <c>pasted</c>, <c>gh</c>) — shown in the providers window.</summary>
    public string? Source { get; init; }

    public DateTimeOffset? Added { get; init; }
}

public sealed record AiCredentialsFile
{
    public Dictionary<string, AiCredential> Providers { get; init => field = value ?? new(StringComparer.OrdinalIgnoreCase); } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Signed-in providers, kept in the configuration folder (<c>config/ai/credentials.json</c>) next to the other
/// settings. Keys are encrypted with Windows DPAPI for the current user, so the file is useless on another account or
/// machine (sign in again there); a key typed into the file by hand in plain text is accepted as well.
/// </summary>
public sealed class AiCredentialStore(string path)
{
    private readonly Lock _gate = new();
    private AiCredentialsFile? _file;

    /// <summary>Raised (on any thread) after a credential was saved or removed.</summary>
    public event Action? Changed;

    public string FilePath => path;

    public AiCredential? Get(string providerId)
    {
        lock (_gate)
        {
            return Load().Providers.GetValueOrDefault(providerId);
        }
    }

    public IReadOnlyDictionary<string, AiCredential> All()
    {
        lock (_gate)
        {
            return new Dictionary<string, AiCredential>(Load().Providers, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The decrypted key, or <c>null</c> (also when it was encrypted by another user/machine).</summary>
    public string? GetKey(string providerId) => Get(providerId)?.Key is { Length: > 0 } stored ? Unprotect(stored) : null;

    public void SaveKey(string providerId, string key, string source) =>
        Update(providerId, current => (current ?? new AiCredential()) with
        {
            Key = Protect(key.Trim()),
            Enabled = true,
            Source = source,
            Added = DateTimeOffset.Now,
        });

    public void Update(string providerId, Func<AiCredential?, AiCredential?> change)
    {
        lock (_gate)
        {
            var providers = Load().Providers;
            var updated = change(providers.GetValueOrDefault(providerId));
            if (updated is null)
            {
                providers.Remove(providerId);
            }
            else
            {
                providers[providerId] = updated;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_file!, AiJsonContext.Default.AiCredentialsFile));
            File.Move(path + ".tmp", path, overwrite: true);
        }

        Changed?.Invoke();
    }

    public void Remove(string providerId) => Update(providerId, _ => null);

    internal static string Protect(string key) => Security.SecretProtector.Protect(key);

    internal static string? Unprotect(string stored) => Security.SecretProtector.Unprotect(stored);

    private AiCredentialsFile Load()
    {
        if (_file is not null)
        {
            return _file;
        }

        try
        {
            _file = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AiJsonContext.Default.AiCredentialsFile) : null;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", overwrite: true); // Keep a hand-edited file the user broke instead of losing it.
        }

        _file ??= new AiCredentialsFile();
        _file = _file with { Providers = new Dictionary<string, AiCredential>(_file.Providers, StringComparer.OrdinalIgnoreCase) };
        return _file;
    }
}
