using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class PanelTransferRoutingTests
{
    private const PanelModeKind Files = PanelModeKind.Files;
    private const PanelModeKind Preview = PanelModeKind.Preview;
    private const PanelModeKind Plugin = PanelModeKind.Plugin;
    private const PanelModeKind Other = PanelModeKind.Other;

    [Theory]
    // Active file list: a folder (file list, quick view, pinned folder view) copies as usual.
    [InlineData(Files, false, Files, false, false, PanelTransferRoute.OrdinaryCopy)]
    [InlineData(Files, false, Files, false, true, PanelTransferRoute.OrdinaryCopy)]
    [InlineData(Files, false, Preview, false, false, PanelTransferRoute.OrdinaryCopy)]
    [InlineData(Files, false, Preview, false, true, PanelTransferRoute.OrdinaryCopy)]
    // Active file list onto a plugin panel.
    [InlineData(Files, false, Plugin, true, false, PanelTransferRoute.SendToPanel)]
    [InlineData(Files, false, Plugin, false, false, PanelTransferRoute.RefuseNotAccepting)]
    [InlineData(Files, false, Plugin, true, true, PanelTransferRoute.RefuseMove)]
    [InlineData(Files, false, Plugin, false, true, PanelTransferRoute.RefuseMove)]
    // Active file list onto the browser or any other mode.
    [InlineData(Files, false, Other, false, false, PanelTransferRoute.RefuseNotAccepting)]
    [InlineData(Files, false, Other, false, true, PanelTransferRoute.RefuseNotAccepting)]
    // Active plugin panel.
    [InlineData(Plugin, true, Files, false, false, PanelTransferRoute.ProvideFromPanel)]
    [InlineData(Plugin, true, Preview, false, false, PanelTransferRoute.ProvideFromPanel)]
    [InlineData(Plugin, true, Plugin, true, false, PanelTransferRoute.RefuseNotAccepting)]
    [InlineData(Plugin, true, Other, false, false, PanelTransferRoute.RefuseNotAccepting)]
    [InlineData(Plugin, true, Files, false, true, PanelTransferRoute.RefuseMove)]
    [InlineData(Plugin, false, Files, false, true, PanelTransferRoute.RefuseMove)]
    [InlineData(Plugin, false, Files, false, false, PanelTransferRoute.BlockedModeActive)]
    [InlineData(Plugin, false, Plugin, true, false, PanelTransferRoute.BlockedModeActive)]
    // Active preview or browser: no files to copy from there.
    [InlineData(Preview, false, Files, false, false, PanelTransferRoute.BlockedModeActive)]
    [InlineData(Preview, false, Plugin, true, false, PanelTransferRoute.BlockedModeActive)]
    [InlineData(Other, false, Files, false, false, PanelTransferRoute.BlockedModeActive)]
    [InlineData(Other, false, Files, false, true, PanelTransferRoute.BlockedModeActive)]
    public void Decide_routes_every_combination(PanelModeKind active, bool activeCanProvide, PanelModeKind other, bool otherCanReceive, bool move, PanelTransferRoute expected) =>
        Assert.Equal(expected, PanelTransferRouting.Decide(active, activeCanProvide, other, otherCanReceive, move));

    [Fact]
    public void Inline_limit_leaves_room_in_one_reply()
    {
        // base64 grows content by a third; one file at the limit must still fit in a 16 MB message.
        Assert.True(PanelTransferLimits.MaxInlineBytes / 3 * 4 < JsonRpcConnection.MaxMessageLength);
        Assert.Equal(8, PanelTransferLimits.MaxInlineMegabytes);
    }
}
