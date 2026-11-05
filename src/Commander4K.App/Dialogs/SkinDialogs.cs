using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App.Dialogs;

/// <summary>
/// The skins: the view of <c>config/skins/</c>, opened as a panel tab on that folder (the Pliki/Podgląd switch shows
/// the JSON files). Clicking a skin applies it at once and saves the choice; the contrast check of the skin on screen
/// is shown under the list.
/// </summary>
public sealed class SkinsView : Panels.FolderModeView
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;

    public SkinsView(AppHost host, SkinManager skins) : base(skins.Current)
    {
        _host = host;
        _skins = skins;
        Build();
    }

    public override string Title => Loc.T("Skórki");

    public override bool HandleKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return false;
        }

        RequestClose();
        return true;
    }

    protected override Control BuildLayout()
    {
        var b = Brushes;
        var current = _skins.Current.Skin;
        var builtIn = BuiltInSkins.All.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cards = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        foreach (var skin in _host.Skins.Skins)
        {
            var chosen = string.Equals(skin.Id, current.Id, StringComparison.OrdinalIgnoreCase);
            var card = new Button
            {
                Padding = new Thickness(0),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = SkinCard(skin, builtIn.Contains(skin.Id), chosen, b),
            };
            ToolTip.SetTip(card, skin.Id);
            card.Click += (_, _) =>
            {
                if (!chosen)
                {
                    _skins.Apply(skin); // repaints the app, this view included
                }
            };
            cards.Children.Add(card);
        }

        var body = new StackPanel { Spacing = 14, Margin = Ds.WindowBodyPadding };
        body.Children.Add(Ds.SectionHeading(b, Loc.T("Wybierz skórkę")));
        body.Children.Add(cards);

        var warnings = SkinValidator.Validate(current);
        body.Children.Add(Ds.Notice(b, warnings.Count == 0
            ? Loc.T("✓ {0}: kontrast i czytelność w porządku.", current.DisplayName)
            : "⚠ " + string.Join("\n⚠ ", warnings), warnings.Count == 0 ? NoticeKind.Info : NoticeKind.Warning));
        if (_host.Skins.Errors.Count > 0)
        {
            body.Children.Add(Ds.Notice(b, Loc.T("Nie wczytano skórek:") + "\n" +
                string.Join("\n", _host.Skins.Errors.Select(e => Loc.T("{0}: {1}", Path.GetFileName(e.Path), e.Error))), NoticeKind.Error));
        }

        body.Children.Add(Ds.Muted(b, Loc.T("Własne skórki: pliki JSON w folderze {0} (schemat: {1}).", _host.Config.SkinsDirectory, SkinSchema.FileName)));

        var footer = Ds.Footer(b, null,
            Ds.Button(b, Loc.T("✏ Edytuj…"), async () => await EditAsync(current)),
            Ds.Button(b, Loc.T("🔄 Przeładuj"), async () => await _host.ReloadSkinsAsync().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Build))),
            Ds.Button(b, Loc.T("Pokaż pliki"), RequestClose, ButtonKind.Primary));
        return Ds.Frame(Ds.WindowHeader(b, Loc.T("Skórki"), Loc.T("Kolory, krój pisma i ramki całej aplikacji. Kliknięcie skórki od razu ją włącza.")), footer, Scroll(body));
    }

    /// <summary>A skin painted in its own colours: swatches, name, font; the active one with an accent ring.</summary>
    private static Border SkinCard(Skin skin, bool builtIn, bool chosen, SkinBrushes current)
    {
        var brushes = new SkinBrushes(skin);
        return new Border().Width(220).Background(brushes.PanelBackground)
            .BorderBrush(chosen ? current.Accent : brushes.PanelBorder).BorderThickness(new Thickness(chosen ? 2 : 1))
            .CornerRadius(new CornerRadius(Math.Max(brushes.ControlRadius, current.IsModern ? Ds.CardRadius : 0))).Padding(10, 8)
            .Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(10).Children(
                Swatch(skin),
                new StackPanel().Spacing(2).VerticalAlignment(VerticalAlignment.Center).Children(
                    new TextBlock().Text(skin.DisplayName).Foreground(brushes.WindowForeground).FontWeight(FontWeight.SemiBold)
                        .TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(130),
                    new TextBlock().Text(builtIn ? skin.Typography.FontFamily : Loc.T("(własna)"))
                        .Foreground(brushes.MutedForeground).FontSize(11).TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(130))));
    }

    /// <summary>Four colour chips: panel, directory, cursor, selection.</summary>
    private static StackPanel Swatch(Skin skin)
    {
        var p = skin.Palette;
        var border = new SolidColorBrush(SkinBrushes.ToColor(p.PanelBorder));
        return new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).Children(
            [.. new[] { p.PanelBackground, p.DirectoryForeground, p.CursorBackground, p.SelectedForeground }
                .Select(c => (Control)new Border().Width(12).Height(24).CornerRadius(new CornerRadius(2))
                    .Background(new SolidColorBrush(SkinBrushes.ToColor(c))).BorderBrush(border).BorderThickness(new Thickness(1)))]);
    }

    private async Task EditAsync(Skin? skin)
    {
        if (OwnerWindow is not { } owner)
        {
            return;
        }

        await new SkinEditorDialog(_host, _skins, skin).ShowAsync(owner);
        Build();
    }
}

/// <summary>Clipboard and Explorer helpers shared by the dialogs.</summary>
internal static class DesktopShell
{
    internal static async Task CopyToClipboardAsync(Window window, string text)
    {
        if (window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>Creates the folder if needed and opens it in Explorer, off the UI thread (both touch the disk or start a process).</summary>
    internal static void OpenFolder(string path) => _ = Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(path);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Opening {path} failed: {ex.Message}");
        }
    });
}
