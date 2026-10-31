using Avalonia.Controls;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;

namespace Commander4K.App.Ai;

/// <param name="FixRequest">A follow-up for the assistant when the user chose "ask the AI to fix it".</param>
public sealed record AiInstallResult(string Message, string? FixRequest = null);

/// <summary>
/// Installs a plugin written by an AI assistant: validates the files off the UI thread, checks that the code works
/// only through the SDK (<see cref="PluginSourceCheck"/>; otherwise offers to send the AI a fix request), shows what
/// will be installed (name, runtime, permissions, files) and asks before writing or replacing anything. The plugin
/// lands disabled; the user enables it in the plugin manager after reviewing its permissions, like any other plugin.
/// </summary>
public static class AiPluginInstaller
{
    /// <param name="fromCatalog">The files come from the official catalog (checked against its hashes), not from an AI answer.</param>
    public static async Task<AiInstallResult> InstallAsync(Window owner, SkinBrushes brushes, AiAppContext context, IReadOnlyList<GeneratedFile> files, Func<Task> openPluginManager,
        bool fromCatalog = false)
    {
        PluginDraft draft;
        IReadOnlyList<SourceFinding> findings;
        try
        {
            (draft, findings) = await Task.Run(() =>
            {
                var prepared = context.PrepareInstall(files);
                return (prepared, PluginSourceCheck.Scan(prepared.Files));
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await new MessageDialog(brushes, Loc.T("Nie można zainstalować wtyczki"), ex.Message).ShowAsync(owner);
            return new AiInstallResult(Loc.T("Wtyczka nie została zainstalowana: {0}", ex.Message),
                "The plugin could not be installed: " + ex.Message + "\nFix it and send all files again (`File: <path>` + one code block each).");
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
        if (!await new ConfirmDialog(brushes, fromCatalog ? Loc.T("Zainstalować wtyczkę z katalogu?") : Loc.T("Zainstalować wtyczkę od AI?"), message, draft.TargetExists ? Loc.T("Zastąp") : Loc.T("Zainstaluj")).ShowAsync(owner))
        {
            return new AiInstallResult(Loc.T("Anulowano instalację wtyczki."));
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
            return new AiInstallResult(Loc.T("Wtyczka nie została zainstalowana: {0}", ex.Message));
        }

        await openPluginManager();
        return new AiInstallResult(Loc.T("Zainstalowano „{0}” w {1}. Włącz ją w menedżerze wtyczek (Ctrl+E) po sprawdzeniu uprawnień.", manifest.Name, draft.TargetFolder));
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
