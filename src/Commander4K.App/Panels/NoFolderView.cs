using Avalonia.Controls;
using Avalonia.Input;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Panels;

/// <summary>Widok mode on a folder without a view of its own: says so and offers to configure one; Esc shows the files.</summary>
internal sealed class NoFolderView : FolderModeView
{
    private readonly string _name;
    private readonly Action _configure;

    public NoFolderView(SkinBrushes brushes, string name, Action configure) : base(brushes)
    {
        _name = name;
        _configure = configure;
        Build();
    }

    public override string Title => _name;

    public override bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            RequestClose();
            return true;
        }

        return false;
    }

    protected override Control BuildLayout() => Ds.EmptyState(Brushes,
        Loc.T("Brak zdefiniowanego podglądu"),
        Loc.T("Folder „{0}” nie ma pliku .c4k z widokiem. Esc pokazuje pliki; tryb Auto pokazuje widok tylko tam, gdzie folder go ma.", _name),
        Loc.T("Skonfiguruj widok folderu…"),
        _configure);
}
