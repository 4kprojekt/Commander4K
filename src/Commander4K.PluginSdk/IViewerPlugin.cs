namespace Commander4K.PluginSdk;

/// <summary>UI-agnostic result of a viewer; the host decides how to render each kind.</summary>
public abstract record ViewerContent;

public sealed record TextViewerContent(string Text, string? Language = null) : ViewerContent;

public sealed record ImageViewerContent(ReadOnlyMemory<byte> EncodedImage) : ViewerContent;

/// <param name="TotalLength">Full length of the source; <paramref name="Data"/> may be only its head.</param>
public sealed record HexViewerContent(ReadOnlyMemory<byte> Data, long TotalLength) : ViewerContent;

/// <summary>Lister (Total Commander WLX equivalent) used by F3 and quick view (Ctrl+Q).</summary>
public interface IViewerPlugin : IPlugin
{
    /// <summary>Higher wins when several viewers accept the same entry.</summary>
    int Priority { get; }

    bool CanView(in VfsEntry entry);

    ValueTask<ViewerContent> LoadAsync(VfsEntry entry, Stream content, CancellationToken cancellationToken);
}
