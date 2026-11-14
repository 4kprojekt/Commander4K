using System.Text.Json.Nodes;
using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class C4kEditorSchemaTests
{
    private const string Base = """
        {
          // comment
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "plugin": { "type": "string", "description": "Plugin id." },
            "preview": { "type": "string" },
            "options": { "type": "object" },
            "card": { "oneOf": [ { "type": "boolean" }, { "type": "string" } ], },
          },
          "additionalProperties": false
        }
        """;

    private static C4kEditorPlugin Mail() => new("official.mail", "Mail", "Reads mail", true,
        [new C4kEditorTarget("account", "Account", true, [new OptionContribution("imap", "string", "IMAP server", null, true), new OptionContribution("since", "string", "Start", ["30d", "1y"])])], []);

    private static JsonObject Build(params C4kEditorPlugin[] plugins) =>
        C4kEditorSchema.Build(Base, plugins, [new C4kEditorCard("clock", "Clock", [new OptionContribution("timeZone", "string", "Zone")])]);

    [Fact]
    public void Base_keys_are_preserved()
    {
        var schema = Build(Mail());

        Assert.Equal("object", (string?)schema["type"]);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.NotNull(schema["properties"]!["title"]);
        Assert.Equal("object", (string?)schema["properties"]!["options"]!["type"]);
    }

    [Fact]
    public void Plugin_ids_and_descriptions_are_suggested_and_any_string_stays_valid()
    {
        var plugin = Build(Mail())["properties"]!["plugin"]!;
        var anyOf = plugin["anyOf"]!.AsArray();

        Assert.Equal("official.mail", (string?)anyOf[0]!["enum"]![0]);
        Assert.Contains("Reads mail", (string?)anyOf[0]!["enumDescriptions"]![0], StringComparison.Ordinal);
        Assert.Equal("string", (string?)anyOf[1]!["type"]);
        Assert.Null(anyOf[1]!["enum"]);
        Assert.Equal("Plugin id.", (string?)plugin["description"]);
    }

    [Fact]
    public void Options_of_an_installed_plugin_sit_under_its_if()
    {
        var branch = Build(Mail())["allOf"]!.AsArray().Single(b => (string?)b!["if"]!["properties"]!["plugin"]?["const"] == "official.mail")!;
        var then = branch["then"]!["properties"]!;

        Assert.Equal("IMAP server", (string?)then["options"]!["properties"]!["imap"]!["description"]);
        Assert.Equal("account", (string?)then["preview"]!["anyOf"]![0]!["enum"]![0]);
        Assert.Null(branch["then"]!["required"]);
    }

    [Fact]
    public void Catalog_only_plugin_gets_an_id_but_no_branch()
    {
        var schema = Build(new C4kEditorPlugin("official.pdf", "PDF", "Shows PDF", false, [], []));

        Assert.Equal("official.pdf", (string?)schema["properties"]!["plugin"]!["anyOf"]![0]!["enum"]![0]);
        Assert.DoesNotContain(schema["allOf"]!.AsArray(), b => (string?)b!["if"]!["properties"]!["plugin"]?["const"] == "official.pdf");
    }

    [Fact]
    public void Builtin_card_ids_and_options_are_offered()
    {
        var schema = Build();

        Assert.Equal("clock", (string?)schema["properties"]!["card"]!["anyOf"]![2]!["enum"]![0]);
        Assert.Contains(schema["allOf"]!.AsArray(), b => (string?)b!["if"]!["properties"]!["card"]?["const"] == "clock");
    }

    [Fact]
    public void Result_round_trips_and_FromManifest_maps_contributions()
    {
        Assert.NotNull(JsonNode.Parse(Build(Mail()).ToJsonString()));

        var manifest = new PluginManifest("a.b", "AB", "1", "me", "desc", [])
        {
            Contributes = new PluginContributions(Previews: [new PreviewContribution("p", "P", [".x"], Card: true, Options: [new OptionContribution("o")])]),
        };
        var plugin = C4kEditorSchema.FromManifest(manifest, true);

        Assert.Equal("p", plugin.Previews[0].Id);
        Assert.True(plugin.Previews[0].Card);
        Assert.Equal("o", plugin.Previews[0].Options[0].Id);
    }

    [Fact]
    public void Real_base_schema_builds()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory);
            if (directory is null)
            {
                return; // scratch artifacts folder: no repository around
            }
        }

        var schema = C4kEditorSchema.Build(File.ReadAllText(Path.Combine(directory, "docs", "c4k.schema.json")), [Mail()], []);

        Assert.NotNull(schema["$defs"]!["api"]);
        Assert.NotNull(schema["properties"]!["plugin"]!["anyOf"]);
    }
}
