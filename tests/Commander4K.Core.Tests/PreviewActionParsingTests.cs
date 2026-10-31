using System.Text.Json;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class PreviewActionParsingTests
{
    private static readonly VfsEntry Entry = new("list.eml", @"C:\mail\list.eml", VfsEntryKind.File, 10);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static async Task<PreviewContent> ParseAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        return await ExternalPreviewPlugin.ParseRenderAsync(document.RootElement, Entry, null, TestToken);
    }

    [Fact]
    public async Task Render_with_actions_becomes_ActionsPreview()
    {
        var result = await ParseAsync("""{"kind":"markdown","content":"x","actions":[{"label":"Odpowiedz","target":"c4k:plugin/reply","shortcut":"R"}]}""");

        var actions = Assert.IsType<ActionsPreview>(result);
        Assert.Equal(new MarkdownPreview("x", false), actions.Content);
        Assert.Equal(new[] { new PreviewAction("Odpowiedz", "c4k:plugin/reply", "R") }, actions.Actions);
    }

    [Fact]
    public async Task Actions_without_target_are_dropped_and_an_empty_list_keeps_the_plain_preview()
    {
        var result = await ParseAsync("""{"kind":"text","content":"t","actions":[{"label":"Nic"}]}""");

        Assert.Equal(new TextPreview("t", false), result);
    }

    [Fact]
    public async Task Form_result_parses_fields_files_buttons()
    {
        var result = await ParseAsync("""
            {
              "kind": "form",
              "title": "Nowa wiadomość",
              "fields": [
                { "id": "to", "label": "Do", "value": "a@b.pl" },
                { "id": "body", "label": "Treść", "value": "Cześć", "multiline": true }
              ],
              "files": [ { "name": "big.iso", "path": "C:\\tmp\\big.iso", "size": 5000000000 } ],
              "buttons": [ { "label": "Wyślij", "target": "c4k:plugin/send", "shortcut": "Ctrl+Enter" } ],
              "message": "Brak adresata",
              "messageError": true
            }
            """);

        var form = Assert.IsType<FormPreview>(result);
        Assert.Equal("Nowa wiadomość", form.Title);
        Assert.Equal(new[] { new FormField("to", "Do", "a@b.pl", false), new FormField("body", "Treść", "Cześć", true) }, form.Fields);
        Assert.Equal(new[] { new FormFile("big.iso", @"C:\tmp\big.iso", 5_000_000_000L) }, form.Files);
        Assert.Equal(new[] { new PreviewAction("Wyślij", "c4k:plugin/send", "Ctrl+Enter") }, form.Buttons);
        Assert.Equal("Brak adresata", form.Message);
        Assert.True(form.MessageIsError);
    }

    [Fact]
    public async Task Form_with_actions_is_wrapped_too()
    {
        var result = await ParseAsync("""{"kind":"form","title":"F","fields":[],"actions":[{"label":"Zamknij","target":"c4k:plugin/close"}]}""");

        var actions = Assert.IsType<ActionsPreview>(result);
        var form = Assert.IsType<FormPreview>(actions.Content);
        Assert.Empty(form.Files);
        Assert.Empty(form.Buttons);
        Assert.Null(form.Message);
    }

    [Fact]
    public async Task Unknown_kind_still_falls_back_to_text()
    {
        var result = await ParseAsync("""{"kind":"hologram","content":"zwykły tekst"}""");

        Assert.Equal(new TextPreview("zwykły tekst", false), result);
    }
}
