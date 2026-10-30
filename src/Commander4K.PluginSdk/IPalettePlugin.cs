namespace Commander4K.PluginSdk;

/// <summary>Where a palette is shown; the user can change it in the settings.</summary>
public enum PaletteDock : byte
{
    Hidden,
    Bottom,
    Left,
    Right,
    /// <summary>A narrow vertical bar between the two file panels (Total Commander's button bar position).</summary>
    BetweenPanels,
}

/// <param name="Icon">A short glyph or emoji shown before (or, in narrow docks, instead of) the title.</param>
public sealed record PaletteItem(string Id, string Title, string? Icon = null, string? Tooltip = null);

/// <param name="ForItem">Shown in an item's context menu (right click) instead of the palette header.</param>
public sealed record PaletteAction(string Id, string Title, string? Icon = null, bool ForItem = false);

/// <summary>
/// A bar of quick buttons (actions, favourite folders, saved views...) docked below, beside or between the panels.
/// Clicking an item calls <see cref="InvokeAsync"/>; header and context-menu actions call
/// <see cref="InvokeActionAsync"/>. Raise <see cref="ItemsChanged"/> to make the host reload the items.
/// </summary>
public interface IPalettePlugin : IPlugin
{
    string Title { get; }

    PaletteDock DefaultDock { get; }

    IReadOnlyList<PaletteAction> Actions => [];

    event Action? ItemsChanged;

    Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken);

    Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken);

    /// <param name="itemId">The item a <see cref="PaletteAction.ForItem"/> action was chosen for; <c>null</c> for header actions.</param>
    Task InvokeActionAsync(string actionId, string? itemId, IPluginHost host, CancellationToken cancellationToken) => Task.CompletedTask;
}
