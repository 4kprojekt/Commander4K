using System.Text;
using Commander4K.App.Composition;

namespace Commander4K.App.Mcp;

/// <summary>
/// The server instructions an MCP client receives at <c>initialize</c>: what Commander4K is and which plugins are
/// usable right now, each with the tool call that uses it, so the client reaches for an installed plugin without
/// being told. Built per session from the plugins loaded at that moment; English, because it is for the model.
/// </summary>
internal static class McpPluginKnowledge
{
    public static string Instructions(AppHost host)
    {
        var text = new StringBuilder()
            .AppendLine("Commander4K is a dual-pane file manager for Windows where everything is a folder: a folder's .c4k files decide whether a panel "
                + "shows its files, a gallery, a mailbox, a dashboard or a plugin panel. Tools act on the user's panels and files; some calls ask "
                + "the user first. Start with get_context; get_plugin_guide explains panels, .c4k files and the plugin protocol.")
            .AppendLine("Use the installed plugins below when they fit the task instead of doing the same work by hand: the user already approved "
                + "their permissions. Commander4K has no AI of its own; you are the only model here.")
            .AppendLine()
            .AppendLine("Built-in panels (load_plugin_panel plugin=\"<id>\", set_panel_mode mode=\"panel:<id>\", dashboard card \"<id>\"):");
        foreach (var panel in host.BuiltInPanels)
        {
            text.AppendLine($"- {panel.Id}: {panel.Title}. {panel.Description}");
        }

        text.AppendLine().AppendLine("Installed plugins (enabled):");
        if (host.ExternalPlugins.Count == 0)
        {
            text.AppendLine("- none. The official catalog is in the plugin manager; install_plugin installs one you wrote.");
        }

        foreach (var plugin in host.ExternalPlugins)
        {
            var m = plugin.Manifest;
            text.AppendLine($"- {m.Id} {m.Version} — {m.Name}: {m.Description}");
            foreach (var command in m.Contributes?.Commands ?? [])
            {
                text.AppendLine($"  - command \"{command.Title}\": run_plugin_command plugin=\"{m.Id}\" command=\"{command.Id}\"");
            }

            foreach (var panel in m.Contributes?.Panels ?? [])
            {
                var id = $"{m.Id}/{panel.Id}";
                text.Append($"  - panel \"{panel.Title}\": load_plugin_panel plugin=\"{id}\"")
                    .Append(panel.RequiresArgument ? " argument=<file>" : string.Empty)
                    .Append(panel.Accepts ? "; receives files (F5 from the other panel)" : string.Empty)
                    .Append(panel.Provides ? "; provides files (F5 from it)" : string.Empty)
                    .Append(panel.Card ? $"; dashboard card \"{id}\"" : string.Empty)
                    .AppendLine();
            }

            foreach (var preview in m.Contributes?.Previews ?? [])
            {
                var id = $"{m.Id}/{preview.Id}";
                var folders = preview.Extensions.Contains("/");
                var files = string.Join(" ", preview.Extensions.Where(e => e != "/"));
                text.Append($"  - preview \"{preview.Title}\": render_preview path=<{(folders && files.Length == 0 ? "folder" : "file")}> plugin=\"{id}\"")
                    .Append(files.Length > 0 ? $" for {files}" : string.Empty)
                    .Append(folders ? $"; a folder shows it with a .c4k file {{\"plugin\": \"{m.Id}\"}}" : string.Empty)
                    .Append(preview.Card ? $"; dashboard card \"{id}\"" : string.Empty)
                    .AppendLine();
            }
        }

        return text.AppendLine().Append("This list is from the moment you connected; list_plugins shows the current state.").ToString();
    }
}
