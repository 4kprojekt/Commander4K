using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Registry;
using Microsoft.Win32;

namespace Commander4K.Plugins.Tests;

/// <summary>Works on a throw-away key under <c>HKCU\Software\Commander4K.Tests</c>, removed after each test.</summary>
public sealed class RegistryPluginTests : IDisposable
{
    private readonly string _subKey = $@"Software\Commander4K.Tests\{Guid.NewGuid():N}";
    private readonly string _keyPath;
    private readonly WindowsRegistryVfsPlugin _plugin = new();

    public RegistryPluginTests()
    {
        _keyPath = $@"reg://HKCU\{_subKey}";
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue("", "default text");
        key.SetValue("Version", "1.2.3");
        key.SetValue("Answer", 42, RegistryValueKind.DWord);
        using var child = key.CreateSubKey(@"Nested\Deeper");
        child.SetValue(@"Path\With\Backslashes", @"C:\Tools\needle.exe");
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);

    [Theory]
    [InlineData("reg://", null, "", null)]
    [InlineData("reg://HKEY_LOCAL_MACHINE", "HKLM", "", null)]
    [InlineData(@"reg://HKCU\Software\X", "HKCU", @"Software\X", null)]
    [InlineData("reg://HKCU/Software/X", "HKCU", @"Software\X", null)]
    [InlineData(@"reg://HKCU\Software\X\\Value\With\Slashes", "HKCU", @"Software\X", @"Value\With\Slashes")]
    [InlineData(@"reg://HKCU\Software\X\\", "HKCU", @"Software\X", "")]
    public void Parses_paths(string path, string? hive, string subKey, string? value) =>
        Assert.Equal(new RegistryPath(hive, subKey, value), RegistryPath.Parse(path));

    [Fact]
    public async Task Lists_subkeys_then_values()
    {
        var entries = new List<VfsEntry>();
        await foreach (var entry in _plugin.EnumerateAsync(_keyPath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.Equal(VfsEntryKind.RegistryKey, entries[0].Kind);
        Assert.Equal("Nested", entries[0].Name);
        Assert.Contains(entries, e => e.Name == WindowsRegistryVfsPlugin.DefaultValueDisplayName);
        Assert.Equal("DWord: 0x0000002A (42)", entries.Single(e => e.Name == "Answer").Description);
    }

    [Fact]
    public async Task Value_content_and_parent()
    {
        var valuePath = RegistryPath.ForValue(_keyPath, "Version");

        await using var stream = await _plugin.OpenReadAsync(valuePath, TestToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("1.2.3", await reader.ReadToEndAsync(TestToken));
        Assert.Equal(_keyPath, _plugin.GetParentPath(valuePath));
        Assert.Equal("reg://HKCU", _plugin.GetParentPath(@"reg://HKCU\Software"));
        Assert.Equal(RegistryPath.RootPath, _plugin.GetParentPath("reg://HKCU"));
    }

    [Fact]
    public async Task Parallel_search_finds_value_data_in_nested_keys()
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in _plugin.SearchAsync(new SearchQuery(_keyPath, ContainsText: "NEEDLE"), TestToken))
        {
            hits.Add(hit);
        }

        var hit1 = Assert.Single(hits);
        Assert.Equal(@"Path\With\Backslashes", hit1.Entry.Name);
        Assert.Contains("needle", hit1.Snippet, StringComparison.Ordinal);
        Assert.EndsWith(@"Nested\Deeper\\Path\With\Backslashes", hit1.Entry.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_by_name_pattern_matches_keys_and_values()
    {
        var names = new List<string>();
        await foreach (var hit in _plugin.SearchAsync(new SearchQuery(_keyPath, NamePattern: "*e*"), TestToken))
        {
            names.Add(hit.Entry.Name);
        }

        Assert.Equal(["Answer", "Deeper", "Nested", "Path\\With\\Backslashes", "Version"], names.Order());
    }

    [Fact]
    public async Task Create_and_delete_key()
    {
        var path = _plugin.Combine(_keyPath, "Created");

        await _plugin.CreateDirectoryAsync(path, TestToken);
        Assert.NotNull(await _plugin.GetEntryAsync(path, TestToken));

        await _plugin.DeleteAsync(path, recursive: true, TestToken);
        Assert.Null(await _plugin.GetEntryAsync(path, TestToken));
    }
}
