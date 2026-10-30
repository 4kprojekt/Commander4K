using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Logs;
using Commander4K.Plugins.BuiltIn.PowerShell;
using Commander4K.Plugins.BuiltIn.TaskManager;

namespace Commander4K.Plugins.Tests;

public sealed class SystemPluginTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("c4k-sys-").FullName;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("2026-10-30 08:00:01.123 [ERROR] boom", LogLevel.Error)]
    [InlineData("2026-10-30T08:00:01 WARN slow", LogLevel.Warning)]
    [InlineData("fatal: disk gone", LogLevel.Fatal)]
    [InlineData("Unhandled System.IO.IOException at start", LogLevel.Error)]
    [InlineData("plain text", LogLevel.Unknown)]
    public void Detects_levels(string line, LogLevel expected) => Assert.Equal(expected, LogAnalyzer.DetectLevel(line));

    [Fact]
    public void Templates_mask_variable_parts()
    {
        var a = LogAnalyzer.Template("2026-10-30 08:00:01 [ERROR] Order 3f2504e0-4f89-11d3-9a0c-0305e82c3301 failed after 1200 ms from 10.0.0.5:443");
        var b = LogAnalyzer.Template("2026-10-30 09:12:44 [ERROR] Order 7c9e6679-7425-40de-944b-e07fc1f90ae7 failed after 87 ms from 192.168.1.20:8080");

        Assert.Equal(a, b);
        Assert.Equal("Order <*> failed after <*> ms from <ip>", a);
    }

    [Fact]
    public async Task Analysis_groups_counts_and_attaches_stack_traces()
    {
        var log = Path.Combine(_directory, "app.log");
        await File.WriteAllLinesAsync(log,
        [
            "2026-10-30 08:00:00 [INFO] started",
            "2026-10-30 08:00:01 [ERROR] Connection 1 failed",
            "   at Db.Open()",
            "2026-10-30 09:30:00 [ERROR] Connection 2 failed",
            "2026-10-30 09:31:00 [WARN] slow request 120 ms",
        ], TestToken);

        var analysis = await LogAnalyzer.AnalyzeAsync(log, null, TestToken);

        var error = Assert.Single(analysis.Patterns, p => p.Level == LogLevel.Error);
        Assert.Equal(2, error.Count);
        Assert.Contains("at Db.Open()", error.Samples[0].Text, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 30, 9, 30, 0), error.Last);
        Assert.Equal(2, analysis.Hourly.Count);
        Assert.Equal(1, analysis.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task PowerShell_literals_cannot_inject_code()
    {
        var hostile = "x'; Remove-Item C:\\ -Recurse; '";
        var quoted = PowerShellCatalogPlugin.Quote(hostile);

        var output = await PowerShellCatalogPlugin.CaptureAsync(PowerShellCatalogPlugin.FindShell(), $"Write-Output {quoted}; 'zażółć'", null, TestToken);

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
