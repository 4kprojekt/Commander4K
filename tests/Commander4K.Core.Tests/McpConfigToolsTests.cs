using System.Text.Json.Nodes;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;
using Commander4K.Core.Theming;

namespace Commander4K.Core.Tests;

public sealed class McpConfigToolsTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "c4k-mcp-config-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly List<string> _changed = [];
    private readonly Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase) { ["F5"] = "cm_Copy" };
    private readonly IReadOnlyList<McpTool> _tools;

    public McpConfigToolsTests()
    {
        Directory.CreateDirectory(_config);
        var catalog = SettingsCatalog.Create(() => [.. BuiltInSkins.All.Select(s => new SettingChoice(s.Id, s.DisplayName))]);
        _tools = McpConfigTools.Create(new McpConfigContext(
            () => catalog,
            _settings,
            action => action(),
            key =>
            {
                _changed.Add(key);
                return Task.CompletedTask;
            },
            () => _bindings,
            bindings =>
            {
                // Same validation as AppHost.SaveKeyBindingsAsync.
                _ = new Input.KeymapEngine(bindings);
                _bindings.Clear();
                foreach (var (keys, command) in bindings)
                {
                    _bindings[keys] = command;
                }

                return Task.CompletedTask;
            },
            id => id is "cm_Copy" or "cm_OpenNetwork" ? "description of " + id : null,
            _config,
            () => SkinCatalog.BuiltInOnly,
            () => BuiltInSkins.ModernDarkId));
    }

    public void Dispose() => Directory.Delete(_config, recursive: true);

    private Task<McpToolResult> Call(string name, JsonObject args) =>
        _tools.Single(t => t.Definition.Name == name).Handler(new McpCall("test", args), TestContext.Current.CancellationToken);

    private string LanguageFile(string code) => Path.Combine(_config, Loc.FolderName, code + ".json");

    private static Dictionary<string, string> Reference() => Loc.Parse(Loc.BuiltInFile("en")!);

    [Fact]
    public async Task List_settings_finds_by_polish_and_english_words()
    {
        foreach (var query in new[] { "skórka", "theme" })
        {
            var result = await Call("list_settings", new JsonObject { ["query"] = query });
            Assert.False(result.IsError);
            Assert.Contains("key: skin", result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task List_settings_marks_read_only_keys()
    {
        var result = await Call("list_settings", new JsonObject { ["query"] = "mcp" });
        Assert.Contains("key: mcp.roots", result.Text, StringComparison.Ordinal);
        Assert.Contains("read-only over MCP", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_setting_changes_value_and_notifies()
    {
        var wanted = !_settings.ShowHidden;
        var result = await Call("set_setting", new JsonObject { ["key"] = "showHidden", ["value"] = wanted });
        Assert.False(result.IsError, result.Text);
        Assert.Equal(wanted, _settings.ShowHidden);
        Assert.Equal(["showHidden"], _changed);
        Assert.Equal($"showHidden: {(!wanted ? "true" : "false")} → {(wanted ? "true" : "false")}.", result.Text);

        Assert.False((await Call("set_setting", new JsonObject { ["key"] = "density", ["value"] = "compact" })).IsError);
        Assert.Equal("compact", _settings.Density);
    }

    [Fact]
    public async Task Set_setting_rejects_unknown_choice_bad_toggle_and_unknown_key()
    {
        Assert.True((await Call("set_setting", new JsonObject { ["key"] = "density", ["value"] = "huge" })).IsError);
        Assert.Equal("default", _settings.Density);
        Assert.True((await Call("set_setting", new JsonObject { ["key"] = "showHidden", ["value"] = "maybe" })).IsError);
        Assert.True((await Call("set_setting", new JsonObject { ["key"] = "nope", ["value"] = "x" })).IsError);
        Assert.Empty(_changed);
    }

    [Theory]
    [InlineData("mcp.enabled", "true")]
    [InlineData("mcp.roots", "C:\\Windows")]
    [InlineData("MCP.Port", "1234")]
    [InlineData("mcp:delete", "allow")]
    [InlineData("pluginCatalogUrl", "https://evil.example/catalog.json")]
    [InlineData("updateRepository", "evil/repo")]
    public async Task Set_setting_refuses_read_only_keys(string key, string value)
    {
        var before = _settings.Mcp.AllowedRoots.Count;
        var result = await Call("set_setting", new JsonObject { ["key"] = key, ["value"] = value });
        Assert.True(result.IsError);
        Assert.Contains("settings window", result.Text, StringComparison.Ordinal);
        Assert.False(_settings.Mcp.Enabled);
        Assert.Equal(before, _settings.Mcp.AllowedRoots.Count);
        Assert.Equal(AppSettings.DefaultPluginCatalogUrl, _settings.PluginCatalogUrl);
        Assert.Equal(AppSettings.DefaultUpdateRepository, _settings.UpdateRepository);
        Assert.Empty(_settings.Mcp.ToolModes);
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task Set_key_binding_reports_replaced_command_and_removes()
    {
        var result = await Call("set_key_binding", new JsonObject { ["keys"] = "F5", ["command"] = "cm_OpenNetwork" });
        Assert.False(result.IsError, result.Text);
        Assert.Contains("cm_Copy", result.Text, StringComparison.Ordinal);
        Assert.Equal("cm_OpenNetwork", _bindings["F5"]);

        Assert.False((await Call("set_key_binding", new JsonObject { ["keys"] = "F5" })).IsError);
        Assert.False(_bindings.ContainsKey("F5"));
    }

    [Fact]
    public async Task Set_key_binding_rejects_bad_keys_and_unknown_commands()
    {
        Assert.True((await Call("set_key_binding", new JsonObject { ["keys"] = "Ctrl+Nope+", ["command"] = "cm_Copy" })).IsError);
        Assert.True((await Call("set_key_binding", new JsonObject { ["keys"] = "Ctrl+Alt+N", ["command"] = "cm_Unknown" })).IsError);
        Assert.Equal("cm_Copy", _bindings["F5"]);
        Assert.Single(_bindings);
    }

    [Fact]
    public async Task List_key_bindings_shows_descriptions()
    {
        var result = await Call("list_key_bindings", new JsonObject());
        Assert.Contains("F5 → cm_Copy (description of cm_Copy)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translation_kit_pages_and_only_missing()
    {
        var reference = Reference();
        var first = await Call("get_translation_kit", new JsonObject { ["language"] = "de", ["limit"] = 50 });
        Assert.False(first.IsError, first.Text);
        Assert.Contains("next offset: 50", first.Text, StringComparison.Ordinal);
        Assert.Contains("plural forms: 2", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("```json", first.Text, StringComparison.Ordinal);

        var existing = reference.Take(10).ToDictionary(e => e.Key, e => e.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile("de"))!);
        await File.WriteAllTextAsync(LanguageFile("de"), System.Text.Json.JsonSerializer.Serialize(existing), TestContext.Current.CancellationToken);
        var missing = await Call("get_translation_kit", new JsonObject { ["language"] = "de", ["onlyMissing"] = true, ["limit"] = 500 });
        Assert.Contains($"{reference.Count - 10} entries", missing.Text, StringComparison.Ordinal);
        var firstKey = System.Text.Json.JsonSerializer.Serialize(reference.Keys.First());
        Assert.DoesNotContain(firstKey + ":", missing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_translation_merges_and_writes()
    {
        var plain = Reference().Where(e => !e.Key.Contains('{', StringComparison.Ordinal) && !e.Key.StartsWith('_')).Take(2).ToList();
        Assert.False((await Call("save_translation", Entries("de", (plain[0].Key, "Eins")))).IsError);
        var second = await Call("save_translation", Entries("de", (plain[1].Key, "Zwei")));
        Assert.False(second.IsError, second.Text);

        var saved = Loc.Parse(await File.ReadAllTextAsync(LanguageFile("de"), TestContext.Current.CancellationToken));
        Assert.Equal("Eins", saved[plain[0].Key]);
        Assert.Equal("Zwei", saved[plain[1].Key]);
        Assert.Contains("language", second.Text, StringComparison.Ordinal);

        var check = await Call("check_translation", Entries("de", (plain[0].Key, "Ein")));
        Assert.False(check.IsError);
        Assert.Equal("Eins", Loc.Parse(await File.ReadAllTextAsync(LanguageFile("de"), TestContext.Current.CancellationToken))[plain[0].Key]);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("de/../../x")]
    [InlineData("")]
    [InlineData("con")]
    [InlineData("nul")]
    public async Task Save_translation_refuses_bad_codes(string code)
    {
        var key = Reference().Keys.First(k => !k.StartsWith('_'));
        var result = await Call("save_translation", Entries(code, (key, "x")));
        Assert.True(result.IsError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_config));
    }

    [Fact]
    public async Task Save_translation_refuses_placeholder_mismatch_and_unknown_keys()
    {
        var withPlaceholder = Reference().Keys.First(k => k.Contains("{0}", StringComparison.Ordinal) && !k.Contains('|', StringComparison.Ordinal));
        var result = await Call("save_translation", Entries("de", (withPlaceholder, "ohne Platzhalter")));
        Assert.True(result.IsError);
        Assert.Contains(withPlaceholder, result.Text, StringComparison.Ordinal);
        Assert.True((await Call("save_translation", Entries("de", ("Nie ma takiego klucza 123", "x")))).IsError);
        Assert.False(File.Exists(LanguageFile("de")));
    }

    [Fact]
    public async Task Broken_language_file_and_missing_arguments_are_tool_errors()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile("de"))!);
        await File.WriteAllTextAsync(LanguageFile("de"), "{ broken", TestContext.Current.CancellationToken);
        Assert.True((await Call("get_translation_kit", new JsonObject { ["language"] = "de", ["onlyMissing"] = true })).IsError);
        Assert.True((await Call("save_translation", Entries("de", ("x", "y")))).IsError);
        Assert.True((await Call("get_translation_kit", new JsonObject())).IsError);
        Assert.True((await Call("check_translation", new JsonObject { ["entries"] = new JsonObject() })).IsError);
    }

    [Theory]
    [InlineData("mine\n")]
    [InlineData("con")]
    [InlineData("com1")]
    public void Skin_ids_that_are_not_safe_file_names_are_refused(string id) =>
        Assert.False(McpConfigTools.IsSkinId(id));

    [Theory]
    [InlineData("de\n", false)]
    [InlineData("lpt1", false)]
    [InlineData("pt-BR", true)]
    public void Language_codes_are_safe_file_names(string code, bool valid) =>
        Assert.Equal(valid, McpConfigTools.IsLanguageCode(code));

    [Fact]
    public async Task Check_skin_reports_low_contrast_and_bad_json()
    {
        var unreadable = BuiltInSkins.ModernDark with
        {
            Id = "unreadable",
            Palette = BuiltInSkins.ModernDark.Palette with { FileForeground = BuiltInSkins.ModernDark.Palette.PanelBackground },
        };
        var warned = await Call("check_skin", new JsonObject { ["json"] = SkinJson.Serialize(unreadable) });
        Assert.False(warned.IsError);
        Assert.StartsWith("Warnings", warned.Text, StringComparison.Ordinal);

        var ok = await Call("check_skin", new JsonObject { ["json"] = SkinJson.Serialize(BuiltInSkins.ModernDark with { Id = "mine" }) });
        Assert.StartsWith("OK", ok.Text, StringComparison.Ordinal);

        Assert.True((await Call("check_skin", new JsonObject { ["json"] = "{ not json" })).IsError);
        Assert.True((await Call("check_skin", new JsonObject { ["json"] = SkinJson.Serialize(BuiltInSkins.ModernDark with { Id = "..\\x" }) })).IsError);
    }

    [Fact]
    public async Task Skin_guide_list_and_get()
    {
        var guide = await Call("get_skin_guide", new JsonObject());
        Assert.Contains("\"" + BuiltInSkins.ModernDarkId + "\"", guide.Text, StringComparison.Ordinal);
        var list = await Call("list_skins", new JsonObject());
        Assert.Contains(BuiltInSkins.ModernDarkId + " (current, built-in)", list.Text, StringComparison.Ordinal);
        var skin = await Call("get_skin", new JsonObject { ["id"] = BuiltInSkins.ModernDarkId });
        Assert.Equal(BuiltInSkins.ModernDarkId, SkinJson.Deserialize(skin.Text).Id);
        Assert.True((await Call("get_skin", new JsonObject { ["id"] = "missing" })).IsError);
    }

    private static JsonObject Entries(string code, params (string Key, string Value)[] entries)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in entries)
        {
            obj[key] = value;
        }

        return new JsonObject { ["language"] = code, ["entries"] = obj };
    }
}
