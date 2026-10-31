using Avalonia.Controls;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;

namespace Commander4K.App.Mcp;

/// <summary>
/// Asks before an MCP tool runs: which client, what exactly (the dispatcher's summary) and for how long the answer
/// holds. Esc, closing the window and Enter all mean "No" — the dialog appears while the user may be typing.
/// </summary>
public sealed class McpConfirmDialog : DialogWindow<McpConsent>
{
    public const int MaxSummaryLength = 2000;

    public McpConfirmDialog(SkinBrushes brushes, McpConfirmation confirmation)
        : base(Loc.T("AI chce: {0}", ToolName(confirmation.Tool)), brushes, 560)
    {
        var summary = confirmation.Summary.Length > MaxSummaryLength
            ? confirmation.Summary[..MaxSummaryLength] + "…"
            : confirmation.Summary;
        var details = new SelectableTextBlock().Text(summary).TextWrapping(TextWrapping.Wrap)
            .FontFamily(brushes.MonospaceFamily);
        // "Nie" is the default button: an Enter meant for the panel must not approve anything.
        var buttons = new List<(string, Action, bool)>
        {
            (Loc.T("Tak"), () => Complete(McpConsent.Yes), false),
            (Loc.T("Nie"), () => Complete(McpConsent.No), true),
        };
        if (!confirmation.AlwaysAsk)
        {
            buttons.Add((Loc.T("Tak do końca sesji"), () => Complete(McpConsent.YesForSession), false));
        }

        var row = ButtonRow([.. buttons]);
        SetBody(
            Label(Loc.T("Klient MCP: {0} (nazwę podaje sam klient)", confirmation.ClientName)),
            Label(Loc.T("Narzędzie: {0}", confirmation.Tool)),
            new ScrollViewer().MaxHeight(260).Content(details),
            row);

        // Focus starts on "Nie": keys typed for the panel (Space, Enter, Tab) must not land on "Tak".
        NoButton = row.Children.OfType<Button>().ElementAt(1);
        Opened += (sender, args) => NoButton.Focus();
    }

    /// <summary>The "Nie" button, focused when the dialog opens.</summary>
    public Button NoButton { get; }

    /// <summary>The tool's name as an action ("AI chce: odczytać plik"); unknown tools keep their MCP name.</summary>
    public static string ToolName(string tool) => tool switch
    {
        "list_directory" => Loc.T("wyświetlić zawartość folderu"),
        "stat" => Loc.T("sprawdzić plik lub folder"),
        "read_file" => Loc.T("odczytać plik"),
        "write_file" => Loc.T("zapisać plik"),
        "make_directory" => Loc.T("utworzyć folder"),
        "move" => Loc.T("przenieść lub zmienić nazwę"),
        "delete" => Loc.T("usunąć"),
        "check_plugin" => Loc.T("sprawdzić wtyczkę"),
        "get_plugin_log" => Loc.T("odczytać dziennik wtyczki"),
        "get_context" => Loc.T("odczytać stan paneli"),
        "list_commands" => Loc.T("wyświetlić listę komend"),
        "get_plugin_guide" => Loc.T("odczytać przewodnik wtyczek"),
        "list_plugins" => Loc.T("wyświetlić listę wtyczek"),
        "navigate" => Loc.T("otworzyć folder w panelu"),
        "select" => Loc.T("zaznaczyć pliki w panelu"),
        "set_panel_mode" => Loc.T("zmienić tryb panelu"),
        "run_command" => Loc.T("uruchomić komendę"),
        "run_plugin_command" => Loc.T("uruchomić komendę wtyczki"),
        "load_plugin_panel" => Loc.T("otworzyć panel wtyczki"),
        "render_preview" => Loc.T("pokazać podgląd pliku"),
        "install_plugin" => Loc.T("zainstalować wtyczkę"),
        "enable_plugin" => Loc.T("włączyć wtyczkę"),
        "reload_plugin" => Loc.T("przeładować wtyczkę"),
        _ => tool,
    };
}
