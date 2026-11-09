using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class C4kFolderTests
{
    private const string Folder = @"C:\Items";

    private static C4kFolder Build(params (string, string)[] files) => C4kReader.Build(Folder, files);

    [Fact]
    public void Build_orders_names_naturally_ignoring_case()
    {
        var folder = Build(("10-b.c4k", "{}"), ("2-a.c4k", "{}"), ("B.C4K", "{}"));

        Assert.Equal(["2-a", "10-b", "B"], folder.Items.Select(i => i.Name));
    }

    [Fact]
    public void Build_caps_at_50_and_counts_skipped()
    {
        var folder = Build([.. Enumerable.Range(0, 60).Select(i => ($"{i:D2}.c4k", "{}"))]);

        Assert.Equal(50, folder.Items.Count);
        Assert.Equal(10, folder.Skipped);
    }

    [Fact]
    public void Broken_file_keeps_neighbours()
    {
        var folder = Build(("a.c4k", "{}"), ("b.c4k", "{ nope"), ("c.c4k", "{}"));

        Assert.Equal(3, folder.Items.Count);
        Assert.Single(folder.Items, i => i.Error is not null);
        Assert.Equal(C4kLayout.Dashboard, folder.Layout);
    }

    [Fact]
    public void Single_broken_file_reports_error()
    {
        var folder = Build(("a.c4k", "[1]"));

        Assert.Equal(C4kLayout.Single, folder.Layout);
        Assert.NotNull(folder.Single!.Error);
        Assert.NotNull(folder.Error);
    }

    [Fact]
    public void Layout_by_count()
    {
        Assert.Equal(C4kLayout.None, Build().Layout);
        Assert.Equal(C4kLayout.Single, Build(("a.c4k", "{}")).Layout);
        Assert.Equal(C4kLayout.Dashboard, Build(("a.c4k", "{}"), ("b.c4k", "{}")).Layout);
    }

    [Fact]
    public void Parse_reads_plugin_options_and_size()
    {
        var item = C4kItem.Parse(Folder, "x.c4k", """
            { // comment
              "title": "Mail", "plugin": "official.mail", "preview": "account", "size": "wide", "refresh": "5m",
              "options": { "user": "a@b.c" },
            }
            """);

        Assert.Null(item.Error);
        Assert.Equal("Mail", item.Title);
        Assert.Equal("official.mail", item.Plugin);
        Assert.Equal("account", item.PreviewId);
        Assert.Equal(TileSize.Wide, item.Size);
        Assert.Equal(TimeSpan.FromMinutes(5), item.Refresh);
        Assert.Contains("a@b.c", item.Options);
        Assert.Equal("x", item.Name);
        Assert.Equal("x", C4kItem.Parse(Folder, "x.c4k", "{}").Title);
        Assert.Equal(FolderActionKind.View, item.Kind);
    }

    [Fact]
    public void Parse_resolves_folder_key_against_file_folder()
    {
        var item = C4kItem.Parse(@"C:\Accounts\work", "x.c4k", """{ "folder": "../acc" }""");

        Assert.Equal(@"C:\Accounts\acc", item.ContextFolder);
        Assert.Equal(@"C:\Accounts\work", C4kItem.Parse(@"C:\Accounts\work", "y.c4k", "{}").ContextFolder);
    }

    [Fact]
    public void Bad_card_field_sets_error_and_keeps_card()
    {
        var item = C4kItem.Parse(Folder, "x.c4k", """{ "card": true, "size": "giant" }""");

        Assert.NotNull(item.Error);
        Assert.Equal(C4kItem.AutoCard, item.Card);
    }

    [Fact]
    public void IsItemFile_rejects_directories_dotfiles_and_oversize()
    {
        Assert.True(C4kReader.IsItemFile(new VfsEntry("a.C4K", @"C:\a.C4K", VfsEntryKind.File, 10)));
        Assert.False(C4kReader.IsItemFile(new VfsEntry(".thumbnail.c4k", @"C:\.thumbnail.c4k", VfsEntryKind.Directory)));
        Assert.False(C4kReader.IsItemFile(new VfsEntry(".c4k", @"C:\.c4k", VfsEntryKind.File, 10)));
        Assert.False(C4kReader.IsItemFile(new VfsEntry("big.c4k", @"C:\big.c4k", VfsEntryKind.File, 300 * 1024)));
        Assert.False(C4kReader.IsItemFile(new VfsEntry("x.txt", @"C:\x.txt", VfsEntryKind.File, 10)));
    }

    [Fact]
    public async Task ReadAsync_lists_only_c4k_files()
    {
        var dir = Directory.CreateTempSubdirectory("c4k-items");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "b.c4k"), """{ "title": "B" }""");
            File.WriteAllText(Path.Combine(dir.FullName, "a.c4k"), "{}");
            File.WriteAllText(Path.Combine(dir.FullName, "note.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".thumbnail.c4k"));
            var router = new Vfs.VfsRouter(new Execution.ResilientAsyncExecutor(), [new DiskProvider()]);

            var folder = await C4kReader.ReadAsync(router, dir.FullName, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(["a", "b"], folder!.Items.Select(i => i.Name));
            Assert.Equal("B", folder.Items[1].Title);
            Assert.Null(await C4kReader.ReadAsync(router, Directory.CreateTempSubdirectory("c4k-empty").FullName, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task A_listing_error_reads_as_no_items()
    {
        var router = new Vfs.VfsRouter(new Execution.ResilientAsyncExecutor(), [new FakeVfsProvider()]);

        Assert.Null(await C4kReader.ReadAsync(router, "nope://host/home", TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Ssh_open_and_run_items()
    {
        var ssh = C4kItem.Parse(@"C:\Work\Server", "vps.c4k", """{ "open": "sftp://kamil@beelink.lan/home/kamil", "keyFile": "~/.ssh/id_ed25519" }""");
        Assert.Equal(FolderActionKind.Open, ssh.Kind);
        Assert.EndsWith(@".ssh\id_ed25519", ssh.Resolve(ssh.KeyFile!), StringComparison.Ordinal);
        Assert.Equal("sftp://kamil@beelink.lan/home/kamil", ssh.Resolve(ssh.Open!));

        var build = C4kItem.Parse(@"C:\Work\Server", "build.c4k", """{ "run": { "command": "build.ps1", "args": ["-Release"], "cwd": "src" }, "image": "../cover.png" }""");
        Assert.Equal(FolderActionKind.Run, build.Kind);
        Assert.Equal(("build.ps1", "src", false), (build.Run!.Command, build.Run.WorkingDirectory, build.Run.Hidden));
        Assert.Equal("build.ps1 -Release", build.Run.ToString());
        Assert.Equal(@"C:\Work\cover.png", build.Resolve(build.Image!));
    }

    [Theory]
    [InlineData("examples.git", "log", "examples.git/log")]
    [InlineData(null, "builtin.sysinfo", "builtin.sysinfo")]
    [InlineData("x.y", "other.plugin/view", "other.plugin/view")]
    public void Panel_ids_are_qualified_with_the_plugin(string? plugin, string panel, string expected) =>
        Assert.Equal(expected, new C4kItem { FilePath = "x.c4k", Folder = Folder, Name = "x", Plugin = plugin, Panel = panel }.PanelId);

    [Theory]
    [InlineData(@"C:\", @"C:\a.c4k")]
    [InlineData(@"\\nas\media\films", @"\\nas\media\films\a.c4k")]
    [InlineData("sftp://me@host/home/me", "sftp://me@host/home/me/a.c4k")]
    [InlineData(@"zip://C:\a.zip::/docs", @"zip://C:\a.zip::/docs/a.c4k")]
    public void Child_paths_follow_the_location(string folder, string expected) =>
        Assert.Equal(expected, C4kJson.ChildPath(folder, "a.c4k"));

    [Fact]
    public void Card_items_parse_api_fields()
    {
        var item = C4kItem.Parse(Folder, "grafana.c4k", """
            { "card": "api", "refresh": "1m",
              "api": {
                "url": "https://grafana.example.com/api/health",
                "headers": { "Authorization": "Bearer {secret:grafana}" },
                "fields": [
                  { "path": "database", "label": "Baza", "as": "status", "ok": ["ok"] },
                  { "path": "version", "label": "Wersja" }
                ]
              } }
            """);

        Assert.Null(item.Error);
        Assert.Equal(FolderViewIds.Api, item.Card);
        Assert.Equal(TimeSpan.FromMinutes(1), item.Refresh);
        var api = item.Api!;
        Assert.Equal("GET", api.Method);
        Assert.False(api.HasSideEffects);
        Assert.Equal("Bearer {secret:grafana}", api.Headers["Authorization"]);
        Assert.Equal(ApiFieldKind.Status, api.Fields[0].As);
        Assert.Equal(["ok"], api.Fields[0].Ok);
        Assert.Equal(ApiFieldKind.Value, api.Fields[1].As);
        Assert.Equal("Wersja", api.Fields[1].Label);
    }

    [Fact]
    public void Api_view_with_post_has_side_effects()
    {
        var item = C4kItem.Parse(Folder, "api.c4k", """{ "view": "api", "api": { "url": "https://x/y", "method": "post", "body": "{}" } }""");

        Assert.Null(item.Error);
        Assert.Equal("POST", item.Api!.Method);
        Assert.True(item.Api.HasSideEffects);
        Assert.Equal("{}", item.Api.Body);
        Assert.False(item.IsPlainFiles);
    }

    [Theory]
    [InlineData("1s", 2)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    public void Refresh_is_parsed_and_clamped_to_two_seconds(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), C4kJson.ParseRefresh(text));

    [Fact]
    public void Refresh_rejects_other_text()
    {
        Assert.Null(C4kJson.ParseRefresh(null));
        Assert.Throws<FormatException>(() => C4kJson.ParseRefresh("fast"));
        Assert.Throws<FormatException>(() => C4kJson.ParseRefresh("10"));
        Assert.Throws<FormatException>(() => C4kJson.ParseRefresh("300000000h"));
        Assert.Throws<FormatException>(() => C4kJson.ParseRefresh("25h"));
    }

    [Theory]
    [InlineData("""{ "card": true, "refresh": "300000000h" }""")]
    [InlineData("""{ "card": true, "refresh": 10 }""")]
    [InlineData("""{ "card": true, "refresh": "fast" }""")]
    [InlineData("""{ "card": "api", "api": { "method": "GET" } }""")]
    [InlineData("""{ "card": "api" }""")]
    [InlineData("""{ "card": 5 }""")]
    [InlineData("""{ "card": "api", "api": { "url": "https://x/y", "fields": [ { "path": "a", "as": "pie" } ] } }""")]
    [InlineData("""{ "card": true, "run": "build.ps1" }""")]
    [InlineData("""{ "run": { "args": [] } }""")]
    [InlineData("""{ "run": 5 }""")]
    public void Bad_card_and_run_fields_become_item_errors(string json) =>
        Assert.False(string.IsNullOrEmpty(C4kItem.Parse(Folder, "x.c4k", json).Error));

    [Fact]
    public void Detects_comments()
    {
        Assert.True(C4kWriter.HasComments("{ // note\n \"view\": \"gallery\" }"));
        Assert.True(C4kWriter.HasComments("""{ /* a */ "view": "gallery" }"""));
        Assert.False(C4kWriter.HasComments("""{ "url": "https://example.com/a//b" }"""));
    }

    private sealed class DiskProvider : IVfsProviderPlugin
    {
        public string Id => "test.disk";

        public string DisplayName => "Disk";

        public PluginManifest Manifest { get; } = new("test.disk", "Disk", "1.0.0", "tests", "Disk", [PluginPermissions.FileRead]);

        public IReadOnlyList<string> Schemes { get; } = ["file"];

        public VfsCapabilities Capabilities => VfsCapabilities.Read;

        public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var f in Directory.EnumerateFileSystemEntries(path))
            {
                var info = new FileInfo(f);
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                yield return new VfsEntry(info.Name, f, isDir ? VfsEntryKind.Directory : VfsEntryKind.File, isDir ? -1 : info.Length);
            }
        }

        public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken) => ValueTask.FromResult<VfsEntry?>(null);

        public string? GetParentPath(string path) => Path.GetDirectoryName(path);

        public string Combine(string directoryPath, string name) => Path.Combine(directoryPath, name);

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(File.OpenRead(path));
    }
}
