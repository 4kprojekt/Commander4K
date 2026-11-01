using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;

namespace Commander4K.App.Panels;

/// <summary>
/// The small hint in a panel's corner: "this folder has 42 pictures, switch to the gallery?". It never takes focus, so the
/// cursor keys stay with the file list; Ctrl+Enter (cm_AcceptFolderSuggestion) accepts it from the keyboard.
/// </summary>
public sealed class FolderSuggestionCard : Border
{
    public FolderSuggestionCard(SkinBrushes brushes, RankedSuggestion suggestion)
    {
        Suggestion = suggestion;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(10);
        MaxWidth = 380;
        Padding = new Thickness(12, 10);
        Background = brushes.Surface2;
        BorderBrush = brushes.Accent;
        BorderThickness = new Thickness(brushes.IsModern ? 1 : 2);
        CornerRadius = new CornerRadius(brushes.IsModern ? 8 : 0);
        BoxShadow = brushes.IsModern ? BoxShadows.Parse("0 4 16 0 #40000000") : default;

        Button Action(string text, Action<RankedSuggestion>? handler, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                Focusable = false,
                Padding = new Thickness(10, 3),
                FontSize = brushes.FontSize,
                Foreground = primary ? brushes.AccentForeground : brushes.WindowForeground,
                Background = primary ? brushes.Accent : brushes.HoverBackground,
                CornerRadius = new CornerRadius(brushes.IsModern ? 4 : 0),
            };
            button.Click += (_, _) => handler?.Invoke(Suggestion);
            return button;
        }

        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "💡 " + suggestion.Suggestion.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = brushes.WindowForeground,
                    FontFamily = brushes.FontFamily,
                    FontSize = brushes.FontSize,
                },
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 6,
                    LineSpacing = 6,
                    Children =
                    {
                        Action(Loc.T("Przełącz") + "  (Ctrl+Enter)", s => Accepted?.Invoke(s), primary: true),
                        Action(Loc.T("Nie teraz"), s => Postponed?.Invoke(s)),
                        Action(Loc.T("Nie pytaj o ten folder"), s => Declined?.Invoke(s)),
                    },
                },
            },
        };
    }

    public RankedSuggestion Suggestion { get; }

    public event Action<RankedSuggestion>? Accepted;

    public event Action<RankedSuggestion>? Postponed;

    public event Action<RankedSuggestion>? Declined;
}
