using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.Core.Localization;

namespace Commander4K.App.Theming;

/// <summary>Button roles of the design system (docs/DesignSystem, "Komponenty i stany").</summary>
public enum ButtonKind
{
    /// <summary>Outlined, on the panel background: Cancel, Test, secondary actions.</summary>
    Secondary,

    /// <summary>Filled with the accent: the one action Enter runs.</summary>
    Primary,

    /// <summary>Filled with the error colour: deletes or overwrites for good.</summary>
    Danger,

    /// <summary>Transparent until hovered: icon buttons, close.</summary>
    Ghost,

    /// <summary>26 px, on <c>surface2</c>: buttons in tool bars and inside cards.</summary>
    Toolbar,
}

/// <summary>
/// The building blocks every window and dialog is made of, so they share one look: header band, section headings,
/// field labels, hints, code boxes, cards, buttons and the footer band. All colours come from the skin
/// (<see cref="SkinBrushes"/>); retro skins (radius 0) get the same structure without rounded corners and fills.
/// </summary>
/// <remarks>
/// Measurements (Grafit): dialog header 44 px with an 18 px icon and a 15/600 title; body padding 16/18 with 12 px
/// between groups; footer band on <c>headerBackground</c> with a top rule, padding 12/18 and buttons to the right;
/// buttons 30 px high, 88 px minimum, radius 4; field labels 12/600 in <c>headerForeground</c>; section headings
/// 11/600 capitals in <c>accent</c>; tool-window header 18/600 with a muted 12 px description.
/// </remarks>
public static class Ds
{
    public const double ButtonHeight = 30;
    public const double ToolbarButtonHeight = 26;
    public const double ButtonMinWidth = 88;
    public const double DialogHeaderHeight = 44;
    public static readonly Thickness BodyPadding = new(18, 16);
    public static readonly Thickness FooterPadding = new(18, 12);
    public static readonly Thickness WindowHeaderPadding = new(20, 14);
    public static readonly Thickness WindowBodyPadding = new(24, 18, 24, 24);
    public const double GroupSpacing = 12;
    public const double CardRadius = 6;
    public const double WindowRadius = 8;

