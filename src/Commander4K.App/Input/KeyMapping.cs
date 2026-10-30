using Avalonia.Input;
using Commander4K.Core.Input;
using CoreModifiers = Commander4K.Core.Input.KeyModifiers;

namespace Commander4K.App.Input;

/// <summary>Translates Avalonia key events into the UI-agnostic <see cref="KeyStroke"/> used by the keymap.</summary>
public static class KeyMapping
{
    public static KeyStroke? ToStroke(KeyEventArgs e)
    {
        if (e.Key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return null;
        }

        var modifiers = CoreModifiers.None;
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0) modifiers |= CoreModifiers.Ctrl;
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Alt) != 0) modifiers |= CoreModifiers.Alt;
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0) modifiers |= CoreModifiers.Shift;
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Meta) != 0) modifiers |= CoreModifiers.Win;

        // Key.Enter/Return and PageUp/Prior share values; KeyStroke normalises whichever name ToString picks.
        return new KeyStroke(e.Key.ToString(), modifiers);
    }

    public static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;
}
