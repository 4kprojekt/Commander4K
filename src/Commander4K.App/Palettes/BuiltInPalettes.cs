using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.Core.Configuration;
using Commander4K.Core.Input;
using Commander4K.PluginSdk;

namespace Commander4K.App.Palettes;

/// <summary>Window operations the built-in palettes need beyond <see cref="IPluginHost"/>.</summary>
public interface IShell
{
    AppHost App { get; }

    Task ExecuteCommandAsync(string commandId);

    ViewSetting CaptureView(string name);

    Task RestoreViewAsync(ViewSetting view);

    Task<PickerItem?> PickAsync(string title, IReadOnlyList<PickerItem> items);
}

/// <summary>Palettes that should reload their items whenever the user navigates.</summary>
public interface INavigationAwarePalette;

internal static class PaletteManifest
{
    public static PluginManifest Create(string id, string name, string description) =>
        new(id, name, "1.0.0", "4Kprojekt", description, []);

    /// <summary>Last path segment as a button label; roots (<c>C:\</c>, <c>net://</c>) keep the whole path.</summary>
    public static string ShortName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length == 0 || name.EndsWith(':') ? path : name;
    }
}

/// <summary>Buttons for chosen commands (Total Commander's button bar); defaults to the file operations and panels.</summary>
public sealed class QuickActionsPalette(IShell shell) : IPalettePlugin
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        CommandIds.Copy, CommandIds.Move, CommandIds.MakeDirectory, CommandIds.Delete, CommandIds.SearchAsync,
        CommandIds.QuickView, CommandIds.OpenTerminal, CommandIds.PanelPrefix + "builtin.taskmanager",
        CommandIds.PanelPrefix + "builtin.sysinfo", CommandIds.Settings,
    ];

    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        [CommandIds.Copy] = "⧉", [CommandIds.Move] = "➜", [CommandIds.MakeDirectory] = "📁", [CommandIds.Delete] = "🗑",
        [CommandIds.SearchAsync] = "🔍", [CommandIds.QuickView] = "👁", [CommandIds.OpenTerminal] = "⌨", [CommandIds.Settings] = "⚙",
        [CommandIds.PanelModes] = "▦", [CommandIds.Refresh] = "⟳", [CommandIds.Compare] = "⇄", [CommandIds.GoToFrecent] = "★",
        [CommandIds.PanelPrefix + "builtin.taskmanager"] = "📊", [CommandIds.PanelPrefix + "builtin.sysinfo"] = "ℹ",
        [CommandIds.PanelPrefix + "builtin.logs"] = "📜", [CommandIds.PanelPrefix + "builtin.powershell"] = "❯",
    };

    public string Id => "builtin.palette.quick";

    public string DisplayName => Title;

    public PluginManifest Manifest { get; } = PaletteManifest.Create("builtin.palette.quick", "Szybkie akcje", "Przyciski wybranych komend.");

    public string Title => "Szybkie akcje";

    public PaletteDock DefaultDock => PaletteDock.BetweenPanels;

    public IReadOnlyList<PaletteAction> Actions { get; } =
    [
        new("add", "Dodaj akcję", "+"),
        new("up", "Przesuń wyżej", "↑", ForItem: true),
        new("remove", "Usuń z palety", "✕", ForItem: true),
    ];

    public event Action? ItemsChanged;

    private List<string> Ids => shell.App.Settings.QuickActions ?? [.. Defaults];

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var commands = shell.App.ListCommands().ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PaletteItem> items = [.. Ids.Where(commands.ContainsKey).Select(id =>
        {
            var description = commands[id].Description;
            var title = description.Contains(": ", StringComparison.Ordinal) ? description[(description.LastIndexOf(": ", StringComparison.Ordinal) + 2)..] : description;
            var keys = shell.App.KeyBindings.FirstOrDefault(b => string.Equals(b.Value, id, StringComparison.OrdinalIgnoreCase)).Key;
            return new PaletteItem(id, title, Icons.GetValueOrDefault(id, "▸"), keys is null ? description : $"{description} ({keys})");
        })];
        return Task.FromResult(items);
    }

    public Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken) => shell.ExecuteCommandAsync(itemId);

    public async Task InvokeActionAsync(string actionId, string? itemId, IPluginHost host, CancellationToken cancellationToken)
    {
        var ids = Ids;
        switch (actionId)
        {
            case "add":
                var choices = shell.App.ListCommands().Where(c => !ids.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(c => c.Description, StringComparer.CurrentCulture)
                    .Select(c => new PickerItem(c.Description, c.Id, c.Id)).ToList();
                if (await shell.PickAsync("Dodaj akcję do palety", choices) is not { } choice)
                {
                    return;
                }

                ids.Add(choice.Value);
                break;
            case "remove" when itemId is not null:
                ids.RemoveAll(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase));
                break;
            case "up" when itemId is not null:
                var index = ids.FindIndex(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase));
                if (index > 0)
                {
                    (ids[index - 1], ids[index]) = (ids[index], ids[index - 1]);
                }

                break;
            default:
                return;
        }

        shell.App.Settings.QuickActions = ids;
        await shell.App.SaveSettingsAsync();
        ItemsChanged?.Invoke();
    }
}

/// <summary>Favourite folders (any VFS path: disks, shares, SFTP, registry); click to open in the active panel.</summary>
public sealed class FavoritesPalette(IShell shell) : IPalettePlugin
{
    public string Id => "builtin.palette.favorites";

    public string DisplayName => Title;

    public PluginManifest Manifest { get; } = PaletteManifest.Create("builtin.palette.favorites", "Ulubione", "Ulubione foldery i lokalizacje.");

