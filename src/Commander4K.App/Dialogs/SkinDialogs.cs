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

/// <summary>Skin list with live preview; Cancel restores the skin that was active before.</summary>
public sealed class SkinPickerDialog : DialogWindow<bool>
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly Skin _original;
    private readonly ListBox _list;
    private readonly TextBlock _warnings;
    private readonly Border _warningCard;
    private readonly TextBlock _errors;
    private readonly Border _errorCard;
    private readonly TextBlock _info;

    public SkinPickerDialog(AppHost host, SkinManager skins) : base(Loc.T("Skórki"), skins.Current, 640)
    {
        _host = host;
        _skins = skins;
        _original = skins.Current.Skin;
        _list = new ListBox().Height(280);
        _list.SelectionChanged += (_, _) => PreviewSelected();
        _warnings = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12);
        _warningCard = new Border().IsVisible(false).Padding(10, 7).BorderThickness(1)
            .Child(_warnings);
        _errors = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12);
        _errorCard = new Border().IsVisible(false).Padding(10, 8).BorderThickness(1)
            .Child(new StackPanel().Spacing(4).Children(
                new TextBlock().Text(Loc.T("Nie wczytano skórek:")).FontWeight(FontWeight.SemiBold),
                new ScrollViewer().MaxHeight(88).Content(_errors)));
        _info = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12).Opacity(0.75).Margin(0, 6, 0, 0)
            .Text(Loc.T("Własne skórki: pliki JSON w folderze {0} (schemat: {1}).", host.Config.SkinsDirectory, SkinSchema.FileName));

        SetBody(
            Label(Loc.T("Wybierz skórkę — podgląd działa na żywo:")),
            _list,
            _warningCard,
            _errorCard,
            new Border().Background(skins.Current.Surface2).CornerRadius(new CornerRadius(skins.Current.ControlRadius))
                .Padding(10, 8).Child(new WrapPanel().Children(
                    Action(Loc.T("✨ Stwórz nową z AI…"), CreateWithAiAsync),
                    Action(Loc.T("✏ Edytuj…"), EditAsync),
                    Action(Loc.T("📂 Otwórz folder skórek"), () => FolderOpener.OpenAsync(this, Brushes, host.Config.SkinsDirectory)),
                    Action(Loc.T("🔄 Przeładuj"), ReloadAsync))),
            _info,
            ButtonRow(("OK", Accept, true), (Loc.T("Anuluj"), Cancel, false)));
        _skins.Changed += ApplySkin;
        Closed += (_, _) => _skins.Changed -= ApplySkin;
        ApplySkin(skins.Current);
        Fill(_original.Id);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private Skin? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Skin;

    private Button Action(string text, Func<Task> onClick) =>
        Ds.Button(Brushes, text, async () => await onClick()).Margin(0, 0, 6, 6);

    private void Fill(string selectId)
    {
        var builtIn = BuiltInSkins.All.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = _host.Skins.Skins.Select(skin => new ListBoxItem
        {
            Tag = skin,
            Content = SkinCard(skin, builtIn.Contains(skin.Id)),
        }).ToList();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => string.Equals(((Skin)i.Tag!).Id, selectId, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();

        _errors.Text = string.Join(Environment.NewLine, _host.Skins.Errors.Select(e => Loc.T("{0}: {1}", Path.GetFileName(e.Path), e.Error)));
        _errorCard.IsVisible = _host.Skins.Errors.Count > 0;
    }

    private static Border SkinCard(Skin skin, bool builtIn)
    {
        var p = skin.Palette;
        var brushes = new SkinBrushes(skin);
        return new Border().Background(brushes.PanelBackground).BorderBrush(brushes.PanelBorder).BorderThickness(new Thickness(1))
            .CornerRadius(new CornerRadius(brushes.ControlRadius)).Padding(9, 6)
            .Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(10).Children(
                Swatch(skin),
                new StackPanel().Spacing(2).VerticalAlignment(VerticalAlignment.Center).Children(
                    new TextBlock().Text(skin.DisplayName).Foreground(brushes.WindowForeground).FontWeight(FontWeight.SemiBold),
                    new TextBlock().Text(builtIn ? skin.Typography.FontFamily : Loc.T("(własna)"))
                        .Foreground(brushes.MutedForeground).FontSize(11))));
    }

    /// <summary>Four colour chips: panel, directory, cursor, selection.</summary>
    private static StackPanel Swatch(Skin skin)
    {
        var p = skin.Palette;
        var border = new SolidColorBrush(SkinBrushes.ToColor(p.PanelBorder));
        return new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).Children(
            [.. new[] { p.PanelBackground, p.DirectoryForeground, p.CursorBackground, p.SelectedForeground }
                .Select(c => (Control)new Border().Width(16).Height(16).CornerRadius(new CornerRadius(2))
                    .Background(new SolidColorBrush(SkinBrushes.ToColor(c))).BorderBrush(border).BorderThickness(new Thickness(1)))]);
    }

    private void PreviewSelected()
    {
        if (Selected is not { } skin)
        {
            return;
        }

        _skins.Preview(skin);
        var warnings = SkinValidator.Validate(skin);
        _warnings.Text = warnings.Count == 0 ? Loc.T("✓ Kontrast i czytelność w porządku.") : "⚠ " + string.Join("\n⚠ ", warnings);
        _warningCard.IsVisible = true;
        _warningCard.Background = warnings.Count == 0 ? _skins.Current.HighlightBackground : _skins.Current.WarningBackground;
        _warningCard.BorderBrush = warnings.Count == 0 ? _skins.Current.Success : _skins.Current.Warning;
        _warnings.Foreground = warnings.Count == 0 ? _skins.Current.Success : _skins.Current.Warning;
    }

    private void ApplySkin(SkinBrushes brushes)
    {
        Ds.ApplySurface(this, brushes, WindowSurface.Raised);
        _warningCard.CornerRadius = new CornerRadius(brushes.ControlRadius);
        _errorCard.CornerRadius = new CornerRadius(brushes.ControlRadius);
        _errorCard.Background = brushes.WarningBackground;
        _errorCard.BorderBrush = brushes.Warning;
        _errors.Foreground = brushes.Warning;
    }

    private void Accept()
    {
        if (Selected is { } skin)
        {
            _skins.Apply(skin);
        }

        Complete(true);
    }

    private void Cancel()
    {
        _skins.Preview(_original);
        Complete(false);
    }

    private async Task CreateWithAiAsync()
    {
        var draft = await new SkinAiPromptDialog(_skins).ShowAsync(this);
        if (draft is null)
        {
            _skins.Preview(Selected ?? _original);
            return;
        }

        await EditAsync(draft, asDraft: true);
    }

    private Task EditAsync() => EditAsync(Selected, asDraft: false);

    private async Task EditAsync(Skin? skin, bool asDraft)
    {
        var saved = await new SkinEditorDialog(_host, _skins, skin, asDraft).ShowAsync(this);
        Fill(saved?.Id ?? Selected?.Id ?? _original.Id);
        PreviewSelected();
    }

    private async Task ReloadAsync()
    {
        var selected = Selected?.Id;
        await _host.ReloadSkinsAsync();
        Fill(selected ?? _original.Id);
    }

    internal static async Task CopyToClipboardAsync(Window window, string text)
    {
        if (window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    internal static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}
