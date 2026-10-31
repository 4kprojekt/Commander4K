namespace Commander4K.Core.Plugins;

/// <summary>Limits of the panel transfer protocol, shared by the adapter and the job that writes provided files.</summary>
public static class PanelTransferLimits
{
    /// <summary>
    /// Inline <c>text</c>/<c>base64</c> content per provided file. Kept well under <see cref="JsonRpcConnection.MaxMessageLength"/>
    /// (one reply), since base64 grows content by a third and the reply carries every file at once.
    /// </summary>
    public const int MaxInlineBytes = 8 * 1024 * 1024;

    public const int MaxInlineMegabytes = MaxInlineBytes / (1024 * 1024);
}

/// <summary>What a panel shows over its file list, as far as F5/F6 are concerned.</summary>
public enum PanelModeKind
{
    /// <summary>The file list itself.</summary>
    Files,

    /// <summary>Quick view (Ctrl+Q) or a pinned folder view: the folder under it is still the panel's folder.</summary>
    Preview,

    /// <summary>A plugin panel.</summary>
    Plugin,

    /// <summary>The browser or any other mode: it takes no files.</summary>
    Other,
}

/// <summary>Outcome of <see cref="PanelTransferRouting.Decide"/>.</summary>
public enum PanelTransferRoute
{
    /// <summary>Copy or move between the folders of both panels.</summary>
    OrdinaryCopy,

    /// <summary>Send the active panel's files to the plugin panel opposite (<c>panel.receive</c>).</summary>
    SendToPanel,

    /// <summary>Write the files the active plugin panel provides into the other panel's folder (<c>panel.provide</c>).</summary>
    ProvideFromPanel,

    /// <summary>The other panel takes no files: a plugin without <c>accepts</c>, the browser, or another mode.</summary>
    RefuseNotAccepting,

    /// <summary>F6 into or out of a plugin panel.</summary>
    RefuseMove,

    /// <summary>The active panel shows a mode that has no files to copy (a plugin without <c>provides</c>, a preview, the browser).</summary>
    BlockedModeActive,
}

/// <summary>The F5/F6 decision, kept free of UI types so every case can be tested.</summary>
public static class PanelTransferRouting
{
    /// <param name="active">Mode of the panel the command comes from.</param>
    /// <param name="activeCanProvide">The active panel is a plugin panel with <c>provides</c>.</param>
    /// <param name="other">Mode of the opposite panel.</param>
    /// <param name="otherCanReceive">The opposite panel is a plugin panel with <c>accepts</c>.</param>
    /// <param name="move">F6 rather than F5.</param>
    public static PanelTransferRoute Decide(PanelModeKind active, bool activeCanProvide, PanelModeKind other, bool otherCanReceive, bool move)
    {
        if (active == PanelModeKind.Plugin)
        {
            if (move)
            {
                return PanelTransferRoute.RefuseMove;
            }

            if (!activeCanProvide)
            {
                return PanelTransferRoute.BlockedModeActive;
            }

            // Provided files land in the other panel's folder, which only a file list or a preview still shows.
            return other is PanelModeKind.Files or PanelModeKind.Preview ? PanelTransferRoute.ProvideFromPanel : PanelTransferRoute.RefuseNotAccepting;
        }

        if (active != PanelModeKind.Files)
        {
            return PanelTransferRoute.BlockedModeActive;
        }

        return other switch
        {
            PanelModeKind.Files or PanelModeKind.Preview => PanelTransferRoute.OrdinaryCopy,
            PanelModeKind.Plugin when move => PanelTransferRoute.RefuseMove,
            PanelModeKind.Plugin when otherCanReceive => PanelTransferRoute.SendToPanel,
            _ => PanelTransferRoute.RefuseNotAccepting,
        };
    }
}
