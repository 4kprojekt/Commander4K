using System.Text;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Viewers;

/// <summary>Fallback F3 viewer: text when the head of the file looks textual, otherwise a hex dump.</summary>
public sealed class TextHexViewerPlugin : IViewerPlugin
{
    /// <summary>Only the head of large files is loaded; the viewer says so.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private const int SniffLength = 8 * 1024;

    public string Id => "builtin.viewer.texthex";

    public string DisplayName => "Text / Hex";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.viewer.texthex", "Podgląd tekst/hex", "Podgląd plików jako tekst lub zrzut szesnastkowy.", PluginPermissions.FileRead);

    public int Priority => 0;

    public bool CanView(in VfsEntry entry) => entry.Kind is VfsEntryKind.File or VfsEntryKind.RegistryValue;

    public async ValueTask<ViewerContent> LoadAsync(VfsEntry entry, Stream content, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBytes];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await content.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        var data = buffer.AsMemory(0, length);
        var total = entry.Size > 0 ? entry.Size : length;
        if (LooksBinary(data.Span[..Math.Min(length, SniffLength)]))
        {
            return new HexViewerContent(data, total);
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new TextViewerContent(text, Path.GetExtension(entry.Name).TrimStart('.'));
    }

    /// <summary>UTF-16 files carry a BOM; anything else with NUL bytes is treated as binary.</summary>
    internal static bool LooksBinary(ReadOnlySpan<byte> head) =>
        !(head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        && head.IndexOf((byte)0) >= 0;

    public static string FormatHex(ReadOnlySpan<byte> data, int maxLines = 65_536)
    {
        var builder = new StringBuilder(Math.Min(data.Length, maxLines * 16) * 5);
        for (var offset = 0; offset < data.Length && offset / 16 < maxLines; offset += 16)
        {
            var line = data.Slice(offset, Math.Min(16, data.Length - offset));
            builder.Append(offset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                builder.Append(i < line.Length ? line[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : "  ").Append(i == 7 ? "  " : " ");
            }

            builder.Append(' ');
            foreach (var b in line)
            {
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
