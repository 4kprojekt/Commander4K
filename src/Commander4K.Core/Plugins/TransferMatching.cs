using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>Host-side check of whether a plugin panel's receiver accepts the items being sent (never trusts the plugin to filter).</summary>
public static class TransferMatching
{
    public static bool Fits(PanelReceiver receiver, IReadOnlyList<TransferItem> items)
    {
        if (items.Count == 0 || (items.Count > 1 && !receiver.Multiple))
            return false;

        foreach (var item in items)
        {
            if (item.Kind == TransferItemKind.Folder)
            {
                if (!receiver.Folders)
                    return false;
            }
            else if (receiver.Extensions is { Count: > 0 } patterns
                && !patterns.Any(pattern => ExternalPreviewPlugin.Matches(pattern, item.Name)))
            {
                return false;
            }
        }

        return true;
    }

    /// <returns>Why none of <paramref name="offered"/> fits (localized), or <c>null</c> when one does.</returns>
    public static string? WhyNone(IReadOnlyList<PanelReceiver> offered, IReadOnlyList<TransferItem> items)
    {
        if (offered.Any(receiver => Fits(receiver, items)))
            return null;
        if (offered.Count == 0)
            return Loc.T("Wtyczka nie podała żadnego odbiorcy dla tych plików.");
        if (items.Any(item => item.Kind == TransferItemKind.Folder) && !offered.Any(receiver => receiver.Folders))
            return Loc.T("Żaden odbiorca nie przyjmuje folderów.");
        if (items.Count > 1 && !offered.Any(receiver => receiver.Multiple))
            return Loc.T("Odbiorca „{0}” przyjmuje tylko jeden plik.", offered[0].Title);
        return Loc.T("Żaden odbiorca nie przyjmuje plików tego typu.");
    }
}
