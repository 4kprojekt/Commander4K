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
/// it into a Claude Code session connected to Commander4K through MCP.
/// </summary>
public sealed class UseClaudeCodeDialog : DialogWindow<bool>
{
    private readonly string _prompt;

    public UseClaudeCodeDialog(SkinBrushes brushes, string prompt) : base(Loc.T("Użyj Claude Code"), brushes, 640)
    {
        _prompt = prompt;
        var text = new SelectableTextBlock().Text(prompt).TextWrapping(TextWrapping.Wrap)
            .FontFamily(FontFamily.Parse("Cascadia Mono, Consolas"));
        SetBody(
            Label(Loc.T("Wklej to w Claude Code połączonym z Commander4K przez MCP")),
            new ScrollViewer().MaxHeight(360).Content(text),
            ButtonRow((Loc.T("Kopiuj"), () => _ = CopyAsync(), false), ("OK", () => Complete(true), true)));
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
