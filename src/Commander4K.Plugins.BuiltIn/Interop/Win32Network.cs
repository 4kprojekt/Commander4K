using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using static Commander4K.Plugins.BuiltIn.Interop.NativeMethods;

namespace Commander4K.Plugins.BuiltIn.Interop;

internal readonly record struct ShareInfo(string Name, string? Remark);

/// <summary>Managed wrappers over the blocking Win32 network calls; callers run them on background threads.</summary>
internal static unsafe class Win32Network
{
    /// <summary>Visible disk shares of <paramref name="server"/> (admin shares such as <c>C$</c> are skipped).</summary>
    /// <exception cref="Win32Exception">The server refused or could not be reached; see <c>NativeErrorCode</c>.</exception>
    public static List<ShareInfo> EnumerateShares(string server)
    {
        var shares = new List<ShareInfo>();
        var resume = 0;
        int status;
        do
        {
            status = NetShareEnum(server, 1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not (NerrSuccess or ErrorMoreData))
                {
                    throw new Win32Exception(status);
                }

                foreach (ref readonly var info in new ReadOnlySpan<ShareInfo1>((void*)buffer, read))
                {
                    if ((info.Type & StypeMask) != StypeDiskTree || (info.Type & StypeSpecial) != 0)
                    {
                        continue;
                    }

                    var remark = Marshal.PtrToStringUni(info.Remark);
                    shares.Add(new ShareInfo(Marshal.PtrToStringUni(info.NetName)!, string.IsNullOrEmpty(remark) ? null : remark));
                }
            }
            finally
            {
                if (buffer != 0)
                {
                    NetApiBufferFree(buffer);
                }
            }
        }
        while (status == ErrorMoreData);

        return shares;
    }

    /// <summary>
    /// Machines announced through the legacy Computer Browser service. Modern networks usually have none, in which
    /// case an empty list is returned rather than an error.
    /// </summary>
    public static List<string> EnumerateBrowserServers()
    {
        var resume = 0;
        var status = NetServerEnum(null, 100, out var buffer, MaxPreferredLength, out var read, out _, SvTypeAll, null, ref resume);
        try
        {
            if (status is not (NerrSuccess or ErrorMoreData))
            {
                return [];
            }

            var names = new List<string>(read);
            foreach (ref readonly var info in new ReadOnlySpan<ServerInfo100>((void*)buffer, read))
            {
                names.Add(Marshal.PtrToStringUni(info.Name)!);
            }

            return names;
        }
        finally
        {
            if (buffer != 0)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>IPv4 neighbours currently in the ARP cache: hosts we have talked to recently, so probed first.</summary>
    public static List<IPAddress> GetArpNeighbours()
    {
        uint size = 0;
        var status = GetIpNetTable(0, ref size, false);
        if (status != ErrorInsufficientBuffer || size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != NerrSuccess)
            {
                return [];
            }

            var count = *(uint*)buffer;
            // MIB_IPNETTABLE: DWORD dwNumEntries followed by the rows (4-byte aligned).
            var rows = new ReadOnlySpan<MibIpNetRow>((byte*)buffer + sizeof(uint), (int)count);
            var result = new List<IPAddress>(rows.Length);
            foreach (ref readonly var row in rows)
            {
                if (row.Type != MibIpNetTypeInvalid)
                {
                    result.Add(new IPAddress(row.Addr));
                }
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Authenticates to a share for this logon session (no drive letter), like <c>net use \\srv\share</c>.</summary>
    public static void Connect(string uncPath, string? userName, string? password)
    {
        var remote = Marshal.StringToHGlobalUni(uncPath);
        try
        {
            var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = remote };
            var status = WNetAddConnection2(in resource, password, userName, ConnectTemporary);
            if (status != NerrSuccess)
            {
                throw new Win32Exception(status);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(remote);
        }
    }
}
