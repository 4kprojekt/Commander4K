using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Commander4K.App.Composition;
using Commander4K.Core.Theming;

namespace Commander4K.App.Theming;

/// <summary>
/// Holds the active skin; every view repaints from <see cref="Changed"/>. Also pushes the skin into the Fluent
/// control resources (buttons, text boxes, lists, switches, menus), so standard controls follow the design system
/// instead of stock Fluent colours. UI thread only.
/// </summary>
public sealed class SkinManager
{
    private readonly AppHost? _host;

    public SkinManager(AppHost? host, Skin initial)
    {
        _host = host;
        Current = new SkinBrushes(initial, host?.Settings.Density);
        ApplyToControls(Current);
    }

    public SkinBrushes Current { get; private set; }

    public event Action<SkinBrushes>? Changed;

    /// <summary>Shows a skin without remembering it (live preview in the picker and the AI generator).</summary>
    public void Preview(Skin skin)
    {
        Current = new SkinBrushes(skin, _host?.Settings.Density);
        ApplyToControls(Current);
        Changed?.Invoke(Current);
    }

    /// <summary>Applies and persists the skin as the user's choice.</summary>
    public void Apply(Skin skin)
    {
        Preview(skin);
        if (_host is not null)
        {
            _host.Settings.Skin = skin.Id;
            _ = _host.SaveSettingsAsync();
        }
    }

    /// <summary>Re-creates the brushes after a density change.</summary>
    public void Refresh() => Preview(Current.Skin);

    private static void ApplyToControls(SkinBrushes b)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = string.Equals(b.Skin.Chrome.ThemeVariant, "Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        var r = app.Resources;
        var accent = SkinBrushes.ToColor(b.Skin.Palette.Accent);
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorDark1"] = accent;
        r["SystemAccentColorDark2"] = accent;
        r["SystemAccentColorLight1"] = accent;
        r["SystemAccentColorLight2"] = accent;
        r["ControlCornerRadius"] = new CornerRadius(b.ControlRadius);
        r["OverlayCornerRadius"] = new CornerRadius(b.IsModern ? 6 : 0);

        // Buttons: raised surface, keyboard focus, hover and pressed states.
        Set(r, b.Surface2, "ButtonBackground", "ButtonBackgroundDisabled", "RepeatButtonBackground", "ToggleButtonBackground");
        Set(r, b.HoverBackground, "ButtonBackgroundPointerOver", "RepeatButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver");
        Set(r, b.PressedBackground, "ButtonBackgroundPressed", "RepeatButtonBackgroundPressed", "ToggleButtonBackgroundPressed");
        Set(r, b.WindowForeground, "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "RepeatButtonForeground", "ToggleButtonForeground");
        Set(r, b.MutedForeground, "ButtonForegroundDisabled");
        Set(r, b.PanelBorder, "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled", "RepeatButtonBorderBrush", "ToggleButtonBorderBrush");
        Set(r, b.FocusRing, "ButtonBorderBrushFocused", "ToggleButtonBorderBrushFocused");

        // Text boxes: code-oriented inputs retain the same skin, including selection and focus.
        Set(r, b.PanelBackground, "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled");
        Set(r, b.FileForeground, "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        Set(r, b.MutedForeground, "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused", "TextControlForegroundDisabled");
        Set(r, b.PanelBorder, "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushDisabled");
        Set(r, b.FocusRing, "TextControlBorderBrushFocused");
        r["TextControlSelectionHighlightColor"] = new SolidColorBrush(SkinBrushes.ToColor(b.Skin.Palette.CursorBackground));
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(1);

        // Drop-downs and lists.
        Set(r, b.Surface2, "ComboBoxBackground", "ComboBoxBackgroundPressed", "ComboBoxDropDownBackground", "ComboBoxBackgroundDisabled");
        Set(r, b.HoverBackground, "ComboBoxBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver", "ListBoxItemBackgroundPointerOver");
        Set(r, b.PanelBorder, "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxDropDownBorderBrush");
        Set(r, b.FocusRing, "ComboBoxBorderBrushFocused");
        Set(r, b.FileForeground, "ComboBoxForeground", "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundSelected", "ListBoxItemForeground");
        Set(r, b.HighlightBackground, "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ListBoxItemBackgroundSelected", "ListBoxItemBackgroundSelectedPointerOver");
        Set(r, b.PanelBackground, "ListBoxBackground");
        Set(r, b.PanelBorder, "ListBoxBorderBrush");

        // Menus and context menus: raised surface with hover rows.
        Set(r, b.Surface2, "MenuFlyoutPresenterBackground");
        Set(r, b.PanelBorder, "MenuFlyoutPresenterBorderBrush");
        Set(r, b.HoverBackground, "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundSubMenuOpened");
        Set(r, b.PressedBackground, "MenuFlyoutItemBackgroundPressed", "MenuFlyoutSubItemBackgroundPressed");
        Set(r, b.FileForeground, "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemForeground", "MenuFlyoutSubItemForegroundPointerOver");
        Set(r, b.MutedForeground, "MenuFlyoutItemKeyboardAcceleratorTextForeground", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver");

        // Switches and check boxes use the accent when on (design system: 40×20 switch, accent fill).
        Set(r, b.Accent, "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver");
        Set(r, b.MutedForeground, "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchKnobFillOff",
            "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver");
        Set(r, b.PanelBackground, "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "CheckBoxCheckBackgroundFillUnchecked");
        Set(r, b.FocusRing, "CheckBoxCheckBackgroundStrokeCheckedPressed");

        // Tooltips.
        Set(r, b.Surface2, "ToolTipBackground");
        Set(r, b.PanelBorder, "ToolTipBorderBrush");
        Set(r, b.FileForeground, "ToolTipForeground");
    }

    private static void Set(Avalonia.Controls.IResourceDictionary resources, IBrush brush, params string[] keys)
    {
        foreach (var key in keys)
        {
            resources[key] = brush;
        }
    }
}
