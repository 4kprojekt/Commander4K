using System.Text.Json.Nodes;
using Commander4K.Core.Security;
using Commander4K.Core.Theming;

namespace Commander4K.Core.Tests;

public sealed class SkinSchemaTests
{
    [Fact]
    public void Structured_output_schema_closes_every_object_and_avoids_unsupported_keywords()
    {
        var schema = SkinSchema.CreateForStructuredOutput();

        var objects = Walk(schema).OfType<JsonObject>().Where(o => o["properties"] is JsonObject).ToList();
        Assert.NotEmpty(objects);
        foreach (var obj in objects)
        {
            Assert.False(obj["additionalProperties"]!.GetValue<bool>());
            var properties = ((JsonObject)obj["properties"]!).Select(p => p.Key).Order();
            Assert.Equal(properties, obj["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        }

        var json = schema.ToJsonString();
        Assert.DoesNotContain("\"pattern\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"minimum\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_schema_validates_colours_and_border_names()
    {
        var json = SkinSchema.CreateJson();

        Assert.Contains("\"pattern\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Double\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cursorBackground\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_for_chat_embeds_schema_example_and_description()
    {
        var prompt = SkinPrompt.ForChat("sepia jak stary terminal");

        Assert.Contains("sepia jak stary terminal", prompt, StringComparison.Ordinal);
        Assert.Contains("\"norton-commander\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"functionKeyLabelBackground\"", prompt, StringComparison.Ordinal);
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
