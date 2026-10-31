using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Commander4K.Core.Configuration;
using Commander4K.Core.Updates;

namespace Commander4K.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly string Asset = UpdateService.AssetNameFor(RuntimeInformation.ProcessArchitecture);
    private readonly string _directory = Directory.CreateTempSubdirectory("c4k-update-").FullName;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta", -1)]
    [InlineData("v2.0", "2.0.0+abc123", 0)]
    public void Versions_compare_by_semver_rules(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemanticVersion.Parse(a).CompareTo(SemanticVersion.Parse(b))));

    [Fact]
    public void Version_parsing_keeps_build_metadata_for_display()
    {
        var version = SemanticVersion.Parse("0.1.0-dev+a1b2c3d4e5f6");

        Assert.Equal("0.1.0-dev", version.ToString());
        Assert.Equal("a1b2c3d4e5f6", version.Build);
        Assert.False(SemanticVersion.TryParse("latest", out _));
    }

    [Fact]
    public async Task Check_picks_newest_stable_release_with_this_architecture()
    {
        using var http = new HttpClient(new FakeGitHub(ReleasesJson()));
        var service = new UpdateService(http);

        var update = await service.CheckAsync("4kprojekt/Commander4K", SemanticVersion.Parse("0.1.0"), includePrereleases: false, TestToken);

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update.Version.ToString());
        Assert.Equal(Asset, update.AssetName);
        Assert.NotNull(update.ChecksumsUrl);
    }

    [Fact]
    public async Task Check_offers_prereleases_only_when_asked_and_nothing_when_current()
    {
        using var http = new HttpClient(new FakeGitHub(ReleasesJson()));
        var service = new UpdateService(http);

        var beta = await service.CheckAsync("o/r", SemanticVersion.Parse("0.1.0"), includePrereleases: true, TestToken);
        var none = await service.CheckAsync("o/r", SemanticVersion.Parse("0.3.0"), includePrereleases: false, TestToken);

        Assert.Equal("0.4.0-beta.1", beta!.Version.ToString());
        Assert.Null(none);
    }

    [Fact]
    public async Task Missing_repository_is_reported_clearly()
    {
        using var http = new HttpClient(new FakeGitHub(null));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateService(http).CheckAsync("nobody/nothing", SemanticVersion.Parse("0.1.0"), false, TestToken));

        Assert.Contains("nobody/nothing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_accepts_matching_checksum_and_rejects_tampered_file()
    {
        var payload = Encoding.UTF8.GetBytes("new exe bytes");
        var good = Convert.ToHexStringLower(SHA256.HashData(payload));
        var update = new UpdateInfo(SemanticVersion.Parse("0.3.0"), "v0.3.0", "v0.3.0", "", "https://x", false,
            Asset, "https://dl/asset", payload.Length, "https://dl/sums");

        using (var http = new HttpClient(new FakeDownloads(payload, $"{good}  {Asset}\n")))
        {
            var path = await new UpdateService(http).DownloadAsync(update, _directory, null, TestToken);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestToken));
        }

        using (var http = new HttpClient(new FakeDownloads(payload, $"{new string('0', 64)} *{Asset}\n")))
        {
            await Assert.ThrowsAsync<UpdateException>(() => new UpdateService(http).DownloadAsync(update, Path.Combine(_directory, "bad"), null, TestToken));
            Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "bad")));
        }
    }

    [Fact]
    public void Install_swaps_files_and_cleanup_removes_old_version()
    {
        var current = Path.Combine(_directory, "Commander4K.exe");
        var staged = Path.Combine(_directory, "staged.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(staged, "new");

        UpdateService.Install(staged, current);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(current + ".old"));
        UpdateService.CleanupPreviousVersion(current);
        Assert.False(File.Exists(current + ".old"));
        Assert.True(UpdateService.CanSelfUpdate(current));
        File.WriteAllText(Path.Combine(_directory, "Commander4K.dll"), "");
        Assert.False(UpdateService.CanSelfUpdate(current));
    }

    [Fact]
    public void Install_kind_and_zip_extraction()
    {
        var exe = Path.Combine(_directory, "Commander4K.exe");
        File.WriteAllText(exe, "x");
        Assert.Equal(InstallKind.Portable, UpdateService.DetectInstallKind(exe));
        File.WriteAllText(Path.Combine(_directory, UpdateService.MsiMarkerFileName), "msi");
        Assert.Equal(InstallKind.Msi, UpdateService.DetectInstallKind(exe));
        Assert.Equal(InstallKind.Development, UpdateService.DetectInstallKind(Path.Combine(_directory, "dotnet.exe")));
        Assert.EndsWith(".msi", UpdateService.AssetNameFor(Architecture.Arm64, InstallKind.Msi), StringComparison.Ordinal);
        Assert.Equal("Commander4K-win-x64.zip", UpdateService.AssetNameFor(Architecture.X64));

        var zip = Path.Combine(_directory, "release.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Commander4K.exe").Open());
            writer.Write("new build");
        }

        var extracted = UpdateService.ExtractExecutable(zip, _directory);
        Assert.Equal("new build", File.ReadAllText(extracted));
    }

    [Fact]
    public void Keymap_merge_adds_new_defaults_but_keeps_user_removals()
    {
        var file = new KeymapFile
        {
            Bindings = new(StringComparer.OrdinalIgnoreCase) { ["F5"] = "cm_Copy" },
            KnownCommands = ["cm_Copy", "cm_Move"], // cm_Move was known and deliberately unbound.
        };

        Assert.True(ConfigStore.MergeNewDefaults(file));

        Assert.DoesNotContain("cm_Move", file.Bindings.Values);
        Assert.Equal("cm_Settings", file.Bindings["Ctrl+,"]);
        Assert.Equal("cm_PanelModes", file.Bindings["F2"]);
        Assert.Contains("cm_Move", file.KnownCommands!);
        Assert.False(ConfigStore.MergeNewDefaults(file));
    }

    [Fact]
    public void Keymap_merge_drops_bindings_of_removed_commands()
    {
        var file = new KeymapFile
        {
            Bindings = new(StringComparer.OrdinalIgnoreCase) { ["Ctrl+B"] = "cm_TogglePalettes", ["Ctrl+D"] = "cm_AddFavorite", ["F5"] = "cm_Copy" },
            KnownCommands = ["cm_TogglePalettes", "cm_AddFavorite", "cm_Copy"],
        };

        Assert.True(ConfigStore.MergeNewDefaults(file));

        Assert.False(file.Bindings.ContainsKey("Ctrl+B"));
        Assert.False(file.Bindings.ContainsKey("Ctrl+D"));
        Assert.Equal("cm_Copy", file.Bindings["F5"]);
        Assert.False(ConfigStore.MergeNewDefaults(file));
    }

    [Fact]
    public void Keymap_migration_moves_old_f1_f2_defaults_but_keeps_user_choices()
    {
        var untouched = new KeymapFile
        {
            Bindings = new(StringComparer.OrdinalIgnoreCase) { ["F1"] = "cm_Help", ["F2"] = "cm_SelectSkin" },
            KnownCommands = ["cm_Help", "cm_SelectSkin"],
        };
        var customised = new KeymapFile
        {
            Bindings = new(StringComparer.OrdinalIgnoreCase) { ["F1"] = "cm_Copy", ["F2"] = "cm_SelectSkin" },
            KnownCommands = ["cm_Help", "cm_SelectSkin", "cm_Copy"],
        };

        ConfigStore.MergeNewDefaults(untouched);
        ConfigStore.MergeNewDefaults(customised);

        Assert.Equal("cm_Settings", untouched.Bindings["F1"]);
        Assert.Equal("cm_PanelModes", untouched.Bindings["F2"]);
        Assert.Equal("cm_Copy", customised.Bindings["F1"]);
        Assert.Equal("cm_PanelModes", customised.Bindings["F2"]);

        // Once applied, a user who puts F1 back to help keeps it.
        untouched.Bindings["F1"] = "cm_Help";
        Assert.False(ConfigStore.MergeNewDefaults(untouched));
        Assert.Equal("cm_Help", untouched.Bindings["F1"]);
    }

    [Theory]
    [InlineData("ukryte", "showHidden")]
    [InlineData("zrodlo", "updateRepository")]
    [InlineData("AKTUALIZACJE beta", "includePrereleases")]
    [InlineData("motyw", "skin")]
    [InlineData("KONTEKSTOWE eksplorator", "shellContextMenu")]
    public void Settings_search_ignores_case_and_polish_diacritics(string query, string expectedKey)
    {
        var catalog = SettingsCatalog.Create(() => []);

        Assert.Equal(expectedKey, SettingsCatalog.Search(catalog, query)[0].Key);
        Assert.Equal(catalog.Count, SettingsCatalog.Search(catalog, "  ").Count);
        Assert.Empty(SettingsCatalog.Search(catalog, "nieistniejące ustawienie xyz"));
    }

    private static string ReleasesJson() => $$"""
        [
          { "tag_name": "v0.5.0", "name": "draft", "draft": true, "prerelease": false, "html_url": "https://x/5", "body": "", "assets": [] },
          { "tag_name": "v0.4.0-beta.1", "name": "beta", "draft": false, "prerelease": true, "html_url": "https://x/4", "body": "",
            "assets": [ { "name": "{{Asset}}", "browser_download_url": "https://dl/4", "size": 10 } ] },
          { "tag_name": "v0.3.0", "name": "Commander4K 0.3.0", "draft": false, "prerelease": false, "html_url": "https://x/3", "body": "Zmiany",
            "assets": [
              { "name": "{{Asset}}", "browser_download_url": "https://dl/3", "size": 10 },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://dl/3/sums", "size": 1 } ] },
          { "tag_name": "v0.2.0", "name": "old", "draft": false, "prerelease": false, "html_url": "https://x/2", "body": "", "assets": [] }
        ]
        """;

    private sealed class FakeGitHub(string? releasesJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("Commander4K", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Task.FromResult(releasesJson is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) });
        }
    }

    private sealed class FakeDownloads(byte[] asset, string sums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("sums", StringComparison.Ordinal)
                    ? new StringContent(sums)
                    : new ByteArrayContent(asset),
            });
    }
}
