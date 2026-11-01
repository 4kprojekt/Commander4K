using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class FolderSuggestionRulesTests
{
    private static readonly VfsEntry Folder = new("photos", @"C:\photos", VfsEntryKind.Directory, 0);

    private static FolderSnapshot Snapshot(bool truncated = false, params string[] names) =>
        new(Folder, [.. names.Select(n => n.EndsWith('/')
            ? new VfsEntry(n.TrimEnd('/'), @"C:\photos\" + n.TrimEnd('/'), VfsEntryKind.Directory, 0)
            : new VfsEntry(n, @"C:\photos\" + n, VfsEntryKind.File, 1))], truncated);

    private static string[] Files(string extension, int count, string prefix = "f") =>
        [.. Enumerable.Range(0, count).Select(i => $"{prefix}{i}{extension}")];

    private static readonly PreviewSuggestRule Images = new(Files: ["*.jpg", "*.png"], MinCount: 5, MinShare: 0.6);

    [Fact]
    public void Files_and_thresholds_match() =>
        Assert.Equal(6, FolderSuggestionRules.Match(Images, Snapshot(false, [.. Files(".jpg", 6), .. Files(".txt", 2)])));

    [Fact]
    public void Too_few_files_fail() =>
        Assert.Null(FolderSuggestionRules.Match(Images, Snapshot(false, Files(".jpg", 4))));

    [Fact]
    public void Share_below_threshold_fails() =>
        Assert.Null(FolderSuggestionRules.Match(Images, Snapshot(false, [.. Files(".jpg", 5), .. Files(".txt", 5)])));

    [Fact]
    public void Folders_do_not_count_toward_share() =>
        Assert.Equal(5, FolderSuggestionRules.Match(Images, Snapshot(false, [.. Files(".jpg", 5), .. Files("/", 10, "d")])));

    [Fact]
    public void Contains_requires_entry()
    {
        var rule = new PreviewSuggestRule(Contains: [".obsidian"]);

        Assert.Null(FolderSuggestionRules.Match(rule, Snapshot(false, "a.md", "b.md")));
        Assert.NotNull(FolderSuggestionRules.Match(rule, Snapshot(false, "a.md", ".obsidian/")));
    }

    [Fact]
    public void Truncated_snapshot_uses_what_was_read() =>
        Assert.Equal(6, FolderSuggestionRules.Match(Images, Snapshot(true, Files(".jpg", 6))));

    [Fact]
    public void Pattern_rules_follow_extension_matching() =>
        Assert.Equal(1, FolderSuggestionRules.Match(new PreviewSuggestRule(Files: [".csv"]), Snapshot(false, "a.CSV")));

    [Fact]
    public void Message_picks_language_then_en_then_first_and_fills_count()
    {
        var rule = new PreviewSuggestRule(Message: new Dictionary<string, string> { ["pl"] = "{count} zdjęć", ["en"] = "{count} photos" });

        Assert.Equal("6 zdjęć", FolderSuggestionRules.MessageFor(rule, 6, "pl", "Galeria"));
        Assert.Equal("6 photos", FolderSuggestionRules.MessageFor(rule, 6, "de", "Galeria"));
        Assert.Equal("x", FolderSuggestionRules.MessageFor(new PreviewSuggestRule(Message: new Dictionary<string, string> { ["fr"] = "x" }), 1, "pl", "Galeria"));
        Assert.Equal("Galeria", FolderSuggestionRules.MessageFor(new PreviewSuggestRule(), 1, "pl", "Galeria"));
    }
}
