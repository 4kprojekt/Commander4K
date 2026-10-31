using Avalonia.Controls;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;

namespace Commander4K.App.Ai;

/// <param name="FixRequest">A follow-up for the assistant when the user chose "ask the AI to fix it".</param>
/// <param name="Installed">The plugin was written to disk (disabled until approved).</param>
public sealed record AiInstallResult(string Message, string? FixRequest = null, bool Installed = false);

/// <summary>
/// Installs a plugin written by an AI assistant: validates the files off the UI thread, checks that the code works
/// only through the SDK (<see cref="PluginSourceCheck"/>; otherwise offers to send the AI a fix request), shows what
/// will be installed (name, runtime, permissions, files) and asks before writing or replacing anything. The plugin
/// lands disabled; the user enables it in the plugin manager after reviewing its permissions, like any other plugin.
/// </summary>
public static class AiPluginInstaller
{
    /// <param name="fromCatalog">The files come from the official catalog (checked against its hashes), not from an AI answer.</param>
    /// <param name="limits">File limits for validation; <c>null</c> = <see cref="PluginLimits.Answer"/>.</param>
    /// <param name="forMcp">Called by an MCP client: no source-check choice (findings come back as the fix request,
    /// nothing is installed), no plugin manager afterwards, and English messages for the model. The confirmation with
    /// the permissions stays — it is the user's consent.</param>
    public static async Task<AiInstallResult> InstallAsync(Window owner, SkinBrushes brushes, AiAppContext context, IReadOnlyList<GeneratedFile> files, Func<Task> openPluginManager,
        bool fromCatalog = false, PluginLimits? limits = null, bool forMcp = false)
    {
        PluginDraft draft;
        IReadOnlyList<SourceFinding> findings;
        try
        {
            (draft, findings) = await Task.Run(() =>
            {
                var prepared = context.PrepareInstall(files, limits);
                return (prepared, PluginSourceCheck.Scan(prepared.Files));
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (forMcp)
            {
                return new AiInstallResult("The plugin was not installed: " + ex.Message);
            }

            await new MessageDialog(brushes, Loc.T("Nie można zainstalować wtyczki"), ex.Message).ShowAsync(owner);
            return new AiInstallResult(Loc.T("Wtyczka nie została zainstalowana: {0}", ex.Message),
                "The plugin could not be installed: " + ex.Message + "\nFix it and send all files again (`File: <path>` + one code block each).");
        }

        if (findings.Count > 0 && forMcp)
        {
            return new AiInstallResult("The plugin was not installed because its code bypasses the SDK.", PluginSourceCheck.FixRequest(findings));
        }

        if (findings.Count > 0)
        {
            var choice = await new ChoiceDialog(brushes, Loc.T("Wtyczka omija SDK"),
                Loc.T("Kod wtyczki korzysta z systemu bezpośrednio, a nie przez SDK Commander4K — aplikacja nie sprawdzi wtedy uprawnień ani nie zapisze tych operacji w dzienniku:") +
                "\n\n" + string.Join("\n", findings.Take(12).Select(f => "• " + f)) + (findings.Count > 12 ? "\n…" : string.Empty),
                fromCatalog ? [Loc.T("Zainstaluj mimo to"), Loc.T("Anuluj")] : [Loc.T("Poproś AI o poprawkę"), Loc.T("Zainstaluj mimo to"), Loc.T("Anuluj")]).ShowAsync(owner);
            if (fromCatalog)
            {
                choice = choice == 1 ? 2 : 3;
            }

            if (choice == 1)
            {
                return new AiInstallResult(Loc.T("Prośba o poprawkę jest gotowa ({0} miejsc omija SDK) — sprawdź ją i wyślij.", findings.Count), PluginSourceCheck.FixRequest(findings));
            }

            if (choice != 2)
            {
                return new AiInstallResult(Loc.T("Anulowano instalację wtyczki."));
            }
        }

        var manifest = draft.Manifest;
        var permissions = manifest.Permissions.Count == 0 ? Loc.T("bez uprawnień") : string.Join(", ", manifest.Permissions);
        var message = Loc.T("„{0}” ({1}, wersja {2}, {3})", manifest.Name, manifest.Id, manifest.Version, manifest.Runtime.Type) + "\n\n" +
            Loc.T("Uprawnienia: {0}", permissions) + "\n" +
            Loc.T("Pliki: {0}", string.Join(", ", draft.Files.Select(f => f.Path))) + "\n" +
            Loc.T("Folder: {0}", draft.TargetFolder) + "\n\n" +
            (draft.TargetExists ? Loc.T("Wtyczka o tym id już istnieje — jej folder zostanie zastąpiony. ") : string.Empty) +
            Loc.T("Wtyczka zostanie zapisana jako wyłączona; włączysz ją w menedżerze wtyczek po sprawdzeniu uprawnień.");
        var title = forMcp ? Loc.T("Zainstalować wtyczkę od klienta MCP?") : fromCatalog ? Loc.T("Zainstalować wtyczkę z katalogu?") : Loc.T("Zainstalować wtyczkę od AI?");
        if (!await new ConfirmDialog(brushes, title, message, draft.TargetExists ? Loc.T("Zastąp") : Loc.T("Zainstaluj")).ShowAsync(owner))
        {
            return new AiInstallResult(forMcp ? "The user declined the installation." : Loc.T("Anulowano instalację wtyczki."));
        }

        try
        {
            await Task.Run(() =>
            {
                // New code needs a fresh look even when the permissions did not change: never inherit an earlier consent.
                context.Host.Approvals.Disable(manifest.Id);
                PluginAuthoring.Install(draft, overwrite: true);
            });
            await context.Host.ReloadExternalPluginsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new AiInstallResult(forMcp ? "The plugin was not installed: " + ex.Message : Loc.T("Wtyczka nie została zainstalowana: {0}", ex.Message));
        }

        if (forMcp)
        {
            return new AiInstallResult($"Installed {manifest.Id} {manifest.Version} in {draft.TargetFolder}. It is disabled: call enable_plugin (the user reviews its permissions).", Installed: true);
        }

        await openPluginManager();
        return new AiInstallResult(Loc.T("Zainstalowano „{0}” w {1}. Włącz ją w menedżerze wtyczek (Ctrl+E) po sprawdzeniu uprawnień.", manifest.Name, draft.TargetFolder), Installed: true);
    }

    /// <summary>A message with several buttons; returns the 1-based number of the one clicked, 0 when closed (Esc).</summary>
    private sealed class ChoiceDialog : DialogWindow<int>
    {
        public ChoiceDialog(SkinBrushes brushes, string title, string message, IReadOnlyList<string> options) : base(title, brushes, 640)
        {
            SetBody(
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ButtonRow([.. options.Select((option, index) => (option, (Action)(() => Complete(index + 1)), index == 0))]));
        }
    }
}
