namespace Commander4K.PluginSdk;

/// <summary>
/// Common base of every Commander4K extension. Implementations need a public parameterless constructor so the
/// plugin loader can instantiate them from an isolated <c>AssemblyLoadContext</c>.
/// </summary>
public interface IPlugin
{
    string Id { get; }

    string DisplayName { get; }
}
