using Avalonia.Controls;
using Avalonia.Markup.Declarative;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

public sealed class LanScanDialog : DialogWindow<bool?>
{
    private readonly CheckBox _popularPorts;

    public LanScanDialog(SkinBrushes brushes, bool popularPorts) : base(Loc.T("Skanuj adresy IP w LAN"), brushes, 520)
    {
        _popularPorts = new CheckBox()
            .Content(Loc.T("Sprawdzaj też popularne porty usług (HTTP, HTTPS, Winbox, API MikroTik i inne)"))
            .IsChecked(popularPorts);

        SetBody(
            new TextBlock().Text(Loc.T("Skan obejmie adresy z lokalnych podsieci IPv4 (maksymalnie /24) i tylko znane porty TCP. Nie skanuje Internetu."))
                .TextWrapping(Avalonia.Media.TextWrapping.Wrap),
            _popularPorts.Margin(0, 10, 0, 0),
            ButtonRow(
                (Loc.T("Skanuj"), Accept, true),
                (Loc.T("Anuluj"), () => Complete(null), false)));
    }

    private void Accept() => Complete(_popularPorts.IsChecked == true);
}
