using System.Net;
using System.Net.Sockets;
using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Updates;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

/// <summary>Runs the example plugins from examples/plugins through the real protocol with the real runtimes.</summary>
public sealed class ExternalPluginTests : IAsyncLifetime
{
    private static readonly string Examples = Path.Combine(RepositoryRoot(), "examples", "plugins");

    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-ext-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly FakeUi _ui = new();
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private IReadOnlyList<RuntimeInfo> _runtimes = [];

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _runtimes = await RuntimeCatalog.DetectAsync(TestToken);
        File.WriteAllBytes(Path.Combine(_folder, "a.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_folder, "b.txt"), new byte[50]);
        File.WriteAllBytes(Path.Combine(_folder, "c.log"), new byte[10]);
        _ui.ActivePath = _folder;
    }

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Python_plugin_reads_through_host_and_is_denied_writes()
    {
        await using var plugin = Start("folder-stats-python");
        var command = new ExternalCommandPlugin(plugin);
        var host = HostFor(plugin);

        await command.ExecuteAsync("summary", host, TestToken);
        Assert.Contains(".txt", _ui.LastText, StringComparison.Ordinal);
        Assert.Contains("2 plików", _ui.LastText, StringComparison.Ordinal);

        await command.ExecuteAsync("try-write", host, TestToken);
        Assert.Contains("Zablokowano", _ui.LastNotification, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "c4k-test.txt")));
        Assert.Contains(_audit.Recent("examples.folder-stats"), e => e.Operation == "fs.write" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(_audit.Recent("examples.folder-stats"), e => e.Operation == "fs.list" && e.Outcome == AuditOutcome.Allowed);

        var panel = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels![0]);
        var session = panel.CreateSession(new PanelRequest(null, null), host);
        var snapshot = await session.LoadAsync(new PanelRequest(null, null), null, TestToken);
        Assert.Equal(["Rozszerzenie", "Pliki", "Rozmiar"], snapshot.Columns.Select(c => c.Header));
        Assert.Equal(".txt", snapshot.Rows[0].Id);
        var details = await session.GetDetailsAsync(".txt", TestToken);
        Assert.Equal("a.txt", details!.Sections[0].Items[0].Key);
    }

    [Fact]
    public async Task Python_plugin_renders_csv_quick_view_as_markdown()
    {
        await using var plugin = Start("folder-stats-python");
        var csvPath = Path.Combine(_folder, "dane.csv");
        await File.WriteAllTextAsync(csvPath, "miasto;ludność\nKraków;800000\nGdańsk;470000\n", TestToken);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("dane.csv", csvPath, VfsEntryKind.File, 40);

        Assert.True(preview.CanPreview(entry));
        Assert.False(preview.CanPreview(entry with { Name = "x.txt" }));
        var result = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, HostFor(plugin), null, TestToken));

        Assert.Contains("| miasto | ludność |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Gdańsk | 470000 |", result.Markdown, StringComparison.Ordinal);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public async Task Node_plugin_uses_net_permission_against_local_server()
    {
        await using var plugin = Start("site-check-node");
        using var server = new LocalHttpServer("<html><title>Witaj C4K</title></html>");
        _ui.PromptAnswer = server.Url;

        await new ExternalCommandPlugin(plugin).ExecuteAsync("check-url", HostFor(plugin), TestToken);

        Assert.Contains("Tytuł: Witaj C4K", _ui.LastText, StringComparison.Ordinal);
        Assert.Contains(_audit.Recent("examples.site-check"), e => e.Operation == "net.get" && e.Detail.StartsWith(server.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PowerShell_plugin_runs_git_through_host()
    {
        Assert.SkipWhen(!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d => File.Exists(Path.Combine(d.Trim(), "git.exe"))), "git is not installed");
        _ui.ActivePath = RepositoryRoot();
        await using var plugin = Start("git-powershell");
        var panel = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels![0]);

        var snapshot = await panel.CreateSession(new PanelRequest(null, null), HostFor(plugin)).LoadAsync(new PanelRequest(null, null), null, TestToken);

        Assert.NotEmpty(snapshot.Rows);
        Assert.Equal(4, snapshot.Rows[0].Cells.Count);
        Assert.Contains(_audit.Recent("examples.git"), e => e.Operation == "shell.run" && e.Detail.StartsWith("git ", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_validation_rejects_traversal_unknown_permissions_and_reserved_ids()
    {
        var folder = Path.Combine(_folder, "bad");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "builtin.evil", "name": "x", "version": "1.0", "author": "a", "description": "d",
              "runtime": { "type": "python", "entry": "..\\..\\outside.py" },
              "permissions": ["fs.read", "root.everything"], "contributes": {} }
            """);

        var discovered = ManifestReader.Read(folder);

        Assert.False(discovered.IsValid);
        Assert.Contains(discovered.Errors, e => e.Contains("zarezerwowany", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("wewnątrz folderu", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("root.everything", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("nic nie dodaje", StringComparison.Ordinal));
        Assert.All(ManifestReader.Discover([Examples]), example => Assert.True(example.IsValid, string.Join("; ", example.Errors)));
    }

    [Theory]
    [InlineData("folder-stats-python", "python", "c4k.py")]
    [InlineData("site-check-node", "node", "c4k.js")]
    [InlineData("git-powershell", "powershell", "c4k.ps1")]
    public void Example_sdk_clients_match_plugins_sdk(string example, string language, string file) =>
        Assert.Equal(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins-sdk", language, file)),
            File.ReadAllText(Path.Combine(Examples, example, file)));

    [Theory]
    [InlineData("3.12.10", ">=3.9", true)]
    [InlineData("3.8.0", ">=3.9", false)]
    [InlineData("24.20.0", ">=18 <25", true)]
    [InlineData("25.0.0", ">=18 <25", false)]
    [InlineData("7.6.7", "^7", true)]
    [InlineData("8.0.0", "^7", false)]
    [InlineData("5.1.0", "7.2", false)]
    public void Runtime_version_ranges(string version, string range, bool expected) =>
        Assert.Equal(expected, RuntimeCatalog.Satisfies(SemanticVersion.Parse(version), range));

    [Fact]
    public void Approvals_require_new_consent_when_permissions_grow()
    {
        var store = new PluginApprovalStore(Path.Combine(_folder, "approvals.json"));
        var manifest = new PluginManifest("acme.tool", "Tool", "1.0.0", "a", "d", [PluginPermissions.FileRead]);
        Assert.False(store.IsApproved(manifest));

        store.Approve(manifest);
        Assert.True(new PluginApprovalStore(Path.Combine(_folder, "approvals.json")).IsApproved(manifest));
        Assert.False(store.IsApproved(manifest with { Version = "1.1.0", Permissions = [PluginPermissions.FileRead, PluginPermissions.ShellRun] }));

        store.Disable(manifest.Id);
        Assert.False(store.IsApproved(manifest));
    }

    private ExternalPlugin Start(string example)
    {
        var discovered = ManifestReader.Read(Path.Combine(Examples, example));
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var runtime = _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type);
        return new ExternalPlugin(discovered, runtime, HostFor(discovered.Manifest), _audit);
    }

    private ScopedPluginHost HostFor(ExternalPlugin plugin) => HostFor(plugin.Manifest);

    private ScopedPluginHost HostFor(PluginManifest manifest) => new(manifest, _ui, _router, _http, _audit);

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }

    private sealed class FakeUi : IPluginUi
    {
        public string? ActivePath { get; set; }

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public string LastText { get; private set; } = string.Empty;

        public string LastNotification { get; private set; } = string.Empty;

        public string? PromptAnswer { get; set; }

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError) => LastNotification = message;

        public Task ShowTextAsync(string title, string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult(PromptAnswer);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;
    }

    private sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LocalHttpServer(string body)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
