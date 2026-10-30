using System.Runtime.InteropServices;

namespace Commander4K.Plugins.BuiltIn.Interop;

/// <summary>
/// Win32 networking APIs via source-generated <see cref="LibraryImportAttribute"/> marshalling. Only pointer-sized
/// (<c>nint</c>) and fixed-width fields are used, so the same layouts are valid on x64 and ARM64.
/// </summary>
internal static partial class NativeMethods
{
    public const int NerrSuccess = 0;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBadNetPath = 53;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorMoreData = 234;
    public const int ErrorLogonFailure = 1326;
    public const int ErrorNoBrowserServersFound = 6118;
    public const int MaxPreferredLength = -1;

    public const uint StypeDiskTree = 0;
    public const uint StypeMask = 0xFF;
    public const uint StypeSpecial = 0x80000000;

    public const uint SvTypeAll = 0xFFFFFFFF;

    public const uint MibIpNetTypeInvalid = 2;

    public const int ResourceTypeDisk = 1;
    public const uint ConnectTemporary = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ShareInfo1
    {
        public nint NetName;
        public uint Type;
        public nint Remark;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServerInfo100
    {
        public uint PlatformId;
        public nint Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MibIpNetRow
    {
        public uint Index;
        public uint PhysAddrLen;
        public fixed byte PhysAddr[8];
        public uint Addr;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public nint LocalName;
        public nint RemoteName;
        public nint Comment;
        public nint Provider;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int NetShareEnum(
        string? serverName,
        int level,
        out nint buffer,
        int preferredMaxLength,
        out int entriesRead,
        out int totalEntries,
        ref int resumeHandle);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int NetServerEnum(
        string? serverName,
        int level,
        out nint buffer,
        int preferredMaxLength,
        out int entriesRead,
        out int totalEntries,
        uint serverType,
        string? domain,
        ref int resumeHandle);

    [LibraryImport("netapi32.dll")]
    public static partial int NetApiBufferFree(nint buffer);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIpNetTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetAddConnection2(in NetResource resource, string? password, string? userName, uint flags);
}
