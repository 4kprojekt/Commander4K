namespace Commander4K.PluginSdk;

/// <summary>What a preview shows; the host renders each kind (and offers editing where <c>CanEdit</c> is set).</summary>
public abstract record PreviewContent;

/// <summary>Markdown rendered with headings, lists, tables, code and quotes; editable side by side when <paramref name="CanEdit"/>.</summary>
public sealed record MarkdownPreview(string Markdown, bool CanEdit) : PreviewContent;

/// <param name="FileName">Name of the file shown, so the host can pick a syntax for colouring; <c>null</c> = plain text.</param>
public sealed record TextPreview(string Text, bool CanEdit, bool Truncated = false, string? FileName = null) : PreviewContent;

public sealed record ImagePreview(ReadOnlyMemory<byte> EncodedImage) : PreviewContent;

public sealed record HexPreview(ReadOnlyMemory<byte> Data, long TotalLength) : PreviewContent;

/// <param name="Size">Bytes, or <c>null</c> while a folder's size is still being computed.</param>
public sealed record FolderPreviewItem(string Name, string Path, bool IsDirectory, long? Size);

/// <summary>Contents of a folder with sizes; previews push updated instances through progress as sizes arrive.</summary>
public sealed record FolderPreview(string Path, IReadOnlyList<FolderPreviewItem> Items, long TotalBytes, long Files, long Folders, bool Complete) : PreviewContent;

/// <summary>One tile of a <see cref="TilesPreview"/>.</summary>
/// <param name="Icon">An emoji or a short text shown when there is no image.</param>
/// <param name="Image">Encoded image (PNG, JPEG…) shown on the tile.</param>
/// <param name="Target">
/// What a click does: a path (opened like Enter), an <c>http(s)</c> URL (the browser panel), another URI such as
/// <c>steam://rungameid/570</c> (opened by Windows after the user confirms the scheme once), or a <c>c4k:</c> link
/// (<c>c4k:install/&lt;plugin id&gt;</c>, <c>c4k:run/&lt;tile&gt;</c> for a <c>c4k.dir</c> script). <c>null</c> = not clickable.
/// </param>
public sealed record PreviewTile(string Title, string? Subtitle = null, string? Icon = null, ReadOnlyMemory<byte>? Image = null, string? Target = null);

/// <summary>A grid of tiles: a gallery, a dashboard, a game library…</summary>
/// <param name="TileWidth">Tile width in pixels (images keep their aspect ratio).</param>
public sealed record TilesPreview(string Title, IReadOnlyList<PreviewTile> Tiles, string? Description = null, double TileWidth = 200) : PreviewContent;

/// <summary>A chart as the whole preview.</summary>
public sealed record ChartPreview(string Title, ChartData Chart, string? Description = null) : PreviewContent;

/// <summary>Plain message (nothing to preview, access denied...).</summary>
public sealed record MessagePreview(string Message) : PreviewContent;

/// <summary>A button or link of an interactive preview.</summary>
/// <param name="Target">Like a card action target, plus <c>c4k:plugin/&lt;action&gt;[?k=v&amp;…]</c> which calls <see cref="IInteractivePreviewPlugin.InvokeActionAsync"/>.</param>
/// <param name="Icon">One of the host's icons (<see cref="PreviewIconNames"/>); unknown names are ignored.</param>
/// <param name="Style"><c>primary</c> (the main action), <c>icon</c> (icon only, the label becomes its tooltip) or <c>null</c>.</param>
public sealed record PreviewAction(string Label, string Target, string? Shortcut = null, string? Icon = null, string? Style = null);

/// <summary>Icon names a preview may use on actions, list tabs and list rows.</summary>
public static class PreviewIconNames
{
    public static readonly IReadOnlyList<string> All =
        ["mail", "inbox", "sent", "drafts", "trash", "archive", "junk", "folder", "reply", "replyAll", "forward", "delete", "flag", "unread", "move", "attachment", "new", "refresh", "send", "edit", "download"];
}

/// <summary>One row of a <see cref="ListPreview"/>: a file whose own preview is shown when the row is selected.</summary>
/// <param name="Meta">Short text on the right of the first line (a time, a size).</param>
/// <param name="Group">Rows with the same group follow one heading ("Dziś", "Wczoraj").</param>
/// <param name="Tags">Matched by <see cref="ListPreviewFilter"/>s. <c>unread</c> (bold, a dot), <c>flagged</c> and <c>attachment</c> (icons) also change the row.</param>
public sealed record ListPreviewItem(string Path, string Title, string? Subtitle = null, string? Snippet = null, string? Meta = null, string? Group = null, IReadOnlyList<string>? Tags = null);

/// <summary>A tab above the list (a mail folder…); its target runs like an action, so the plugin answers with the other list.</summary>
public sealed record ListPreviewTab(string Label, string Target, int? Count = null, bool Selected = false, string? Icon = null);

/// <summary>A filter of the segmented switch above the list; an empty <paramref name="Tag"/> shows every row.</summary>
public sealed record ListPreviewFilter(string Tag, string Label);

/// <summary>
/// The files of <paramref name="Folder"/> as a list (messages, tickets…) with the preview of the selected one: under the
/// list, or in the opposite panel when that panel shows the quick view. Selecting a row moves the panel's cursor onto the
/// file, so the preview is the one any plugin gives that file.
/// </summary>
/// <param name="Selected">Path of the row selected first; otherwise the panel's cursor, otherwise the first row.</param>
public sealed record ListPreview(
    string Folder,
    IReadOnlyList<ListPreviewItem> Items,
    string? Title = null,
    IReadOnlyList<ListPreviewTab>? Tabs = null,
    IReadOnlyList<ListPreviewFilter>? Filters = null,
    string? SearchPlaceholder = null,
    string? Status = null,
    string? StatusRight = null,
    string? Selected = null,
    string? Empty = null,
    string? Badge = null) : PreviewContent;