    public static Button Button(SkinBrushes b, string text, Action onClick, ButtonKind kind = ButtonKind.Secondary, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            Height = kind == ButtonKind.Toolbar ? ToolbarButtonHeight : ButtonHeight,
            Padding = new Thickness(kind == ButtonKind.Toolbar ? 10 : 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(b.ControlRadius),
        };
        Style(button, b, kind);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Paints an existing button in a role (buttons created elsewhere, e.g. with a custom content).</summary>
    public static Button Style(Button button, SkinBrushes b, ButtonKind kind)
    {
        switch (kind)
        {
            case ButtonKind.Primary:
                button.Background = b.Accent;
                button.BorderBrush = b.Accent;
                button.Foreground = b.AccentForeground;
                button.FontWeight = FontWeight.SemiBold;
                break;
            case ButtonKind.Danger:
                button.Background = b.Error;
                button.BorderBrush = b.Error;
                button.Foreground = b.ErrorForeground;
                button.FontWeight = FontWeight.SemiBold;
                break;
            case ButtonKind.Ghost:
                // Flat icon/tool button: hover and pressed come from the Fluent resources the skin sets; retro skins keep stock buttons.
                if (b.IsModern)
                {
                    button.Background = Brushes.Transparent;
                    button.BorderThickness = new Thickness(0);
                    button.Foreground = b.WindowForeground;
                    button.CornerRadius = new CornerRadius(b.ControlRadius);
                    button.Height = ToolbarButtonHeight;
                    button.MinWidth = 28;
                    button.Padding = new Thickness(6, 0);
                }
                else
                {
                    button.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty);
                    button.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BorderThicknessProperty);
                    button.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty);
                    button.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty);
                    button.Height = 24;
                    button.MinWidth = 0;
                    button.Padding = new Thickness(8, 2);
                }

                button.FontSize = Math.Max(12, b.FontSize);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
                break;
            case ButtonKind.Toolbar:
                button.Background = b.Surface2;
                button.BorderBrush = b.PanelBorder;
                button.Foreground = b.FileForeground;
                button.FontSize = Math.Max(11, b.FontSize - 1);
                break;
            default:
                if (b.IsModern)
                {
                    button.Background = b.PanelBackground;
                    button.BorderBrush = b.PanelBorder;
                    button.Foreground = b.FileForeground;
                }

                break;
        }

        return button;
    }

    /// <summary>
    /// The footer band: <paramref name="leading"/> on the left (a secondary action or a status line), the buttons to
    /// the right, each at least 88 px wide. Modern skins fill it with <c>headerBackground</c> above a 1 px rule.
    /// </summary>
    public static Border Footer(SkinBrushes b, Control? leading, params Control[] buttons)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var control in buttons)
        {
            if (control is Button button)
            {
                button.MinWidth = Math.Max(button.MinWidth, ButtonMinWidth);
            }

            right.Children.Add(control);
        }

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        if (leading is not null)
        {
            leading.VerticalAlignment = VerticalAlignment.Center;
            leading.Margin = new Thickness(0, 0, 12, 0);
            if (leading is Button leadingButton)
            {
                leadingButton.MinWidth = Math.Max(leadingButton.MinWidth, ButtonMinWidth);
                leadingButton.HorizontalAlignment = HorizontalAlignment.Left;
            }

            row.Children.Add(leading);
        }

        return Band(b, row);
    }

    /// <summary>
    /// The band under a window's content (footer, chat composer): <c>headerBackground</c> on modern skins, a 1 px rule
    /// above, 12/18 px padding.
    /// </summary>
    public static Border Band(SkinBrushes b, Control child) => new()
    {
        Background = b.IsModern ? b.HeaderBackground : Brushes.Transparent,
        BorderBrush = b.PanelBorder,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = FooterPadding,
        Child = child,
    };

    /// <summary>Header band of a tool window: 18/600 title, an optional muted description, a rule below.</summary>
    public static Border WindowHeader(SkinBrushes b, string title, string? description = null, Control? trailing = null)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(WindowTitle(b, title));
        if (description is not null)
        {
            text.Children.Add(Muted(b, description));
        }

        Control child = text;
        if (trailing is not null)
        {
            var dock = new DockPanel();
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(trailing, Dock.Right);
            dock.Children.Add(trailing);
            dock.Children.Add(text);
            child = dock;
        }

        return new Border
        {
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = WindowHeaderPadding,
            Child = child,
        };
    }

    /// <summary>Title of a tool window: 18/600 in the main text colour.</summary>
    public static TextBlock WindowTitle(SkinBrushes b, string title) => new()
    {
        Text = title,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = b.FileForeground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Header band of a dialog: 44 px, an optional 18 px glyph in <paramref name="iconBrush"/>, a 15/600 title.</summary>
    public static Border DialogHeader(SkinBrushes b, string title, string? icon = null, IBrush? iconBrush = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            row.Children.Add(new TextBlock { Text = icon, FontSize = 16, Foreground = iconBrush ?? b.Accent, VerticalAlignment = VerticalAlignment.Center });
        }

        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = b.FileForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            MinHeight = DialogHeaderHeight,
            Padding = new Thickness(18, 0),
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
    }

    /// <summary>Section heading: 11/600 capitals in the accent colour.</summary>
    public static TextBlock SectionHeading(SkinBrushes b, string text) => new()
    {
        Text = text.ToUpper(Loc.Culture),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.8,
        Foreground = b.Accent,
    };

    /// <summary>Label above a field: 12/600 in <c>headerForeground</c>.</summary>
    public static TextBlock FieldLabel(SkinBrushes b, string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = b.HeaderForeground,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    /// <summary>A label with its field below it.</summary>
    public static StackPanel Field(SkinBrushes b, string label, Control field) => new()
    {
        Children = { FieldLabel(b, label), field },
    };

    /// <summary>Secondary text: 12 px, muted, wrapping.</summary>
    public static TextBlock Muted(SkinBrushes b, string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = b.MutedForeground,
    };

    /// <summary>Body text in the dialog's main colour.</summary>
    public static TextBlock Text(SkinBrushes b, string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = b.FileForeground,
    };

    /// <summary>Selectable monospace text for commands, paths and fingerprints.</summary>
    public static SelectableTextBlock Code(SkinBrushes b, string? text = null) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = b.MonospaceFamily,
        FontSize = 12,
        Foreground = b.FileForeground,
    };

    /// <summary>A well on <c>windowBackground</c> (code, previews, lists inside a window).</summary>
    public static Border Box(SkinBrushes b, Control content, Thickness? padding = null) => new()
    {
        Background = b.WindowBackground,
        BorderBrush = b.PanelBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(b.IsModern ? CardRadius : 0),
        Padding = padding ?? new Thickness(10, 8),
        Child = content,
    };

    /// <summary>A card: like <see cref="Box"/> with more room, for one item of a list (a provider, a plugin).</summary>
    public static Border Card(SkinBrushes b, Control content) => Box(b, content, new Thickness(14, 12));

    /// <summary>A small rounded label (status, tier, version).</summary>
    public static Border Chip(SkinBrushes b, string text, IBrush? foreground = null) => new()
    {
        CornerRadius = new CornerRadius(10),
        BorderBrush = b.PanelBorder,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 1),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground ?? b.HeaderForeground },
    };

    /// <summary>A coloured note (warning / error / info) with the matching background token.</summary>
    public static Border Notice(SkinBrushes b, string text, NoticeKind kind = NoticeKind.Info) => new()
    {
        Background = kind switch
        {
            NoticeKind.Warning => b.WarningBackground,
            NoticeKind.Error => b.ErrorBackground,
            _ => b.HighlightBackground,
        },
        CornerRadius = new CornerRadius(b.IsModern ? CardRadius : 0),
        Padding = new Thickness(10, 8),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = kind switch
            {
                NoticeKind.Warning => b.Warning,
                NoticeKind.Error => b.Error,
                _ => b.HighlightForeground,
            },
        },
    };

    /// <summary>
    /// Paints a window's surface and text: <see cref="WindowSurface.Window"/> for the main window and the splash,
    /// <see cref="WindowSurface.Panel"/> for tool windows, <see cref="WindowSurface.Raised"/> for dialogs.
    /// </summary>
    public static void ApplySurface(Window window, SkinBrushes b, WindowSurface surface)
    {
        window.Background = surface switch
        {
            WindowSurface.Raised when b.IsModern => b.Surface2,
            WindowSurface.Panel => b.PanelBackground,
            _ => b.WindowBackground,
        };
        window.Foreground = b.WindowForeground;
        window.FontFamily = b.FontFamily;
        window.FontSize = b.FontSize;
    }

    /// <summary>Header, scrolling body and footer: the layout every tool window uses.</summary>
    public static DockPanel Frame(Control header, Control? footer, Control body)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        if (footer is not null)
        {
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
        }

        dock.Children.Add(body);
        return dock;
    }

    /// <summary>Search field: 6 px corners, a ⌕ glyph inside on the left, the text box keeps its own focus ring.</summary>
    public static Grid SearchField(SkinBrushes b, TextBox box, double height = 34)
    {
        box.Height = height;
        box.Padding = new Thickness(34, 0, 10, 0);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.CornerRadius = new CornerRadius(b.IsModern ? 6 : 0);
        box.Background = b.IsModern ? b.HeaderBackground : b.PanelBackground;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(new TextBlock
        {
            Text = "⌕",
            FontSize = 17,
            Foreground = b.MutedForeground,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 1),
        });
        return grid;
    }

    /// <summary>Flat icon button with a tooltip (<see cref="ButtonKind.Ghost"/>): tool bars, back/forward, close.</summary>
    public static Button ToolButton(SkinBrushes b, object content, string tip, Action click)
    {
        var button = new Button { Content = content };
        button.Click += (_, _) => click();
        ToolTip.SetTip(button, tip);
        return Style(button, b, ButtonKind.Ghost);
    }
}

public enum NoticeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Which surface a window is painted on (<see cref="Ds.ApplySurface"/>).</summary>
public enum WindowSurface
{
    /// <summary><c>windowBackground</c>: the main window, the splash.</summary>
    Window,

    /// <summary><c>panelBackground</c>: tool windows.</summary>
    Panel,

    /// <summary><c>surface2</c> on modern skins: dialogs.</summary>
    Raised,
}
