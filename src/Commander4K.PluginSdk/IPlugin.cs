namespace Commander4K.PluginSdk;

/// <summary>
/// Common base of every Commander4K extension. Each plugin carries a <see cref="PluginManifest"/>; its
/// <see cref="PluginManifest.Id"/> must equal <see cref="Id"/>. Side effects (file writes, killing processes, running
/// programs, network) must go through the <see cref="IPluginHost"/> the plugin is given, which enforces the manifest's
/// permissions and records every call in the audit log.
/// </summary>
public interface IPlugin
{
    string Id { get; }

    string DisplayName { get; }

    PluginManifest Manifest { get; }
}
