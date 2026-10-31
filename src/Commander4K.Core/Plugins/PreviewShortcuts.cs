using Commander4K.Core.Input;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Which keys a plugin preview's buttons may take. Esc, Enter, Shift+Enter, Alt+Enter, Tab, Backspace and Alt+←/→ always belong to the app (Ctrl+Enter stays free)
/// (close, open, focus, back), and while the user types in a field the text-editing chords belong to the field.
/// </summary>
public static class PreviewShortcuts
{
    /// <returns>The shortcut in <see cref="KeyStroke.ToString"/> form, or <c>null</c> when it is invalid or reserved.</returns>
    public static string? Normalize(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return null;
        }

        KeyStroke stroke;
        try
        {
            stroke = KeyStroke.Parse(shortcut);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }

        return IsReserved(stroke) ? null : stroke.ToString();
    }

    public static bool IsReserved(KeyStroke stroke) =>
        stroke.Key is "ESCAPE" or "TAB" or "BACKSPACE"
        || (stroke.Key == "ENTER" && stroke.Modifiers is KeyModifiers.None or KeyModifiers.Shift or KeyModifiers.Alt)
        || (stroke.Modifiers == KeyModifiers.Alt && stroke.Key is "LEFT" or "RIGHT");

    /// <summary>Ctrl+A/C/V/X/Z/Y, Ctrl+arrows, Ctrl+Backspace/Delete, Ctrl+Home/End (with or without Shift).</summary>
    public static bool IsTextEditing(KeyStroke stroke) =>
        (stroke.Modifiers & ~KeyModifiers.Shift) == KeyModifiers.Ctrl
        && stroke.Key is "A" or "C" or "V" or "X" or "Z" or "Y" or "LEFT" or "RIGHT" or "UP" or "DOWN" or "BACKSPACE" or "DELETE" or "HOME" or "END";
}
