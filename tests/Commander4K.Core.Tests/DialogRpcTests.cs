using System.Text.Json;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class DialogRpcTests
{
    [Fact]
    public void Parses_fields_modes_and_answers_with_their_types()
    {
        using var json = JsonDocument.Parse("""
            { "title": "Konto", "submit": "Zapisz", "errors": { "user": "zły adres" },
              "fields": [
                { "id": "h", "type": "heading", "label": "Serwer" },
                { "id": "user", "label": "Adres", "required": true },
                { "id": "auth", "type": "segmented", "value": "key", "options": [ { "value": "password", "label": "Hasło" }, "key" ] },
                { "id": "keyFile", "type": "file", "visibleWhen": { "auth": "key" } },
                { "id": "port", "type": "number", "value": 22, "min": 1, "max": 65535 },
                { "id": "save", "type": "checkbox", "value": true } ] }
            """);

        var request = DialogRpc.Parse(json.RootElement);

        Assert.Equal("Konto", request.Title);
        Assert.Equal("zły adres", request.Errors!["user"]);
        Assert.Equal(DialogFieldType.Heading, request.Fields[0].Type);
        Assert.Equal(DialogFieldType.Text, request.Fields[1].Type);
        Assert.True(request.Fields[1].Required);
        Assert.Equal([new DialogOption("password", "Hasło"), new DialogOption("key", "key")], request.Fields[2].Options!);
        Assert.Equal(["key"], request.Fields[3].VisibleWhen!["auth"]);
        Assert.Equal("22", request.Fields[4].Value);
        Assert.Equal(65535, request.Fields[4].Max);
        Assert.Equal("true", request.Fields[5].Value);

        var answer = DialogRpc.Answer(request, new Dictionary<string, string> { ["user"] = "jan", ["port"] = "2222", ["save"] = "false" })!;
        Assert.Equal("jan", answer["user"]);
        Assert.Equal(2222d, answer["port"]);
        Assert.Equal(false, answer["save"]);
        Assert.False(answer.ContainsKey("keyFile"));
        Assert.Null(DialogRpc.Answer(request, null));
    }

    [Theory]
    [InlineData("""{ "title": "x" }""")]
    [InlineData("""{ "fields": [] }""")]
    [InlineData("""{ "title": "x", "fields": [ { "id": "a", "type": "slider" } ] }""")]
    [InlineData("""{ "title": "x", "fields": [ { "id": "a", "type": "3" } ] }""")]
    [InlineData("""{ "title": "x", "fields": [ { "id": "a", "type": "select" } ] }""")]
    public void Refuses_malformed_requests(string text)
    {
        using var json = JsonDocument.Parse(text);
        Assert.Throws<ArgumentException>(() => DialogRpc.Parse(json.RootElement));
    }
}
