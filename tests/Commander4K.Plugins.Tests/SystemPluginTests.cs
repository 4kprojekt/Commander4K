using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.PowerShell;
using Commander4K.Plugins.BuiltIn.TaskManager;

namespace Commander4K.Plugins.Tests;

public sealed class SystemPluginTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("c4k-sys-").FullName;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task PowerShell_literals_cannot_inject_code()
    {
        var hostile = "x'; Remove-Item C:\\ -Recurse; '";
        var quoted = PowerShellCatalogPlugin.Quote(hostile);

        var output = PowerShellCatalogPlugin.FormatResult(await Commander4K.Core.Execution.ProcessRunner.RunAsync(
            PowerShellCatalogPlugin.BuildRequest(PowerShellCatalogPlugin.FindShell(), $"Write-Output {quoted}; 'zażółć'", null), TestToken));

        Assert.Contains(hostile, output, StringComparison.Ordinal);
        Assert.Contains("zażółć", output, StringComparison.Ordinal);
        Assert.Contains("[kod wyjścia 0]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_is_created_with_defaults_and_exposed_as_commands()
    {
        var plugin = new PowerShellCatalogPlugin(Path.Combine(_directory, "powershell.json"));
        var changed = false;
        plugin.CommandsChanged += () => changed = true;

        await plugin.ReloadAsync(TestToken);

        Assert.True(changed);
        Assert.True(File.Exists(plugin.CatalogPath));
        Assert.Contains(plugin.Commands, c => c.Id == "net-listen" && c.Category == "PowerShell/Sieć");
        Assert.Equal(plugin.Commands.Count, plugin.Commands.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Script_files_are_commands_and_get_the_selected_files_as_arguments()
    {
        var plugin = new PowerShellCatalogPlugin(Path.Combine(_directory, "powershell.json"));
        await plugin.ReloadAsync(TestToken);

        // The folder is created with an example, and every .ps1 in it (subfolder = submenu) is a command.
        Assert.Contains(plugin.Commands, c => c.Id.StartsWith(PowerShellCatalogPlugin.ScriptPrefix, StringComparison.Ordinal) && c.Category == "PowerShell/Skrypty");
        Directory.CreateDirectory(Path.Combine(plugin.ScriptsFolder, "Moje"));
        var script = Path.Combine(plugin.ScriptsFolder, "Moje", "licz 'to'.ps1");
        File.WriteAllText(script, "\"$($args.Count)|$($args -join '|')\"");
        await plugin.ReloadAsync(TestToken);
        Assert.Contains(plugin.Commands, c => c.Id == PowerShellCatalogPlugin.ScriptPrefix + Path.Combine("Moje", "licz 'to'.ps1") && c.Category == "PowerShell/Skrypty/Moje");

        var line = PowerShellCatalogPlugin.ScriptCommandLine(script, [@"C:\a b\x.txt", @"C:\it's; calc.exe"]);
        var result = await Commander4K.Core.Execution.ProcessRunner.RunAsync(
            PowerShellCatalogPlugin.BuildRequest(PowerShellCatalogPlugin.FindShell(), line, null), TestToken);

        Assert.Equal(@"2|C:\a b\x.txt|C:\it's; calc.exe", result.Output.Trim());
    }

    [Fact]
    public async Task Task_manager_builds_a_process_tree_including_this_process()
    {
        var session = new TaskManagerPanelPlugin().CreateSession(new PanelRequest(null, null), null!);

        var snapshot = await session.LoadAsync(new PanelRequest(null, null), null, TestToken);
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var row = Assert.Single(snapshot.Rows, r => r.Id == self);
        Assert.NotNull(row.ParentId);
        Assert.Contains(snapshot.Rows, r => r.Id == row.ParentId);
        var details = await session.GetDetailsAsync(self, TestToken);
        Assert.Contains(details!.Sections, s => s.Title.StartsWith("Biblioteki", StringComparison.Ordinal) && s.Items.Count > 5);
    }
}
