using System.Security.Principal;

namespace Commander4K.Core.Security;

/// <summary>Whether this process runs elevated (UAC "Run as administrator"); a filtered admin token counts as not elevated.</summary>
public static class ProcessElevation
{
    private static readonly Lazy<bool> Lazy = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    });

    public static bool IsAdministrator => Lazy.Value;
}
