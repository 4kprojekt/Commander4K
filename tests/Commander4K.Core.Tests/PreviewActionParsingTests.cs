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
              "buttons": [ { "label": "Wyślij", "target": "c4k:plugin/send", "shortcut": "Alt+S" } ],
              "message": "Brak adresata",
              "messageError": true
            }
            """);

        var form = Assert.IsType<FormPreview>(result);
        Assert.Equal("Nowa wiadomość", form.Title);
        Assert.Equal(new[] { new FormField("to", "Do", "a@b.pl", false), new FormField("body", "Treść", "Cześć", true) }, form.Fields);
        Assert.Equal(new[] { new FormFile("big.iso", @"C:\tmp\big.iso", 5_000_000_000L) }, form.Files);
        Assert.Equal(new[] { new PreviewAction("Wyślij", "c4k:plugin/send", "Alt+S") }, form.Buttons);
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

public sealed class PreviewShortcutTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reserved_shortcut_is_dropped_but_the_action_stays()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"kind":"text","content":"t","actions":[
              {"label":"A","target":"c4k:plugin/a","shortcut":"Esc"},
              {"label":"B","target":"c4k:plugin/b","shortcut":"Alt+Left"},
              {"label":"C","target":"c4k:plugin/c","shortcut":"Shift+Enter"},
              {"label":"D","target":"c4k:plugin/d","shortcut":"Ctrl+Backspace"},
              {"label":"E","target":"c4k:plugin/e","shortcut":"Tab"},
              {"label":"F","target":"c4k:plugin/f","shortcut":"nonsense+"},
              {"label":"G","target":"c4k:plugin/g","shortcut":"R"}]}
            """);

        var actions = Assert.IsType<ActionsPreview>(await ExternalPreviewPlugin.ParseRenderAsync(document.RootElement, new VfsEntry("x", "x", VfsEntryKind.File), null, TestToken)).Actions;

        Assert.Equal(["A", "B", "C", "D", "E", "F", "G"], actions.Select(a => a.Label));
        Assert.Equal([null, null, null, null, null, null, "R"], actions.Select(a => a.Shortcut));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Enter")]
    [InlineData("Shift+Enter")]
    [InlineData("Alt+Enter")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Tab")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Back")]
    [InlineData("Alt+Left")]
    [InlineData("Alt+Right")]
    public void Reserved_keys_have_no_shortcut(string shortcut) => Assert.Null(PreviewShortcuts.Normalize(shortcut));

    [Theory]
    [InlineData("r", "R")]
    [InlineData("Delete", "DELETE")]
    [InlineData("ctrl+shift+r", "Ctrl+Shift+R")]
    [InlineData("F6", "F6")]
    public void Ordinary_shortcuts_are_normalised(string shortcut, string expected) => Assert.Equal(expected, PreviewShortcuts.Normalize(shortcut));

    [Theory]
    [InlineData("Ctrl+A", true)]
    [InlineData("Ctrl+C", true)]
    [InlineData("Ctrl+V", true)]
    [InlineData("Ctrl+X", true)]
    [InlineData("Ctrl+Z", true)]
    [InlineData("Ctrl+Y", true)]
    [InlineData("Ctrl+Shift+Left", true)]
    [InlineData("Ctrl+Up", true)]
    [InlineData("Ctrl+Backspace", true)]
    [InlineData("Ctrl+Delete", true)]
    [InlineData("Ctrl+Shift+End", true)]
    [InlineData("Ctrl+Home", true)]
    [InlineData("Ctrl+R", false)]
    [InlineData("Alt+A", false)]
    [InlineData("F5", false)]
    public void Text_editing_chords_are_recognised(string stroke, bool editing) =>
        Assert.Equal(editing, PreviewShortcuts.IsTextEditing(Commander4K.Core.Input.KeyStroke.Parse(stroke)));
}
