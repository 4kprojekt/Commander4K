namespace Commander4K.Core.Tests;

/// <summary>Wiring in the window that has no test project of its own; a lost line here breaks every plugin quietly.</summary>
public sealed class AppWiringTests
{
    [Fact]
    public void Main_window_connects_the_plugin_ui()
    {
        // Without it ui.prompt / ui.confirm answer null at once (a plugin's setup reads as "cancelled"), notifications
        // vanish and context.get has no panels.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Commander4K.App", "MainWindow.cs"));
        Assert.Contains("host.PluginUi.Target = _pluginHost;", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Commander4K.sln")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return root;
    }
}
