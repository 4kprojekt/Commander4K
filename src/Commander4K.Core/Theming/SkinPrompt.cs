using System.Text;
using System.Text.Json;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Theming;

/// <summary>
/// Instructions for designing a skin with Claude, used both by the in-app generator and as the text the
/// "copy prompt" button puts on the clipboard for claude.ai or Claude Design.
/// </summary>
public static class SkinPrompt
{
    /// <summary>Stable part of the prompt (system prompt for the API call).</summary>
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

    /// <summary>Full prompt to paste into claude.ai or Claude Design.</summary>
    /// <param name="baseSkin">Skin to start from (the skin editor's JSON); <c>null</c> = a new design with an example.</param>
    public static string ForChat(string? description, Skin? baseSkin = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(DesignBrief);
        builder.AppendLine();
        builder.AppendLine("The skin JSON must validate against this JSON Schema (draft 2020-12):");
        builder.AppendLine("```json");
        builder.AppendLine(SkinSchema.CreateJson());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("""
            Hard requirements (Commander4K refuses the file otherwise):
            - One JSON object, no comments, no trailing commas; property names exactly as in the schema (camelCase).
            - Every property the schema lists under "required" is present; colours match the "pattern" (#RRGGBB or #AARRGGBB).
            - id is lower-case letters, digits and hyphens (max 64 characters) and is also the file name: <id>.json.
            - Optional palette tokens (windowForeground, mutedForeground, surface2, hover/pressed backgrounds, focusRing,
              activeHeader*, selectedBackground, warning/success, the *Background tints, codeBackground) may be omitted;
              they are then derived from the required colours.
            """);
        if (baseSkin is null)
        {
            builder.AppendLine("Example (the built-in Norton Commander skin):");
        }
        else
        {
            builder.AppendLine("Start from this skin and keep what the request does not ask to change; give the result a new id and displayName:");
        }

        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(baseSkin ?? BuiltInSkins.NortonCommander, ConfigJsonContext.Default.Skin));
        builder.AppendLine("```");
        builder.AppendLine();
        var task = baseSkin is null ? "Design a new skin" : "Design a skin based on the one above";
        builder.AppendLine(string.IsNullOrWhiteSpace(description)
            ? task + ". Describe the idea in one sentence, then give the complete JSON in a single ```json code block."
            : $"{task} matching this description: \"{description.Trim()}\". Give the complete JSON in a single ```json code block.");
        builder.AppendLine("If you can produce files (for example in Claude Design), also offer it as a downloadable <id>.json file.");
        builder.AppendLine("The user will paste it into Commander4K's skin editor, which validates it, or save it into the config/skins folder.");
        return builder.ToString();
    }

    /// <summary>User message for the in-app generator; the brief goes into the system prompt.</summary>
    public static string ForApi(string description) =>
        $"Design a Commander4K skin matching this description: \"{description.Trim()}\". Return only the skin object.";
}
