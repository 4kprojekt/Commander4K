using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Mcp;

/// <summary>
/// Hands a prompt to Claude Code: shows it and copies it to the clipboard when the dialog opens, so the user can paste
/// it into a Claude Code session connected to Commander4K through MCP. Shows the MCP server's status and opens the
/// connect window for a session that is not connected yet.
/// </summary>
public sealed class UseClaudeCodeDialog : DialogWindow<bool>
{
    private readonly string _prompt;

    public UseClaudeCodeDialog(SkinBrushes brushes, string prompt, string mcpStatus, Action connect) : base(Loc.T("Użyj Claude Code"), brushes, 640)
    {
        _prompt = prompt;
        var text = new SelectableTextBlock().Text(prompt).TextWrapping(TextWrapping.Wrap)
            .FontFamily(brushes.MonospaceFamily);
        SetBody(
            Label(Loc.T("Wklej to w Claude Code połączonym z Commander4K przez MCP")),
            new ScrollViewer().MaxHeight(360).Content(text),
            new TextBlock().Text(mcpStatus).TextWrapping(TextWrapping.Wrap).Foreground(brushes.MutedForeground).Margin(0, 6, 0, 0),
            ButtonRow(
                (Loc.T("Połącz Claude Code"), () =>
                {
                    Complete(true);
                    connect();
                }, false),
                (Loc.T("Kopiuj"), () => _ = CopyAsync(), false),
                ("OK", () => Complete(true), true)));
        Opened += (_, _) => _ = CopyAsync();
    }

    private async Task CopyAsync()
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_prompt);
        }
    }
}
