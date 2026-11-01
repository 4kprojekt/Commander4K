using System.Text.Json;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Theming;

/// <summary>
/// Built-in skins plus user skins loaded from <c>config/skins/*.json</c>. A user skin with a built-in id replaces it,
/// so a shipped look can be tweaked by copying and editing its JSON.
/// </summary>
public sealed class SkinCatalog
{
    private readonly Dictionary<string, Skin> _skins;

    private SkinCatalog(IEnumerable<Skin> skins, IReadOnlyList<(string Path, string Error)> errors)
    {
        _skins = new Dictionary<string, Skin>(StringComparer.OrdinalIgnoreCase);
        foreach (var skin in skins)
        {
            _skins[skin.Id] = skin;
        }

        Errors = errors;
    }

    public static SkinCatalog BuiltInOnly { get; } = new(BuiltInSkins.All, []);

    /// <summary>Files that failed to load; the UI can surface them instead of silently ignoring a typo.</summary>
    public IReadOnlyList<(string Path, string Error)> Errors { get; }

    /// <summary>Built-ins first in their fixed order, then custom skins by name.</summary>
    public IReadOnlyList<Skin> Skins =>
    [
        .. BuiltInSkins.All.Select(b => _skins[b.Id]),
        .. _skins.Values.Where(s => !BuiltInSkins.All.Any(b => string.Equals(b.Id, s.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>Returns the skin, falling back to <see cref="BuiltInSkins.ModernDark"/> for unknown ids.</summary>
    public Skin Get(string? id) =>
        id is not null && _skins.TryGetValue(id, out var skin) ? skin : _skins[BuiltInSkins.ModernDarkId];

    /// <summary>The skin after <paramref name="currentId"/> in <see cref="Skins"/>, wrapping around (quick cycling).</summary>
    public Skin Next(string? currentId)
    {
        var skins = Skins;
        var index = -1;
        for (var i = 0; i < skins.Count; i++)
        {
            if (string.Equals(skins[i].Id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        return skins[(index + 1) % skins.Count];
    }

    public static Task<SkinCatalog> LoadAsync(string skinsDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                var skins = new List<Skin>(BuiltInSkins.All);
                var errors = new List<(string, string)>();
                if (!Directory.Exists(skinsDirectory))
                {
                    return new SkinCatalog(skins, errors);
                }

                foreach (var file in Directory.EnumerateFiles(skinsDirectory, "*.json"))
                {
                    if (string.Equals(Path.GetFileName(file), SkinSchema.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        await using var stream = File.OpenRead(file);
                        var skin = await JsonSerializer.DeserializeAsync(stream, ConfigJsonContext.Default.Skin, cancellationToken)
                            .ConfigureAwait(false);
                        if (skin is not null)
                        {
                            skins.Add(skin);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        errors.Add((file, ex.Message));
                    }
                }

                return new SkinCatalog(skins, errors);
            },
            cancellationToken);

    /// <summary>Writes a skin as JSON, e.g. to export a built-in as a starting point for a custom one.</summary>
    public static Task ExportAsync(Skin skin, string path, CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await using var stream = File.Create(path);
                await JsonSerializer.SerializeAsync(stream, skin, ConfigJsonContext.Default.Skin, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
}
