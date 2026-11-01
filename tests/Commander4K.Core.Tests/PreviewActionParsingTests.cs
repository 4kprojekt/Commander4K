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
                { "id": "body", "label": "Treść", "value": "Cześć", "multiline": true },
                { "id": "password", "label": "Hasło", "value": "", "password": true }
              ],
              "files": [ { "name": "big.iso", "path": "C:\\tmp\\big.iso", "size": 5000000000 } ],
              "buttons": [ { "label": "Wyślij", "target": "c4k:plugin/send", "shortcut": "Alt+S" } ],
              "message": "Brak adresata",
              "messageError": true
            }
            """);

        var form = Assert.IsType<FormPreview>(result);
        Assert.Equal("Nowa wiadomość", form.Title);
        Assert.Equal(new[]
        {
            new FormField("to", "Do", "a@b.pl", false),
            new FormField("body", "Treść", "Cześć", true),
            new FormField("password", "Hasło", "", false, true),
        }, form.Fields);
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
    public async Task A_result_with_another_path_tells_the_host_to_follow_the_entry()
    {
        var result = await ParseAsync("""{"kind":"markdown","content":"x","path":"C:\\mail\\read.eml","actions":[{"label":"R","target":"c4k:plugin/reply"}]}""");

        var moved = Assert.IsType<MovedPreview>(result);
        Assert.Equal(@"C:\mail\read.eml", moved.Entry.Path);
        Assert.Equal("read.eml", moved.Entry.Name);
        Assert.Equal(VfsEntryKind.File, moved.Entry.Kind);
        Assert.IsType<ActionsPreview>(moved.Content);
    }

    [Theory]
    [InlineData("""{"kind":"text","content":"t","path":"C:\\mail\\list.eml"}""")]
    [InlineData("""{"kind":"text","content":"t","path":""}""")]
    [InlineData("""{"kind":"text","content":"t","path":"relative.eml"}""")]
    public async Task The_same_empty_or_relative_path_is_not_a_move(string json) =>
        Assert.Equal(new TextPreview("t", false), await ParseAsync(json));

    [Fact]
    public async Task Unknown_kind_still_falls_back_to_text()
    {
        var result = await ParseAsync("""{"kind":"hologram","content":"zwykły tekst"}""");

        Assert.Equal(new TextPreview("zwykły tekst", false), result);
    }
}

public sealed class PreviewPluginResolverTests
{
    private static readonly VfsEntry Entry = new("entry", "C:\\entry", VfsEntryKind.File);

    [Fact]
    public void Resolver_uses_highest_priority_matching_preview()
    {
        var entry = new VfsEntry("page", "http://router.lan/", VfsEntryKind.File);
        var lower = new StubPreview("test.lower", 10, true);
        var higher = new StubPreview("test.higher", 20, true);
        var unmatched = new StubPreview("test.unmatched", 100, false);

        Assert.Same(higher, PreviewPluginResolver.Resolve([lower, higher, unmatched], entry));
    }

    [Fact]
    public void Resolver_returns_null_when_no_preview_accepts_entry() =>
        Assert.Null(PreviewPluginResolver.Resolve([new StubPreview("test.none", 1, false)], Entry));

    private sealed class StubPreview(string id, int priority, bool accepts) : IPreviewPlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public PluginManifest Manifest { get; } = new(id, id, "1.0.0", "test", "test preview", []);

        public int Priority => priority;

        public bool CanPreview(VfsEntry entry) => accepts;

        public Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<PreviewContent>(new MessagePreview(id));
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
              {"label":"G","target":"c4k:plugin/g","shortcut":"R"},
              {"label":"H","target":"c4k:plugin/h","shortcut":"Ctrl+Enter"}]}
            """);

        var actions = Assert.IsType<ActionsPreview>(await ExternalPreviewPlugin.ParseRenderAsync(document.RootElement, new VfsEntry("x", "x", VfsEntryKind.File), null, TestToken)).Actions;

        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H"], actions.Select(a => a.Label));
        Assert.Equal([null, null, null, null, null, null, "R", "Ctrl+Enter"], actions.Select(a => a.Shortcut));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Enter")]
    [InlineData("Shift+Enter")]
    [InlineData("Alt+Enter")]
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
    [InlineData("ctrl+enter", "Ctrl+ENTER")]
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
