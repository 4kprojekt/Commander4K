using System.Security.Cryptography;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Columns;

/// <summary>"SHA-256" column: the first 16 hex digits of each file's hash (enough to spot duplicates), read through the host.</summary>
public sealed class HashColumnPlugin : IColumnMetadataPlugin
{
    public const string Sha256Column = "sha256";

    public string Id => "builtin.columns.hash";

    public string DisplayName => "SHA-256";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.columns.hash", Loc.T("Kolumna SHA-256"),
        Loc.T("Skrót SHA-256 plików (pierwsze 16 znaków) — do szukania duplikatów i sprawdzania pobranych plików."), PluginPermissions.FileRead);

    public IReadOnlyList<ColumnDefinition> Columns { get; } = [new(Sha256Column, "SHA-256", ColumnAlignment.Left, 150)];

    public bool Supports(in VfsEntry entry) => entry.Kind == VfsEntryKind.File;

    public async ValueTask<ColumnValue> GetValueAsync(VfsEntry entry, string columnId, IPluginHost host, CancellationToken cancellationToken)
    {
        await using var stream = await host.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ColumnValue(Convert.ToHexStringLower(hash)[..16]);
    }
}
