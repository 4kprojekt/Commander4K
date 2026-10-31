using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;
using Commander4K.Core.Theming;

namespace Commander4K.App.Mcp;

/// <summary>
/// <c>save_skin</c>: the one configuration tool that needs the window. The skin is shown live while the user decides
/// (the dialog itself is drawn with it), so the confirmation is the preview; "No", Esc or a cancelled call put the
/// previous skin back. Saving works like <see cref="AiSkinDialog"/>: a built-in id gets <c>-ai</c>, the file is
/// <c>config/skins/&lt;id&gt;.json</c>.
/// </summary>
public static class McpSkinTools
{
    public static IReadOnlyList<McpTool> Create(MainWindow window, AppHost host, Func<SkinManager> skins) =>
    [
        new McpTool(
            new McpToolDefinition(
                "save_skin",
                "Saves a skin (JSON as in get_skin_guide) into the user's skins and, unless apply is false, switches to it. "
                + "The user sees the skin live and accepts or declines it. A built-in skin's id gets \"-ai\" appended; "
                + "the same id as one of the user's skins replaces it.",
                McpSchema.Object(
                    ("json", "string", "The complete skin JSON (check it with check_skin first).", true),
                    ("apply", "boolean", "Switch to the skin after saving (default true).", false)),
                McpToolMode.Ask,
                AlwaysAsk: true,
                SelfConfirming: true),
            (call, ct) => SaveAsync(window, host, skins, call, ct),
            args => "save_skin " + SkinName(args["json"]?.ToString())),
    ];

    private static async Task<McpToolResult> SaveAsync(MainWindow window, AppHost host, Func<SkinManager> skins, McpCall call, CancellationToken ct)
    {
        var (parsed, warnings, error) = McpConfigTools.ParseSkin(call.Required("json"));
        if (parsed is null)
        {
            return McpToolResult.Error(error!);
        }

        var apply = call.Optional("apply") is not { } flag || !bool.TryParse(flag, out var value) || value;
        var skin = BuiltInSkins.All.Any(b => string.Equals(b.Id, parsed.Id, StringComparison.OrdinalIgnoreCase))
            ? parsed with { Id = parsed.Id + "-ai" }
            : parsed;
        var replaces = host.Skins.Skins.Any(s => string.Equals(s.Id, skin.Id, StringComparison.OrdinalIgnoreCase));

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var manager = skins();
            var original = manager.Current.Skin;
            if (ct.IsCancellationRequested)
            {
                return McpToolResult.Error("The call was cancelled.");
            }

            manager.Preview(skin);
            var message = Loc.T("Klient MCP „{0}” proponuje skórkę „{1}” ({2}). Widzisz ją teraz w oknie.", call.ClientName, skin.DisplayName, skin.Id)
                + "\n\n" + (replaces ? Loc.T("Zastąpi Twoją skórkę o tym samym id.") : Loc.T("Zostanie zapisana jako config/skins/{0}.json.", skin.Id));
            if (warnings.Count > 0)
            {
                message += "\n\n" + Loc.T("Uwagi:") + "\n• " + string.Join("\n• ", warnings);
            }

            var dialog = new ConfirmDialog(manager.Current, Loc.T("AI chce: {0}", McpConfirmDialog.ToolName("save_skin")), message, Loc.T("Zapisz skórkę"), cancelIsDefault: true);
            bool accepted;
            using (ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close())))
            {
                accepted = await dialog.ShowAsync(window) && !ct.IsCancellationRequested;
            }

            if (!accepted)
            {
                manager.Preview(original);
                return McpToolResult.Error("The user declined the skin; the previous skin is back.");
            }

            try
            {
                await SkinCatalog.ExportAsync(skin, Path.Combine(host.Config.SkinsDirectory, skin.Id + ".json"), CancellationToken.None);
                await host.ReloadSkinsAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                manager.Preview(original);
                return McpToolResult.Error($"Saving the skin failed: {ex.Message}");
            }

            if (apply)
            {
                manager.Apply(host.Skins.Get(skin.Id));
            }
            else
            {
                manager.Preview(original);
            }

            return new McpToolResult($"Saved skin {skin.Id} ({skin.DisplayName}){(apply ? " and switched to it" : string.Empty)}."
                + (warnings.Count > 0 ? $" It still has {warnings.Count} contrast warnings." : string.Empty));
        });
    }

    private static string SkinName(string? json)
    {
        var (skin, _, _) = McpConfigTools.ParseSkin(json ?? string.Empty);
        return skin is null ? "(invalid JSON)" : $"{skin.Id} ({skin.DisplayName})";
    }
}