    public string Title => "Ulubione";

    public PaletteDock DefaultDock => PaletteDock.Left;

    public IReadOnlyList<PaletteAction> Actions { get; } =
    [
        new("add", "Dodaj bieżący folder", "+"),
        new("rename", "Zmień nazwę", "✎", ForItem: true),
        new("remove", "Usuń z ulubionych", "✕", ForItem: true),
    ];

    public event Action? ItemsChanged;

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        IReadOnlyList<PaletteItem> items = [.. shell.App.Settings.Favorites.Select((f, i) => new PaletteItem(i.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Name, "★", f.Path))];
        return Task.FromResult(items);
    }

    public Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken) =>
        Find(itemId) is { } favorite ? host.NavigateAsync(favorite.Path) : Task.CompletedTask;

    public async Task InvokeActionAsync(string actionId, string? itemId, IPluginHost host, CancellationToken cancellationToken)
    {
        var favorites = shell.App.Settings.Favorites;
        switch (actionId)
        {
            case "add" when host.ActivePath is { } path:
                if (favorites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    host.Notify("Ten folder już jest w ulubionych.");
                    return;
                }

                favorites.Add(new FavoriteSetting { Name = PaletteManifest.ShortName(path), Path = path });
                break;
            case "rename" when Find(itemId) is { } favorite:
                if (await host.PromptAsync("Zmień nazwę", favorite.Path, favorite.Name) is not { Length: > 0 } newName)
                {
                    return;
                }

                favorite.Name = newName;
                break;
            case "remove" when Find(itemId) is { } favorite:
                favorites.Remove(favorite);
                break;
            default:
                return;
        }

        await shell.App.SaveSettingsAsync();
        ItemsChanged?.Invoke();
    }

    private FavoriteSetting? Find(string? itemId) =>
        int.TryParse(itemId, out var index) && index >= 0 && index < shell.App.Settings.Favorites.Count ? shell.App.Settings.Favorites[index] : null;
}

/// <summary>Saved layouts: both panels' folders and modes (files, preview, plugin panels), restored with one click.</summary>
public sealed class ViewsPalette(IShell shell) : IPalettePlugin
{
    public string Id => "builtin.palette.views";

    public string DisplayName => Title;

    public PluginManifest Manifest { get; } = PaletteManifest.Create("builtin.palette.views", "Widoki", "Zapisane układy obu paneli.");

    public string Title => "Widoki";

    public PaletteDock DefaultDock => PaletteDock.Bottom;

    public IReadOnlyList<PaletteAction> Actions { get; } =
    [
        new("save", "Zapisz bieżący widok", "+"),
        new("overwrite", "Nadpisz bieżącym układem", "⟲", ForItem: true),
        new("remove", "Usuń widok", "✕", ForItem: true),
    ];

    public event Action? ItemsChanged;

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        IReadOnlyList<PaletteItem> items = [.. shell.App.Settings.SavedViews.Select((v, i) => new PaletteItem(
            i.ToString(System.Globalization.CultureInfo.InvariantCulture), v.Name, "▦",
            $"Lewy: {v.LeftPath}{Mode(v.LeftMode)}\nPrawy: {v.RightPath}{Mode(v.RightMode)}"))];
        return Task.FromResult(items);
    }

    public Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken) =>
        Find(itemId) is { } view ? shell.RestoreViewAsync(view) : Task.CompletedTask;

    public async Task InvokeActionAsync(string actionId, string? itemId, IPluginHost host, CancellationToken cancellationToken)
    {
        var views = shell.App.Settings.SavedViews;
        switch (actionId)
        {
            case "save":
                if (await host.PromptAsync("Zapisz widok", "Nazwa widoku (np. „Projekt + logi”):", $"Widok {views.Count + 1}") is not { Length: > 0 } name)
                {
                    return;
                }

                views.Add(shell.CaptureView(name));
                break;
            case "overwrite" when Find(itemId) is { } view:
                views[views.IndexOf(view)] = shell.CaptureView(view.Name);
                break;
            case "remove" when Find(itemId) is { } view:
                views.Remove(view);
                break;
            default:
                return;
        }

        await shell.App.SaveSettingsAsync();
        ItemsChanged?.Invoke();
    }

    private static string Mode(string? mode) => mode is null ? string.Empty : $" [{mode}]";

    private ViewSetting? Find(string? itemId) =>
        int.TryParse(itemId, out var index) && index >= 0 && index < shell.App.Settings.SavedViews.Count ? shell.App.Settings.SavedViews[index] : null;
}

/// <summary>Most frecent locations from the index (refreshed as the user navigates).</summary>
public sealed class RecentPalette(IShell shell) : IPalettePlugin, INavigationAwarePalette
{
    public string Id => "builtin.palette.recent";

    public string DisplayName => Title;

    public PluginManifest Manifest { get; } = PaletteManifest.Create("builtin.palette.recent", "Ostatnie", "Najczęściej i najświeżej odwiedzane foldery.");

    public string Title => "Ostatnie";

    public PaletteDock DefaultDock => PaletteDock.Hidden;

    public event Action? ItemsChanged
    {
        add { }
        remove { }
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        if (shell.App.Index is not { } index)
        {
            return [];
        }

        var frecent = await index.GetFrecentAsync(null, 12, cancellationToken);
        return [.. frecent.Select(f => new PaletteItem(f.Path, PaletteManifest.ShortName(f.Path), "⟲", $"{f.Path} · {f.Visits}×"))];
    }

    public Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken) => host.NavigateAsync(itemId);
}
