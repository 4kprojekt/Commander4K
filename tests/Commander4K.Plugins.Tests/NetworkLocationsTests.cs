using System.Buffers.Binary;
using System.Text;
using Commander4K.Plugins.BuiltIn.Network;

namespace Commander4K.Plugins.Tests;

public sealed class NetworkLocationsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "c4k-netloc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Share_target_is_net_name_plus_suffix()
    {
        Assert.Equal(@"\\nas\media\Filmy", NetworkLocations.TryGetTarget(Link(netName: @"\\nas\media", suffix: "Filmy", withIdList: true)));
        Assert.Equal(@"\\nas\media", NetworkLocations.TryGetTarget(Link(netName: @"\\nas\media", suffix: "")));
    }

    [Fact]
    public void Local_target_uses_the_base_path_and_id_list_only_links_are_skipped()
    {
        Assert.Equal(@"C:\Dane", NetworkLocations.TryGetTarget(Link(localBase: @"C:\Dane")));
        Assert.Null(NetworkLocations.TryGetTarget(Link(withLinkInfo: false, withIdList: true)));
        Assert.Null(NetworkLocations.TryGetTarget("not a shortcut at all, definitely longer than seventy-six bytes......"u8));
    }

    [Fact]
    public void Explorer_folders_with_target_lnk_are_listed_by_folder_name()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "NAS – filmy"));
        File.WriteAllBytes(Path.Combine(_folder, "NAS – filmy", "target.lnk"), Link(netName: @"\\nas\media", suffix: ""));
        File.WriteAllBytes(Path.Combine(_folder, "Archiwum.lnk"), Link(netName: @"\\srv\archiwum", suffix: "2026"));
        Directory.CreateDirectory(Path.Combine(_folder, "FTP"));
        File.WriteAllBytes(Path.Combine(_folder, "FTP", "target.lnk"), Link(withLinkInfo: false, withIdList: true));

        var locations = NetworkLocations.Read(_folder);

        Assert.Equal(
            [new NetworkLocation("Archiwum", @"\\srv\archiwum\2026"), new NetworkLocation("NAS – filmy", @"\\nas\media")],
            locations);
    }

    /// <summary>A minimal shell link (MS-SHLLINK): header, optional ID list, LinkInfo with a network or local target.</summary>
    private static byte[] Link(string? netName = null, string suffix = "", string? localBase = null, bool withIdList = false, bool withLinkInfo = true)
    {
        var data = new List<byte>();
        var header = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new Guid("00021401-0000-0000-C000-000000000046").TryWriteBytes(header.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (withIdList ? 1u : 0) | (withLinkInfo ? 2u : 0));
        data.AddRange(header);
        if (withIdList)
        {
            data.AddRange([6, 0, 4, 0, 0x1F, 0x50, 0, 0]); // IDListSize = 6, one 4-byte item, terminator.
        }

        if (!withLinkInfo)
        {
            return [.. data];
        }

        const int headerSize = 28;
        var strings = new List<byte>();
        int Add(string text)
        {
            var at = headerSize + strings.Count;
            strings.AddRange(Encoding.Latin1.GetBytes(text));
            strings.Add(0);
            return at;
        }

        var flags = 0u;
        int localOffset = 0, networkOffset = 0;
        if (localBase is not null)
        {
            flags |= 1;
            strings.AddRange([16, .. new byte[15]]); // VolumeID (size 16, contents not read).
            localOffset = Add(localBase);
        }

        if (netName is not null)
        {
            flags |= 2;
            networkOffset = headerSize + strings.Count;
            var relative = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(relative, 20 + (uint)netName.Length + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(relative.AsSpan(8), 20); // NetNameOffset, right after this block.
            strings.AddRange(relative);
            strings.AddRange(Encoding.Latin1.GetBytes(netName));
            strings.Add(0);
        }

        var suffixOffset = Add(suffix);
        var info = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)(headerSize + strings.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)localOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), (uint)networkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        data.AddRange(info);
        data.AddRange(strings);
        return [.. data];
    }
}
