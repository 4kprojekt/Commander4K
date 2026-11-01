using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.Connections;

public sealed record SftpConnectionPreview(string Path) : PreviewContent;

public sealed class SftpConnectionPreviewPlugin : IPreviewPlugin
{
    public const string PluginId = "builtin.sftp-setup";

    public static SftpConnectionPreviewPlugin Instance { get; } = new();

    public string Id => PluginId;

    public string DisplayName => "SFTP";

    public PluginManifest Manifest { get; } = new(
        PluginId,
        Loc.T("Konfiguracja SFTP"),
        "1.0.0",
        "4Kprojekt",
        Loc.T("Konfiguruje połączenie z wykrytym serwerem SFTP."),
        [PluginPermissions.Network]);

    public int Priority => int.MaxValue;

    public bool CanPreview(VfsEntry entry) =>
        entry.Kind == VfsEntryKind.Host
        && Uri.TryCreate(entry.Path, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase);

    public Task<PreviewContent> LoadAsync(
        VfsEntry entry,
        IPluginHost host,
        IProgress<PreviewContent>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult<PreviewContent>(new SftpConnectionPreview(entry.Path));
}