/// <param name="Target">What a click does (a <c>c4k:plugin/…</c> action saving it, a path…), or <c>null</c>.</param>
public sealed record MailPreviewAttachment(string Name, string? Size = null, string? Target = null);

/// <summary>A message to read: subject, sender, attachments, the text (Tekst) and optionally the source (Źródło), and a quick reply.</summary>
/// <param name="QuickReplyTarget">A <c>c4k:plugin/…</c> action that receives the typed text as the form value <c>text</c>; <c>null</c> = no quick reply.</param>
/// <param name="Badge">Short text on the right of the path bar (security checks, a folder).</param>
public sealed record MailPreview(
    string Subject,
    string From,
    string? FromAddress,
    string? To,
    string? Date,
    IReadOnlyList<MailPreviewAttachment> Attachments,
    string Body,
    string? Source = null,
    string? Folder = null,
    string? QuickReplyTarget = null,
    string? QuickReplyPlaceholder = null,
    string? QuickReplyFrom = null,
    string? Status = null,
    string? Badge = null,
    string? Note = null) : PreviewContent;

/// <summary>Another preview with a row of actions under it.</summary>
public sealed record ActionsPreview(PreviewContent Content, IReadOnlyList<PreviewAction> Actions) : PreviewContent;

/// <summary>
/// The plugin renamed or moved the entry it shows (a message marked read), or the view is now about another entry (the
/// draft of a reply): the host follows <paramref name="Entry"/> for later renders, actions and form edits.
/// </summary>
public sealed record MovedPreview(PreviewContent Content, VfsEntry Entry) : PreviewContent;

public sealed record FormField(string Id, string Label, string Value, bool Multiline = false, bool Password = false);

public sealed record FormFile(string Name, string Path, long Size);

/// <summary>Editable fields, attached files and buttons (composing a message…).</summary>
public sealed record FormPreview(
    string Title,
    IReadOnlyList<FormField> Fields,
    IReadOnlyList<FormFile> Files,
    IReadOnlyList<PreviewAction> Buttons,
    string? Message = null,
    bool MessageIsError = false) : PreviewContent;

/// <param name="FormValues">Current field values when the action comes from a <see cref="FormPreview"/>.</param>
public sealed record PreviewActionRequest(
    VfsEntry Entry,
    string? OptionsJson,
    string Action,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyDictionary<string, string>? FormValues);

/// <summary>A preview that reacts to its <see cref="PreviewAction"/> buttons and form edits.</summary>
public interface IInteractivePreviewPlugin : IPreviewPlugin
{
    /// <returns>New content to show, or <c>null</c> to render the entry again.</returns>
    Task<PreviewContent?> InvokeActionAsync(PreviewActionRequest request, IPluginHost host, CancellationToken cancellationToken);

    Task FormChangedAsync(VfsEntry entry, string? optionsJson, IReadOnlyDictionary<string, string> values, IPluginHost host, CancellationToken cancellationToken);
}

/// <summary>Parses <c>c4k:plugin/&lt;action&gt;?k=v&amp;…</c> targets.</summary>
public static class PreviewActionTargets
{
    public const string PluginPrefix = "c4k:plugin/";

    public static bool TryParse(string target, out string action, out IReadOnlyDictionary<string, string> arguments)
    {
        action = string.Empty;
        arguments = new Dictionary<string, string>();
        if (target is null || !target.StartsWith(PluginPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = target[PluginPrefix.Length..];
        var query = rest.IndexOf('?');
        var name = query < 0 ? rest : rest[..query];
        if (name.Length == 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query >= 0)
        {
            foreach (var pair in rest[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                values[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        action = Uri.UnescapeDataString(name);
        arguments = values;
        return true;
    }
}
/// <summary>An HTML page from the plugin folder, shown in the browser engine.</summary>
/// <param name="RootFolder">Absolute folder (<c>&lt;plugin folder&gt;/&lt;webRoot&gt;</c>) the page and its files are served from.</param>
/// <param name="Page">Path of the page inside <paramref name="RootFolder"/>, with <c>/</c> separators.</param>
/// <param name="StateJson">Raw JSON of the result's <c>state</c>, or null.</param>
public sealed record WebPreview(string PluginId, string RootFolder, string Page, string? StateJson) : PreviewContent;

/// <summary>
/// Quick view (Ctrl+Q / F2 ▸ Podgląd) for the entry under the cursor. The host picks the highest-priority plugin whose
/// <see cref="CanPreview"/> accepts the entry, cancels the load as soon as the cursor or folder changes, and saves edits
/// through <see cref="IPluginHost.WriteTextAsync"/> (so editing needs <see cref="PluginPermissions.FileWrite"/>).
/// </summary>
public interface IPreviewPlugin : IPlugin
{
    int Priority { get; }

    bool CanPreview(VfsEntry entry);

    /// <param name="progress">Intermediate states for slow previews (folder sizes filling in).</param>
    Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken);
}

/// <summary>Resolves the same highest-priority preview plugin wherever the app needs to preview an entry.</summary>
public static class PreviewPluginResolver
{
    public static IPreviewPlugin? Resolve(IEnumerable<IPreviewPlugin> plugins, VfsEntry entry) =>
        plugins.Where(plugin => plugin.CanPreview(entry)).OrderByDescending(plugin => plugin.Priority).FirstOrDefault();
}

public readonly record struct DirectorySizeInfo(long Bytes, long Files, long Folders, int Inaccessible);
