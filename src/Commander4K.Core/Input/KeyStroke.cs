using System.Collections.Frozen;
using System.Text;

namespace Commander4K.Core.Input;

[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// UI-agnostic key press. Key names follow Avalonia's <c>Key</c> enum (<c>F5</c>, <c>Tab</c>, <c>Escape</c>, <c>A</c>)
/// and are normalised to upper case; common aliases (<c>Esc</c>, <c>Return</c>, <c>PgDn</c>, <c>D1</c>) map to one name.
/// </summary>
public readonly record struct KeyStroke
{
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ESC"] = "ESCAPE",
        ["RETURN"] = "ENTER",
        ["DEL"] = "DELETE",
        ["INS"] = "INSERT",
        ["BACK"] = "BACKSPACE",
        ["PRIOR"] = "PAGEUP",
        ["PGUP"] = "PAGEUP",
        ["NEXT"] = "PAGEDOWN",
        ["PGDN"] = "PAGEDOWN",
        ["OEMPLUS"] = "PLUS",
        ["ADD"] = "PLUS",
        ["+"] = "PLUS",
        ["OEMMINUS"] = "MINUS",
        ["SUBTRACT"] = "MINUS",
        ["-"] = "MINUS",
        ["MULTIPLY"] = "*",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public KeyStroke(string key, KeyModifiers modifiers = KeyModifiers.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = NormalizeKey(key);
        Modifiers = modifiers;
    }

    public string Key { get; }

    public KeyModifiers Modifiers { get; }

    /// <summary>Parses <c>Ctrl+Shift+F5</c>; modifier names are case-insensitive, <c>Control</c> and <c>Ctrl</c> are equal.</summary>
    public static KeyStroke Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();

        // "Ctrl++" means Ctrl and the plus key.
        var keyPart = trimmed.EndsWith("++", StringComparison.Ordinal) ? "+" : null;
        var parts = (keyPart is null ? trimmed : trimmed[..^2]).Split('+', StringSplitOptions.TrimEntries);

        var modifiers = KeyModifiers.None;
        var modifierCount = keyPart is null ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < modifierCount; i++)
        {
            modifiers |= parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Ctrl,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                "WIN" or "META" or "CMD" => KeyModifiers.Win,
                _ => throw new FormatException($"Unknown modifier '{parts[i]}' in '{text}'."),
            };
        }

        keyPart ??= parts[^1];
        if (keyPart.Length == 0)
        {
            throw new FormatException($"Missing key in '{text}'.");
        }

        return new KeyStroke(keyPart, modifiers);
    }

    public override string ToString()
    {
        var builder = new StringBuilder(16);
        if ((Modifiers & KeyModifiers.Ctrl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & KeyModifiers.Alt) != 0) builder.Append("Alt+");
        if ((Modifiers & KeyModifiers.Shift) != 0) builder.Append("Shift+");
        if ((Modifiers & KeyModifiers.Win) != 0) builder.Append("Win+");
        return builder.Append(Key).ToString();
    }

    private static string NormalizeKey(string key)
    {
        var trimmed = key.Trim();
        if (Aliases.TryGetValue(trimmed, out var alias))
        {
            return alias;
        }

        // Avalonia names digit keys D0..D9 and numpad keys NumPad0..NumPad9.
        if (trimmed.Length == 2 && (trimmed[0] is 'D' or 'd') && char.IsAsciiDigit(trimmed[1]))
        {
            return trimmed[1..];
        }

        return trimmed.ToUpperInvariant();
    }
}
