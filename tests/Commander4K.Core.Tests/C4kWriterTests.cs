using System.Text.Json.Nodes;
using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class C4kWriterTests
{
    [Fact]
    public void UniqueFileName_adds_numbers()
    {
        var taken = new HashSet<string> { "mail.c4k", "mail 2.c4k" };

        Assert.Equal("mail.c4k", C4kWriter.UniqueFileName("mail", _ => false));
        Assert.Equal("mail 3.c4k", C4kWriter.UniqueFileName("mail", taken.Contains));
    }

    [Fact]
    public void UniqueFileName_strips_illegal_characters()
    {
        Assert.Equal("ab.c4k", C4kWriter.UniqueFileName(" a:b? ", _ => false));
        Assert.Equal("folder.c4k", C4kWriter.UniqueFileName("  ", _ => false));
    }

    [Fact]
    public void Create_puts_schema_first()
    {
        var json = C4kWriter.Create(new JsonObject { ["view"] = "gallery" });

        Assert.Equal(C4kWriter.SchemaUrl, JsonNode.Parse(json)!.AsObject().First().Value!.GetValue<string>());
        Assert.EndsWith("\n", json);
    }

    [Fact]
    public void SetOptions_keeps_other_keys()
    {
        var json = C4kWriter.SetOptions("{\"plugin\":\"p\",\"title\":\"T\"}", "{\"a\":1}");
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("T", (string?)root["title"]);
        Assert.Equal(1, (int?)root["options"]!["a"]);
    }

    [Fact]
    public void SetLogin_roundtrips_and_a_password_login_drops_the_key()
    {
        var json = C4kWriter.SetLogin("{\"open\":\"sftp://host\",\"keyFile\":\"old\"}", "ubuntu", @"C:\keys\id");
        var item = C4kItem.Parse(@"C:\x", "a.c4k", json);

        Assert.Equal(("ubuntu", @"C:\keys\id", "sftp://host"), (item.User, item.KeyFile, item.Open));
        Assert.Null(JsonNode.Parse(C4kWriter.SetLogin(json, "root", null))!["keyFile"]);
    }

    [Fact]
    public void SetSize_small_removes_key()
    {
        Assert.Null(JsonNode.Parse(C4kWriter.SetSize("{\"size\":\"wide\"}", TileSize.Small))!["size"]);
    }

    [Fact]
    public void SetSize_roundtrips_through_C4kItem_Parse()
    {
        var json = C4kWriter.SetSize("{\"view\":\"gallery\"}", TileSize.Wide);

        Assert.Equal(TileSize.Wide, C4kItem.Parse(@"C:\x", "a.c4k", json).Size);
    }

    [Fact]
    public void ItemFor_plugin_with_options()
    {
        var item = C4kWriter.ItemFor(new FolderSuggestionView(Plugin: "p", Preview: "v", OptionsJson: "{\"a\":1}"));

        Assert.Equal("p", (string?)item["plugin"]);
        Assert.Equal("v", (string?)item["preview"]);
        Assert.Equal(1, (int?)item["options"]!["a"]);
        Assert.Equal("gallery", (string?)C4kWriter.ItemFor(new FolderSuggestionView(View: "gallery"))["view"]);
    }

    [Fact]
    public async Task CreateNewAsync_never_overwrites()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var first = await C4kWriter.CreateNewAsync(dir, "mail", "one", TimeSpan.FromSeconds(5), CancellationToken.None);
            var second = await C4kWriter.CreateNewAsync(dir, "mail", "two", TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Equal(FolderConfigWrite.Written, first.Result);
            Assert.Equal(FolderConfigWrite.Written, second.Result);
            Assert.Equal(Path.Combine(dir, "mail 2.c4k"), second.Path);
            Assert.Equal("one", File.ReadAllText(Path.Combine(dir, "mail.c4k")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(dir, "mail 2.c4k")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
