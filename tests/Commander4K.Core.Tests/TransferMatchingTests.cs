using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class TransferMatchingTests
{
    private static TransferItem File(string name) => new(@"C:\x\" + name, name, TransferItemKind.File, 1);

    private static TransferItem Folder(string name) => new(@"C:\x\" + name, name, TransferItemKind.Folder, 0);

    [Fact]
    public void Multiple_false_rejects_two_files()
    {
        var receiver = new PanelReceiver("r", "R");

        Assert.False(TransferMatching.Fits(receiver, [File("a.txt"), File("b.txt")]));
        Assert.True(TransferMatching.Fits(receiver, [File("a.txt")]));
    }

    [Fact]
    public void Extensions_use_preview_patterns()
    {
        var receiver = new PanelReceiver("r", "R", Extensions: [".json", "openapi.*"], Multiple: true);

        Assert.True(TransferMatching.Fits(receiver, [File("a.JSON")]));
        Assert.True(TransferMatching.Fits(receiver, [File("openapi.yaml")]));
        Assert.False(TransferMatching.Fits(receiver, [File("a.txt")]));
        Assert.False(TransferMatching.Fits(receiver, [File("a.json"), File("a.txt")]));
    }

    [Fact]
    public void Folder_selection_needs_folder_receiver()
    {
        IReadOnlyList<TransferItem> items = [File("a.txt"), Folder("src")];

        Assert.False(TransferMatching.Fits(new PanelReceiver("r", "R", Multiple: true), items));
        Assert.True(TransferMatching.Fits(new PanelReceiver("r", "R", Multiple: true, Folders: true), items));
        Assert.Equal(Loc.T("Żaden odbiorca nie przyjmuje folderów."),
            TransferMatching.WhyNone([new PanelReceiver("r", "R", Multiple: true)], items));
    }

    [Fact]
    public void WhyNone_explains_single_file_limit()
    {
        IReadOnlyList<TransferItem> two = [File("a.txt"), File("b.txt")];

        Assert.Equal(Loc.T("Odbiorca „{0}” przyjmuje tylko jeden plik.", "R"),
            TransferMatching.WhyNone([new PanelReceiver("r", "R")], two));
        Assert.Equal(Loc.T("Wtyczka nie podała żadnego odbiorcy dla tych plików."), TransferMatching.WhyNone([], two));
        Assert.Equal(Loc.T("Żaden odbiorca nie przyjmuje plików tego typu."),
            TransferMatching.WhyNone([new PanelReceiver("r", "R", Extensions: [".json"], Multiple: true)], two));
        Assert.Null(TransferMatching.WhyNone([new PanelReceiver("r", "R", Multiple: true)], two));
    }
}
