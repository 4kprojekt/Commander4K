using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>One tile of a dashboard.</summary>
/// <param name="Shortcut">The tile as a plain shortcut: its look when it is not a card (or the card is not loaded yet), and the click target.</param>
public sealed record DashboardSlot(int Index, C4kItem Item, PreviewTile Shortcut);

/// <summary>A folder of <c>.c4k</c> files as a dashboard: the app turns the slots whose item is a card into live cards (<see cref="CardResolver"/>).</summary>
public sealed record DashboardPreview(string Title, string? Description, C4kFolder Folder, IReadOnlyList<DashboardSlot> Slots) : PreviewContent;
