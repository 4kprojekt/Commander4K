using Commander4K.Core.Tools;

namespace Commander4K.Core.Tests;

public sealed class ExternalToolsServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-tools-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Files_token_becomes_one_argument_per_file()
    {
        Assert.SkipWhen(ExternalToolsService.ResolveExecutable("pwsh") is null, "PowerShell 7 is not installed");
        var script = Path.Combine(_folder, "record.ps1");
        var output = Path.Combine(_folder, "args.txt");
        await File.WriteAllTextAsync(script, $"$args | Set-Content -LiteralPath '{output}' -Encoding utf8", TestContext.Current.CancellationToken);
        var service = new ExternalToolsService(new ExternalToolsConfig
        {
            Tools = { [ExternalToolsService.VsCodeTool] = [new("pwsh", ["-NoProfile", "-File", script, "{dir}", "{files}"])] },
        });

        var result = await service.OpenInVsCodeAsync(@"C:\Projekt z spacją", [@"C:\Projekt z spacją\a b.txt", @"C:\Projekt z spacją\c.cs"]);

        Assert.True(result.Launched, result.Error);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(output) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal([@"C:\Projekt z spacją", @"C:\Projekt z spacją\a b.txt", @"C:\Projekt z spacją\c.cs"],
            (await File.ReadAllLinesAsync(output, TestContext.Current.CancellationToken)).Where(l => l.Length > 0));
    }

    [Fact]
    public async Task Window_placement_is_missing_on_first_run_and_read_back_after_saving()
    {
        var config = new Configuration.ConfigStore(_folder);
        var path = Path.Combine(_folder, Configuration.ConfigStore.SettingsFileName);
        Assert.Null(Configuration.AppSettings.ReadWindowPlacement(path)); // No file yet: first run → maximized.

        var settings = await config.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Null(Configuration.AppSettings.ReadWindowPlacement(path)); // Defaults written, still no placement.

        settings.Window = new Configuration.WindowPlacement { Maximized = false, X = 100, Y = 80, Width = 1200, Height = 700 };
        await config.SaveSettingsAsync(settings, TestContext.Current.CancellationToken);
        var read = Configuration.AppSettings.ReadWindowPlacement(path)!;

        Assert.Equal((false, 100, 80, 1200.0, 700.0), (read.Maximized, read.X, read.Y, read.Width, read.Height));
        File.WriteAllText(path, "{ broken");
        Assert.Null(Configuration.AppSettings.ReadWindowPlacement(path));
    }

    [Fact]
    public void Tools_missing_from_an_older_tools_json_fall_back_to_defaults()
    {
        // Resolution only: launching would open VS Code on the developer's desktop.
        var older = new ExternalToolsService(new ExternalToolsConfig()); // Written before "vscode" existed.
        var custom = new ExternalToolsService(new ExternalToolsConfig { Tools = { [ExternalToolsService.VsCodeTool] = [new("my-editor", ["{dir}"])] } });

        Assert.Contains(older.CandidatesFor(ExternalToolsService.VsCodeTool)!, c => c.Executable.EndsWith("Code.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("my-editor", Assert.Single(custom.CandidatesFor(ExternalToolsService.VsCodeTool)!).Executable);
        Assert.Null(older.CandidatesFor("no-such-tool"));
    }
}
