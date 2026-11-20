using Commander4K.Core.Folders;

namespace Commander4K.Core.Tests;

public sealed class C4kViewsTests
{
    private const string Folder = @"C:\Items";

    private static C4kItem Parse(string json) => C4kItem.Parse(Folder, "srv.c4k", json);

    [Fact]
    public void Entries_inherit_the_files_keys_and_default_picks_the_item()
    {
        var item = Parse("""
            { "open": "sftp://h", "user": "root", "title": "Server",
              "views": [ { "title": "SSH", "view": "terminal" }, { "title": "SFTP", "default": true }, { "view": "monitor" } ] }
            """);

        Assert.Null(item.Error);
        Assert.Equal(3, item.Views.Count);
        Assert.All(item.Views, v => Assert.Equal("root", v.User));
        Assert.Equal("Server", item.Title);
        Assert.Equal(["SSH", "SFTP", "monitor"], item.Views.Select(v => v.Title));
        Assert.Null(item.View);
        Assert.Equal(FolderActionKind.Open, item.Kind);
        Assert.True(item.Views[1].IsDefault);
        Assert.False(item.Views[0].IsDefault);
    }

    [Fact]
    public void First_entry_is_the_item_without_default()
    {
        var item = Parse("""{ "views": [ { "view": "notes" }, { "view": "gallery" } ] }""");

        Assert.Equal("notes", item.View);
        Assert.Equal(2, item.Views.Count);
    }

    [Fact]
    public void A_single_entry_applies_but_makes_no_switch()
    {
        var item = Parse("""{ "views": [ { "view": "gallery" } ] }""");

        Assert.Equal("gallery", item.View);
        Assert.Empty(item.Views);
    }

    [Fact]
    public void Views_must_be_a_list_of_objects()
    {
        Assert.NotNull(Parse("""{ "views": "x" }""").Error);
        Assert.NotNull(Parse("""{ "views": [ 1 ] }""").Error);
    }

    [Fact]
    public void Terminal_view_keeps_open_as_the_server_not_the_action()
    {
        var item = Parse("""{ "open": "sftp://h", "views": [ { "view": "terminal" }, { "default": true } ] }""");

        Assert.Equal(FolderActionKind.Open, item.Kind);
        Assert.Equal(FolderActionKind.View, item.Views[0].Kind);
        Assert.True(item.Views[0].IsServerView);
        Assert.False(item.Views[1].IsServerView);
    }

    [Fact]
    public void A_link_to_an_ssh_server_is_an_ssh_card_unless_the_file_says_otherwise()
    {
        Assert.Equal("ssh", Parse("""{ "open": "sftp://root@h" }""").Card);
        Assert.True(Parse("""{ "open": "sftp://root@h" }""").NeedsTrust); // it signs in, so it waits for "Zezwól"
        Assert.Null(Parse("""{ "open": "sftp://root@h", "card": false }""").Card);
        Assert.Equal("files", Parse("""{ "open": "sftp://root@h", "card": "files" }""").Card);
        Assert.Null(Parse("""{ "open": "ftp://h" }""").Card);
        Assert.Null(Parse("""{ "open": "C:\\Data" }""").Card);
        Assert.Null(Parse("""{ "view": "terminal", "open": "sftp://root@h" }""").Card);

        // With views the default entry is the card, and the file's trust covers it even without a monitor view.
        var views = Parse("""{ "open": "sftp://root@h", "views": [ { "view": "terminal" }, { "default": true } ] }""");
        Assert.Equal("ssh", views.Card);
        Assert.True(views.NeedsTrust);
    }

    [Fact]
    public void Trust_is_shared_by_every_view_and_monitor_needs_it()
    {
        var item = Parse("""{ "open": "sftp://h", "user": "root", "views": [ { "view": "monitor" }, { "default": true } ] }""");

        Assert.True(item.NeedsTrust);
        Assert.All(item.Views, v => Assert.Equal(item.TrustHash, v.TrustHash));
        Assert.All(item.Views, v => Assert.True(v.NeedsTrust));
    }

    [Fact]
    public void Trust_hash_of_an_ssh_card_is_stable_and_follows_open()
    {
        var a = Parse("""{ "card": "ssh", "open": "sftp://h", "user": "root" }""");
        var again = Parse("""{ "title": "other", "card": "ssh", "open": "sftp://h", "user": "root" }""");
        var moved = Parse("""{ "card": "ssh", "open": "sftp://other", "user": "root" }""");

        Assert.Equal(a.TrustHash, again.TrustHash);
        Assert.NotEqual(a.TrustHash, moved.TrustHash);
    }

    [Fact]
    public void Build_reads_views_from_a_file()
    {
        var folder = C4kReader.Build(Folder, [("srv.c4k", """{ "views": [ { "view": "notes" }, { "view": "gallery" } ] }""")]);

        Assert.Equal(2, folder.Items.Single().Views.Count);
    }
}
