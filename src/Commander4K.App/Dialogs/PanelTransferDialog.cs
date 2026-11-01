using Avalonia.Controls;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Jobs;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

/// <param name="OptionId">The receiver or provider chosen.</param>
/// <param name="Target">Folder to write to (providing only), <c>null</c> when receiving.</param>
public sealed record PanelTransferChoice(string OptionId, string? Target, OverwritePolicy Overwrite);

/// <summary>
/// F5 to or from a plugin panel: what is sent, to which receiver (or from which provider) — a list only when there is a
/// choice — with its description, and for providing the target folder and the overwrite policy.
/// </summary>
public sealed class PanelTransferDialog : DialogWindow<PanelTransferChoice?>
{
    private readonly IReadOnlyList<(string Id, string Title, string? Description)> _options;
    private readonly ListBox? _list;
    private readonly TextBlock _optionDescription;
    private readonly TextBox? _target;
    private readonly CheckBox? _overwrite;

    /// <param name="target">Initial target folder; <c>null</c> hides the target and overwrite fields (receiving).</param>
    public PanelTransferDialog(SkinBrushes brushes, string title, string description, IReadOnlyList<(string Id, string Title, string? Description)> options, string? target)
        : base(title, brushes, 560)
    {
        _options = options;
        _optionDescription = new TextBlock().TextWrapping(TextWrapping.Wrap).Opacity(0.75).Margin(0, 4, 0, 0);
        var body = new List<Control> { Label(description) };
        if (options.Count > 1)
        {
            _list = new ListBox().MaxHeight(260).ItemsSource(options.Select(option => new ListBoxItem
            {
                Content = new TextBlock().Text(option.Title).FontWeight(FontWeight.SemiBold),
            }).ToList());
            _list.SelectionChanged += (_, _) => ShowDescription();
            _list.DoubleTapped += (_, _) => Accept();
            _list.SelectedIndex = 0;
            body.Add(_list);
        }
        else if (options.Count == 1)
        {
            body.Add(new TextBlock().Text(options[0].Title).FontWeight(FontWeight.SemiBold));
        }

        body.Add(_optionDescription);
        if (target is not null)
        {
            _target = new TextBox().Text(target);
            _overwrite = new CheckBox().Content(Loc.T("Nadpisuj istniejące pliki"));
            body.Add(Label(Loc.T("Do folderu:")).Margin(0, 10, 0, 6));
            body.Add(_target);
            body.Add(_overwrite.Margin(0, 8, 0, 0));
        }

        body.Add(ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), ("OK", Accept, true)));
        SetBody([.. body]);
        ShowDescription();
        Opened += (_, _) =>
        {
            if (_list is not null)
            {
                _list.Focus();
            }
            else
            {
                _target?.Focus();
            }
        };
    }

    private int SelectedIndex => _list?.SelectedIndex ?? 0;

    private void ShowDescription()
    {
        var description = SelectedIndex >= 0 && SelectedIndex < _options.Count ? _options[SelectedIndex].Description : null;
        _optionDescription.Text = description ?? string.Empty;
        _optionDescription.IsVisible = !string.IsNullOrEmpty(description);
    }

    private void Accept()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _options.Count)
        {
            return;
        }

        string? target = null;
        if (_target is not null)
        {
            target = _target.Text?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                return;
            }
        }

        Complete(new PanelTransferChoice(
            _options[SelectedIndex].Id,
            target,
            _overwrite?.IsChecked == true ? OverwritePolicy.Overwrite : OverwritePolicy.Skip));
    }
}
