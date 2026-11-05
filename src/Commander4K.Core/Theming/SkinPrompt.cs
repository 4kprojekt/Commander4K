namespace Commander4K.Core.Theming;

/// <summary>The design rules for a skin, handed to MCP clients by get_skin_guide (the client designs the skin itself).</summary>
public static class SkinPrompt
{
    public const string DesignBrief = """
        You design colour skins for Commander4K, a dual-pane, keyboard-driven file manager in the tradition of
        Norton Commander, Midnight Commander and Total Commander. A skin is one JSON object that colours the whole
        shell: two file panels, a cursor bar, column headers, a status line, a command line and a function-key bar
        (F1..F10, each key number followed by a label).

        Design rules:
        - Readability first. Text against its background should reach a WCAG contrast of at least 3:1 (aim for
          4.5:1 for file names): fileForeground, directoryForeground and selectedForeground on panelBackground,
          cursorForeground on cursorBackground, statusForeground on statusBackground,
          functionKeyLabelForeground on functionKeyLabelBackground, commandLineForeground on commandLineBackground.
        - Directories, executables, archives, hidden files, network entries and selected (marked) files must be
          distinguishable from plain files at a glance; selectedForeground must stand out the most.
        - cursorBackground must be clearly visible against panelBackground; inactiveCursorBackground is the cursor
          of the panel without focus, so make it subtler than cursorBackground.
        - Colours are "#RRGGBB" (or "#AARRGGBB" when transparency is wanted).
        - id: short lower-case kebab-case (letters, digits, hyphens), e.g. "solarized-dark". displayName may use
          any language and characters.
        - typography.fontFamily is a comma-separated fallback list of fonts available on Windows 11 (for a retro
          text-mode look use "Cascadia Mono, Consolas, Courier New"). fontSize 11-16, rowHeightFactor 1.2-1.8.
        - chrome.borderStyle is "None", "Single" or "Double" (Double reads as the Norton Commander box frame).
          chrome.themeVariant is "Dark" or "Light" and must match how dark the palette is; it styles dialogs.
          uppercaseNames gives a DOS look; showIcons draws small folder/file glyphs.
        """;
}
