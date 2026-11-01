using Commander4K.Core.Folders;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;

namespace Commander4K.Plugins.Tests;

public sealed class FolderSuggestionTests
{
    private static FolderSnapshot Snapshot(params string[] names) =>
        new(new VfsEntry("f", @"C:\f", VfsEntryKind.Directory, 0), [.. names.Select(n => n.EndsWith('/')
            ? new VfsEntry(n.TrimEnd('/'), @"C:\f\" + n.TrimEnd('/'), VfsEntryKind.Directory, 0)
            : new VfsEntry(n, @"C:\f\" + n, VfsEntryKind.File, 1))], false);

    private static string[] Files(string extension, int count) => [.. Enumerable.Range(0, count).Select(i => $"n{i}{extension}")];

    [Fact]
    public void Gallery_needs_five_images_and_sixty_percent()
    {
        Assert.NotNull(FolderSuggestions.Gallery(Snapshot([.. Files(".png", 5), .. Files(".txt", 3)])));
        Assert.Null(FolderSuggestions.Gallery(Snapshot(Files(".png", 4))));
        Assert.Null(FolderSuggestions.Gallery(Snapshot([.. Files(".png", 5), .. Files(".txt", 4)])));
    }

    [Fact]
    public void Gallery_message_counts_images()
    {
        var suggestion = FolderSuggestions.Gallery(Snapshot(Files(".jpg", 5)))!;

        Assert.Contains("5", suggestion.Message, StringComparison.Ordinal);
        Assert.Equal(FolderViewIds.Gallery, suggestion.View.View);
        Assert.Equal(0.7, suggestion.Score);
    }

    [Fact]
    public void Notes_from_obsidian_folder() =>
        Assert.Equal(FolderViewIds.Notes, FolderSuggestions.Notes(Snapshot(".obsidian/", "a.md"))!.View.View);

    [Fact]
    public void Notes_need_five_markdown_files()
    {
        Assert.Null(FolderSuggestions.Notes(Snapshot(Files(".md", 4))));
        Assert.Equal(FolderViewIds.Notes, FolderSuggestions.Notes(Snapshot([.. Files(".md", 5), .. Files(".txt", 3)]))!.View.View);
    }

    [Fact]
    public async Task Folder_preview_prefers_gallery_then_notes()
    {
        var plugin = new FolderPreviewPlugin();
        var token = TestContext.Current.CancellationToken;

        var photos = await plugin.SuggestAsync(Snapshot([.. Files(".png", 8), .. Files(".md", 2)]), null!, token);
        var vault = await plugin.SuggestAsync(Snapshot([.. Files(".png", 5), .. Files(".md", 5), ".obsidian/"]), null!, token);

        Assert.Equal(FolderViewIds.Gallery, photos!.View.View);
        Assert.Equal(FolderViewIds.Notes, vault!.View.View);
    }
}
