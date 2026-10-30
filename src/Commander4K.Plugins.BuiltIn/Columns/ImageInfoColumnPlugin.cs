using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Columns;

/// <param name="Taken">EXIF DateTimeOriginal (or DateTime), local camera time.</param>
public sealed record ImageInfo(int Width, int Height, DateTime? Taken, string? Camera);

/// <summary>
/// Photo columns: dimensions (JPEG, PNG, GIF, BMP), EXIF date taken and camera. Only the file header is read (at most
/// 256 KB, through the host), parsed once per file for all three columns.
/// </summary>
public sealed class ImageInfoColumnPlugin : IColumnMetadataPlugin
{
    public const string SizeColumn = "image.size";
    public const string TakenColumn = "exif.date";
    public const string CameraColumn = "exif.camera";
    private const int HeaderBytes = 256 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp"];

    private readonly ConcurrentDictionary<(string Path, DateTimeOffset Modified), Lazy<Task<ImageInfo?>>> _cache = new();

    public string Id => "builtin.columns.image";

    public string DisplayName => "Image info";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.columns.image", "Kolumny zdjęć",
        "Wymiary obrazów oraz data wykonania i aparat z EXIF (JPEG).", PluginPermissions.FileRead);

    public IReadOnlyList<ColumnDefinition> Columns { get; } =
    [
        new(SizeColumn, "Wymiary", ColumnAlignment.Right, 92),
        new(TakenColumn, "Zrobione", ColumnAlignment.Left, 120),
        new(CameraColumn, "Aparat", ColumnAlignment.Left, 130),
    ];

    public bool Supports(in VfsEntry entry) =>
        entry.Kind == VfsEntryKind.File && ImageExtensions.Contains(System.IO.Path.GetExtension(entry.Name).ToLowerInvariant());

    public async ValueTask<ColumnValue> GetValueAsync(VfsEntry entry, string columnId, IPluginHost host, CancellationToken cancellationToken)
    {
        if (_cache.Count > 4096)
        {
            _cache.Clear();
        }

        var lazy = _cache.GetOrAdd((entry.Path, entry.LastWriteTime), _ => new Lazy<Task<ImageInfo?>>(() => ReadAsync(entry.Path, host, cancellationToken)));
        ImageInfo? info;
        try
        {
            info = await lazy.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cache.TryRemove((entry.Path, entry.LastWriteTime), out _); // Do not cache a cancelled read.
            throw;
        }

        return (columnId, info) switch
        {
            (_, null) => ColumnValue.Empty,
            (SizeColumn, { Width: > 0 } i) => new ColumnValue($"{i.Width}×{i.Height}", (long)i.Width * i.Height),
            (TakenColumn, { Taken: { } taken }) => new ColumnValue(taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), taken.Ticks),
            (CameraColumn, { Camera: { } camera }) => new ColumnValue(camera),
            _ => ColumnValue.Empty,
        };
    }

    private static async Task<ImageInfo?> ReadAsync(string path, IPluginHost host, CancellationToken cancellationToken)
    {
        await using var stream = await host.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[HeaderBytes];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        return Parse(buffer.AsSpan(0, length));
    }

    /// <summary>Reads dimensions and EXIF from the start of an image file; <c>null</c> for unknown formats.</summary>
    public static ImageInfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ParseJpeg(data);
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // Not a u8 literal: \x89 would encode as two bytes.
        if (data.Length >= 24 && data[..8].SequenceEqual(pngSignature) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return new ImageInfo(BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]), null, null);
        }

        if (data.Length >= 10 && data[..4].SequenceEqual("GIF8"u8))
        {
            return new ImageInfo(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), null, null);
        }

        if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return new ImageInfo(BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])), null, null);
        }

        return null;
    }

    private static ImageInfo ParseJpeg(ReadOnlySpan<byte> data)
    {
        int width = 0, height = 0;
        DateTime? taken = null;
        string? camera = null;
        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                break; // Not at a marker any more: give up with what we have.
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++; // Fill byte.
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2; // Markers without a length.
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break; // End of image / start of scan: no more headers.
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            var segmentStart = position + 4;
            var segmentEnd = Math.Min(data.Length, position + 2 + length);
            if (length < 2 || segmentStart > segmentEnd)
            {
                break;
            }

            var segment = data[segmentStart..segmentEnd];
            if (marker == 0xE1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                (taken, camera, var exifWidth, var exifHeight) = ParseExif(segment[6..]);
                if (width == 0)
                {
                    (width, height) = (exifWidth, exifHeight);
                }
            }
            else if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && segment.Length >= 5)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]); // Frame header wins over EXIF hints.
            }

            position += 2 + length;
        }

        return new ImageInfo(width, height, taken, camera);
    }

    private static (DateTime? Taken, string? Camera, int Width, int Height) ParseExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return default;
        }

        var little = tiff[0] == (byte)'I';
        string? make = null, model = null, original = null, modified = null;
        int width = 0, height = 0;
        var exifOffset = 0;
        foreach (var (tag, type, count, valueOffset, entryPosition) in Entries(tiff, ReadUInt32(tiff, 4, little), little))
        {
            switch (tag)
            {
                case 0x010F: make = ReadAscii(tiff, type, count, valueOffset, entryPosition, little); break;
                case 0x0110: model = ReadAscii(tiff, type, count, valueOffset, entryPosition, little); break;
                case 0x0132: modified = ReadAscii(tiff, type, count, valueOffset, entryPosition, little); break;
                case 0x8769: exifOffset = (int)valueOffset; break;
            }
        }

        if (exifOffset > 0)
        {
            foreach (var (tag, type, _, valueOffset, entryPosition) in Entries(tiff, (uint)exifOffset, little))
            {
                switch (tag)
                {
                    case 0x9003: original = ReadAscii(tiff, type, 20, valueOffset, entryPosition, little); break;
                    case 0xA002: width = (int)(type == 3 ? ReadUInt16(tiff, entryPosition + 8, little) : valueOffset); break;
                    case 0xA003: height = (int)(type == 3 ? ReadUInt16(tiff, entryPosition + 8, little) : valueOffset); break;
                }
            }
        }

        DateTime? taken = DateTime.TryParseExact(original ?? modified, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
        var camera = (make, model) switch
        {
            (null, null) => null,
            ({ } m, { } mo) when mo.StartsWith(m, StringComparison.OrdinalIgnoreCase) => mo,
            _ => string.Join(' ', new[] { make, model }.Where(s => !string.IsNullOrEmpty(s))),
        };
        return (taken, string.IsNullOrWhiteSpace(camera) ? null : camera, width, height);
    }

    /// <summary>The 12-byte entries of one IFD (bounds-checked; a malformed offset yields nothing).</summary>
    private static List<(ushort Tag, ushort Type, uint Count, uint ValueOffset, int Position)> Entries(ReadOnlySpan<byte> tiff, uint offset, bool little)
    {
        var entries = new List<(ushort, ushort, uint, uint, int)>();
        if (offset + 2 > tiff.Length)
        {
            return entries;
        }

        var count = ReadUInt16(tiff, (int)offset, little);
        for (var i = 0; i < count; i++)
        {
            var position = (int)offset + 2 + (i * 12);
            if (position + 12 > tiff.Length)
            {
                break;
            }

            entries.Add((ReadUInt16(tiff, position, little), ReadUInt16(tiff, position + 2, little), ReadUInt32(tiff, position + 4, little),
                ReadUInt32(tiff, position + 8, little), position));
        }

        return entries;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> tiff, ushort type, uint count, uint valueOffset, int entryPosition, bool little)
    {
        if (type != 2 || count == 0)
        {
            return null;
        }

        var start = count <= 4 ? entryPosition + 8 : (int)valueOffset;
        if (start < 0 || start + count > tiff.Length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(tiff.Slice(start, (int)count)).TrimEnd('\0', ' ');
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int position, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[position..]) : BinaryPrimitives.ReadUInt16BigEndian(data[position..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int position, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) : BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
}
