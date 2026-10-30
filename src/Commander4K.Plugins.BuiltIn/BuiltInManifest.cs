using System.Reflection;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn;

/// <summary>Manifests of the plugins compiled into the app; they declare permissions like external plugins do.</summary>
internal static class BuiltInManifest
{
    private static readonly string Version =
        typeof(BuiltInManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static PluginManifest Create(string id, string name, string description, params string[] permissions) =>
        new(id, name, Version, "4Kprojekt", description, permissions);
}
