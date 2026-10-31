using System.Buffers.Binary;
using System.Text;

namespace Commander4K.Plugins.BuiltIn.Network;

/// <summary>A network location the user added in Windows Explorer ("Add a network location").</summary>
public sealed record NetworkLocation(string Name, string Path);

/// <summary>
/// Reads Explorer's network locations (<c>%AppData%\Microsoft\Windows\Network Shortcuts</c>): each is a folder with a
/// <c>target.lnk</c> (or a plain <c>.lnk</c>). The target is read from the shortcut's LinkInfo (MS-SHLLINK): the
/// UNC name plus path suffix for shares, the local base path otherwise. Locations whose shortcut holds only a shell
/// ID list (FTP/WebDAV added as URLs) are skipped. No COM, so it is safe on any thread.
/// </summary>
public static class NetworkLocations
{
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2;
    private const uint VolumeIdAndLocalBasePath = 0x1, CommonNetworkRelativeLinkAndPathSuffix = 0x2;

    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Network Shortcuts");

    public static IReadOnlyList<NetworkLocation> Read(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var locations = new List<NetworkLocation>();
        foreach (var item in Directory.EnumerateFileSystemEntries(folder))
        {
            var shortcut = Directory.Exists(item) ? System.IO.Path.Combine(item, "target.lnk") : item;
            if (!shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(shortcut))
            {
                continue;
            }

            try
            {
                if (TryGetTarget(File.ReadAllBytes(shortcut)) is { } target)
                {
                    var name = Directory.Exists(item) ? System.IO.Path.GetFileName(item) : System.IO.Path.GetFileNameWithoutExtension(item);
                    locations.Add(new NetworkLocation(name, target));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [.. locations.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The target path stored in a shell link, or <c>null</c> when it has no usable LinkInfo.</summary>
    public static string? TryGetTarget(ReadOnlySpan<byte> link)
    {
        if (link.Length < 76 || BinaryPrimitives.ReadUInt32LittleEndian(link) != 0x4C)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(link[20..]);
        var offset = 76;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (link.Length < offset + 2)
            {
                return null;
            }

            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(link[offset..]);
        }

        if ((flags & HasLinkInfo) == 0 || link.Length < offset + 28)
        {
            return null;
        }

        var info = link[offset..];
        var infoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
        if (infoSize < 28 || infoSize > info.Length)
        {
            return null;
        }

        info = info[..infoSize];
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        var localBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        var network = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[20..]);
        var suffix = AnsiString(info, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));

        if ((infoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0 && network > 0 && network + 20 <= info.Length)
        {
            var relative = info[network..];
            var netName = AnsiString(relative, (int)BinaryPrimitives.ReadUInt32LittleEndian(relative[8..]));
            if (!string.IsNullOrEmpty(netName))
            {
                return string.IsNullOrEmpty(suffix) ? netName : netName.TrimEnd('\\') + "\\" + suffix;
            }
        }

        if ((infoFlags & VolumeIdAndLocalBasePath) != 0 && AnsiString(info, localBase) is { Length: > 0 } basePath)
        {
            return string.IsNullOrEmpty(suffix) ? basePath : basePath.TrimEnd('\\') + "\\" + suffix;
        }

        return null;
    }

    private static string? AnsiString(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length)
        {
            return null;
        }

        var end = data[offset..].IndexOf((byte)0);
        var bytes = end < 0 ? data[offset..] : data.Slice(offset, end);
        return Encoding.Latin1.GetString(bytes);
    }
}
