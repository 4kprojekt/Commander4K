using System.Text.Json.Nodes;
using Commander4K.Core.Security;
using Commander4K.Core.Theming;

namespace Commander4K.Core.Tests;

public sealed class SkinSchemaTests
{
    [Fact]
    public void Documentation_schema_validates_colours_and_border_names()
    {
        var json = SkinSchema.CreateJson();

        Assert.Contains("\"pattern\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Double\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cursorBackground\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_takes_the_json_out_of_a_pasted_chat_answer()
    {
        var json = SkinJson.Serialize(BuiltInSkins.NortonCommander);

        Assert.Equal(BuiltInSkins.NortonCommander, SkinJson.Deserialize(SkinJson.Extract($"Here it is:\n```json\n{json}\n```\nEnjoy!")));
        Assert.Equal(BuiltInSkins.NortonCommander, SkinJson.Deserialize(SkinJson.Extract($"Sure — {json} — done.")));
        Assert.Equal(json.Trim(), SkinJson.Extract("  " + json + "  "));
    }

    [Fact]
    public void TryRead_finds_the_skin_in_noisy_text_and_repairs_the_id()
    {
        var json = SkinJson.Serialize(BuiltInSkins.NortonCommander with { Id = "Mój Skin!" });
        var noisy = $"Sure {{not json}} here you go:\n```json\n{json}\n```\nBye {{";

        Assert.True(SkinJson.TryRead(noisy, fixId: true, out var skin, out _));
        Assert.Equal("moj-skin", skin.Id);
        Assert.False(SkinJson.TryRead(noisy, fixId: false, out _, out var error));
        Assert.NotEmpty(error);
        Assert.False(SkinJson.TryRead("no json at all", fixId: true, out _, out _));
    }

    [Fact]
    public void Read_accepts_a_skin_written_the_way_other_assistants_write_it()
    {
        // PascalCase and snake_case keys, "colors" for palette, short and rgb() colours, quoted numbers,
        // lower-case enum values, no typography, and a field the model does not have.
        var palette = JsonNode.Parse(SkinJson.Serialize(BuiltInSkins.ModernDark))!["palette"]!.AsObject();
        var colors = new JsonObject();
        foreach (var (name, value) in palette)
        {
            colors[char.ToUpperInvariant(name[0]) + name[1..]] = value?.DeepClone();
        }

        colors["Accent"] = "#0af";
        colors["CursorBackground"] = "rgb(30, 60, 90)";
        colors["panel_border"] = "334455";
        colors.Remove("PanelBorder");
        colors["glowColor"] = "#ffffff";
        var answer = new JsonObject
        {
            ["id"] = "neon",
            ["name"] = "Neon",
            ["colors"] = colors,
            ["chrome"] = new JsonObject { ["borderStyle"] = "double", ["themeVariant"] = "dark", ["cornerRadius"] = "6" },
        };

        var read = SkinJson.Read("Here is your skin:\n```json\n" + answer.ToJsonString() + "\n```", fixId: false);

        Assert.Null(read.Error);
        var skin = read.Skin!;
        Assert.Equal("Neon", skin.DisplayName);
        Assert.Equal(RgbColor.Parse("#00AAFF"), skin.Palette.Accent);
        Assert.Equal(RgbColor.Parse("#1E3C5A"), skin.Palette.CursorBackground);
        Assert.Equal(RgbColor.Parse("#334455"), skin.Palette.PanelBorder);
        Assert.Equal(PanelBorderStyle.Double, skin.Chrome.BorderStyle);
        Assert.Equal("Dark", skin.Chrome.ThemeVariant);
        Assert.Equal(6, skin.Chrome.CornerRadius);
        Assert.False(string.IsNullOrWhiteSpace(skin.Typography.FontFamily));
        Assert.Contains(read.Notes, note => note.Contains("glowColor", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_lists_every_problem_in_plain_words_with_its_place()
    {
        var json = JsonNode.Parse(SkinJson.Serialize(BuiltInSkins.ModernDark))!.AsObject();
        json["palette"]!["accent"] = "very blue";
        json["palette"]!.AsObject().Remove("cursorBackground");
        json["typography"]!["fontSize"] = "big";

        var read = SkinJson.Read(json.ToJsonString(), fixId: false);

        Assert.Null(read.Skin);
        Assert.Contains("palette.accent", read.Error, StringComparison.Ordinal);
        Assert.Contains("palette.cursorBackground", read.Error, StringComparison.Ordinal);
        Assert.Contains("typography.fontSize", read.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Commander4K", read.Error, StringComparison.Ordinal);

        var broken = SkinJson.Read("{ \"id\": \"x\", \"palette\": { \"accent\": \"#fff\" ", fixId: false);
        Assert.Null(broken.Skin);
        Assert.DoesNotContain("Commander4K", broken.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialized_skin_round_trips_with_string_enums()
    {
        var json = SkinJson.Serialize(BuiltInSkins.NortonCommander);

        Assert.Contains("\"borderStyle\": \"Double\"", json, StringComparison.Ordinal);
        Assert.Equal(BuiltInSkins.NortonCommander, SkinJson.Deserialize(json));
    }

    [Fact]
    public void Validator_accepts_built_ins_text_contrast_and_flags_unreadable_skins()
    {
        Assert.Empty(SkinValidator.Validate(BuiltInSkins.ModernDark));
        Assert.Empty(SkinValidator.Validate(BuiltInSkins.TotalCommanderClassic));

        var unreadable = BuiltInSkins.ModernDark with
        {
            Id = "Bad Id",
            Palette = BuiltInSkins.ModernDark.Palette with { FileForeground = BuiltInSkins.ModernDark.Palette.PanelBackground },
        };
        var warnings = SkinValidator.Validate(unreadable);

        Assert.Contains(warnings, w => w.StartsWith("Pliki:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Bad Id", StringComparison.Ordinal));
        Assert.Equal(21, SkinValidator.ContrastRatio(RgbColor.Parse("#000000"), RgbColor.Parse("#FFFFFF")), 0.01);
    }

    [Fact]
    public void Known_hosts_trust_on_first_use_and_detect_changed_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c4k-known-{Guid.NewGuid():N}.json");
        try
        {
            var store = new KnownHostsStore(path);
            Assert.Equal(HostKeyStatus.Unknown, store.Check("NAS", 22, "abc"));

            store.Trust("NAS", 22, "abc");
            var reloaded = new KnownHostsStore(path);

            Assert.Equal(HostKeyStatus.Trusted, reloaded.Check("nas", 22, "abc"));
            Assert.Equal(HostKeyStatus.Mismatch, reloaded.Check("nas", 22, "xyz"));
            Assert.Equal(HostKeyStatus.Unknown, reloaded.Check("nas", 2222, "abc"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<JsonNode> Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, child) in obj)
                {
                    foreach (var nested in Walk(child))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var nested in Walk(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
