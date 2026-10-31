using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>One tile of a dashboard.</summary>
/// <param name="Shortcut">The tile as a plain shortcut: its look when it is not a card (or the card is not loaded yet), and the click target.</param>
public sealed record DashboardSlot(int Index, FolderTile Tile, PreviewTile Shortcut);

/// <summary>A <c>c4k.dir</c> dashboard: the app turns the slots whose tile is a card into live cards (<see cref="CardResolver"/>).</summary>
/// <param name="Section"><c>preview</c> or <c>enter</c>: where in the file the dashboard came from.</param>
public sealed record DashboardPreview(string Title, string? Description, FolderConfig Config, string Section, IReadOnlyList<DashboardSlot> Slots) : PreviewContent;
