using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Mcp;

/// <summary>
/// File tools over the VFS (any scheme: disks, shares, SFTP, archives). Every path comes from a declared path
/// argument, so the dispatcher has already checked and normalized it. Messages are for the model, hence English.
/// </summary>
public static class McpFileTools
{
    public const int MaxListing = 500;
    public const int MaxTextChars = 60_000;
    public const int MaxBase64Bytes = 262_144;
    public const int MaxWriteBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(20);

    public static IReadOnlyList<McpTool> Create(VfsRouter router)
    {
        var path = ("path", "string", "Full path (disk path, \\\\server\\share, sftp://..., zip://...).", true);
        return
        [
            new McpTool(
                new McpToolDefinition(
                    "list_directory",
                    "Lists a folder: one line per entry with kind, size and modification time (at most 500 entries).",
                    McpSchema.Object(path),
                    McpToolMode.Allow,
                    PathArguments: ["path"]),
                (call, ct) => Guard(() => ListAsync(router, call.Required("path"), ct)),
                args => "list_directory " + Str(args, "path")),
            new McpTool(
                new McpToolDefinition(
                    "stat",
                    "Describes one file or folder: kind, size and modification time.",
                    McpSchema.Object(path),
                    McpToolMode.Allow,
                    PathArguments: ["path"]),
                (call, ct) => Guard(() => StatAsync(router, call.Required("path"), ct)),
                args => "stat " + Str(args, "path")),
            new McpTool(
                new McpToolDefinition(
                    "read_file",
                    "Reads a file. encoding \"text\" (default) returns UTF-8 text, at most 60000 characters. encoding \"base64\" "
                    + "returns at most 262144 bytes starting at offset (bytes) and says \"next offset: N\" when there is more.",
                    McpSchema.Object(
                        path,
                        ("encoding", "string", "\"text\" (default) or \"base64\".", false),
                        ("offset", "integer", "Byte offset to start from (default 0).", false)),
                    McpToolMode.Ask,
                    PathArguments: ["path"]),
                (call, ct) => Guard(() => ReadAsync(router, call, ct)),
                args => "read_file " + Str(args, "path")),
            new McpTool(
                new McpToolDefinition(
                    "write_file",
                    "Writes a file, replacing it if it exists (overwrite). encoding \"text\" (default, UTF-8) or \"base64\". At most 8 MB.",
                    McpSchema.Object(
                        path,
                        ("content", "string", "File content.", true),
                        ("encoding", "string", "\"text\" (default) or \"base64\".", false)),
                    McpToolMode.Ask,
                    PathArguments: ["path"],
                    Writes: true),
                (call, ct) => Guard(() => WriteAsync(router, call, ct)),
                WriteSummary),
            new McpTool(
                new McpToolDefinition(
                    "make_directory",
                    "Creates a folder (including missing parents where the file system supports it).",
                    McpSchema.Object(path),
                    McpToolMode.Ask,
                    PathArguments: ["path"],
                    Writes: true),
                (call, ct) => Guard(() => MakeDirectoryAsync(router, call.Required("path"), ct)),
                args => "make_directory " + Str(args, "path")),
            new McpTool(
                new McpToolDefinition(
                    "move",
                    "Moves or renames a file or folder. Fails if the destination already exists (no overwrite) "
                    + "or if the two paths are on different file systems.",
                    McpSchema.Object(
                        ("source", "string", "Existing path.", true),
                        ("destination", "string", "New path.", true)),
                    McpToolMode.Ask,
                    AlwaysAsk: true,
                    PathArguments: ["source", "destination"],
                    Writes: true),
                (call, ct) => Guard(() => MoveAsync(router, call.Required("source"), call.Required("destination"), ct)),
                args => $"move {Str(args, "source")} -> {Str(args, "destination")}"),
            new McpTool(
                new McpToolDefinition(
                    "delete",
                    "Deletes a file or a folder with everything inside it (recursive, no recycle bin).",
                    McpSchema.Object(path),
                    McpToolMode.Ask,
                    AlwaysAsk: true,
                    PathArguments: ["path"],
                    Writes: true),
                (call, ct) => Guard(() => DeleteAsync(router, call.Required("path"), ct)),
                args => "delete " + Str(args, "path")),
        ];
    }

    private static string Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static string WriteSummary(JsonObject args)
    {
        var content = Str(args, "content");
        var base64 = string.Equals(Str(args, "encoding"), "base64", StringComparison.OrdinalIgnoreCase);
        var bytes = base64 ? (long)content.Length * 3 / 4 : Encoding.UTF8.GetByteCount(content);
        return $"write_file {Str(args, "path")} ({bytes} B)";
    }

    /// <summary>Expected failures become tool errors; anything else is left to the dispatcher and server.</summary>
    private static async Task<McpToolResult> Guard(Func<Task<McpToolResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or FormatException or TimeoutException or InvalidOperationException)
        {
            return McpToolResult.Error(ex.Message);
        }
    }

    private static async Task<McpToolResult> ListAsync(VfsRouter router, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ListingTimeout);
        var text = new StringBuilder($"Listing of {path}:\n");
        var count = 0;
        try
        {
            var reader = router.OpenListing(path, timeout.Token);
            await foreach (var entry in reader.ReadAllAsync(timeout.Token).ConfigureAwait(false))
            {
                if (entry.Kind == VfsEntryKind.ParentLink)
                {
                    continue;
                }

                if (++count > MaxListing)
                {
                    text.AppendLine("… (more entries not shown)");
                    break;
                }

                text.AppendLine(Describe(entry));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpToolResult.Error($"Listing {path} timed out after {ListingTimeout.TotalSeconds:0} s.");
        }

        return new McpToolResult(count == 0 ? $"{path} is empty." : text.ToString());
    }

    private static string Describe(VfsEntry entry)
    {
        var size = entry.Size >= 0 ? $"{entry.Size} B" : string.Empty;
        var time = entry.LastWriteTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        return entry.IsContainer
            ? $"{entry.Name}/  [{entry.Kind}]  {time}"
            : $"{entry.Name}  [{entry.Kind}]  {size}  {time}";
    }

    private static async Task<McpToolResult> StatAsync(VfsRouter router, string path, CancellationToken ct)
    {
        var entry = await router.GetEntryAsync(path, ct).ConfigureAwait(false);
        return entry is null
            ? McpToolResult.Error($"Not found: {path}")
            : new McpToolResult($"{path}\n{Describe(entry.Value)}");
    }

    private static async Task<McpToolResult> ReadAsync(VfsRouter router, McpCall call, CancellationToken ct)
    {
        var path = call.Required("path");
        var base64 = ParseEncoding(call.Optional("encoding"), out var encodingError);
        if (encodingError is not null)
        {
            return McpToolResult.Error(encodingError);
        }

        long offset = 0;
        if (call.Optional("offset") is { } offsetText && (!long.TryParse(offsetText, out offset) || offset < 0))
        {
            return McpToolResult.Error("offset must be a non-negative integer (bytes).");
        }

        await using var stream = await router.OpenReadAsync(path, ct).ConfigureAwait(false);
        await SkipAsync(stream, offset, ct).ConfigureAwait(false);
        return base64
            ? await ReadBase64Async(stream, offset, ct).ConfigureAwait(false)
            : await ReadTextAsync(stream, ct).ConfigureAwait(false);
    }

    private static bool ParseEncoding(string? value, out string? error)
    {
        error = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "text":
                return false;
            case "base64":
                return true;
            default:
                error = "encoding must be \"text\" or \"base64\".";
                return false;
        }
    }

    private static async Task SkipAsync(Stream stream, long offset, CancellationToken ct)
    {
        if (offset == 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            return;
        }

        var buffer = new byte[8192];
        while (offset > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            offset -= read;
        }
    }

    private static async Task<McpToolResult> ReadBase64Async(Stream stream, long offset, CancellationToken ct)
    {
        // One byte beyond the limit tells whether there is more, without reading the rest of the file.
        var buffer = new byte[MaxBase64Bytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var more = total > MaxBase64Bytes;
        var text = Convert.ToBase64String(buffer, 0, Math.Min(total, MaxBase64Bytes));
        return new McpToolResult(more ? $"{text}\nnext offset: {offset + MaxBase64Bytes}" : text);
    }

    private static async Task<McpToolResult> ReadTextAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxTextChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        var text = new string(buffer, 0, Math.Min(read, MaxTextChars));
        if (text.Contains('\0'))
        {
            return McpToolResult.Error("This looks like a binary file. Use encoding: \"base64\" to read it.");
        }

        return new McpToolResult(read > MaxTextChars ? text + "\n… (file truncated)" : text);
    }

    private static async Task<McpToolResult> WriteAsync(VfsRouter router, McpCall call, CancellationToken ct)
    {
        var path = call.Required("path");
        var content = call.Required("content");
        var base64 = ParseEncoding(call.Optional("encoding"), out var encodingError);
        if (encodingError is not null)
        {
            return McpToolResult.Error(encodingError);
        }

        var tooLarge = McpToolResult.Error($"Content is larger than {MaxWriteBytes / (1024 * 1024)} MB.");
        byte[] bytes;
        if (base64)
        {
            if ((long)content.Length * 3 / 4 > MaxWriteBytes + 3)
            {
                return tooLarge;
            }

            bytes = Convert.FromBase64String(content);
        }
        else
        {
            if (content.Length > MaxWriteBytes || Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
            {
                return tooLarge;
            }

            bytes = new UTF8Encoding(false).GetBytes(content);
        }

        if (bytes.Length > MaxWriteBytes)
        {
            return tooLarge;
        }

        await using (var stream = await router.OpenWriteAsync(path, overwrite: true, ct).ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        return new McpToolResult($"Wrote {bytes.Length} bytes to {path}.");
    }

    private static async Task<McpToolResult> MakeDirectoryAsync(VfsRouter router, string path, CancellationToken ct)
    {
        await router.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
        return new McpToolResult($"Created folder {path}.");
    }

    private static async Task<McpToolResult> MoveAsync(VfsRouter router, string source, string destination, CancellationToken ct)
    {
        await router.MoveAsync(source, destination, overwrite: false, ct).ConfigureAwait(false);
        return new McpToolResult($"Moved {source} to {destination}.");
    }

    private static async Task<McpToolResult> DeleteAsync(VfsRouter router, string path, CancellationToken ct)
    {
        await router.DeleteAsync(path, recursive: true, ct).ConfigureAwait(false);
        return new McpToolResult($"Deleted {path}.");
    }
}
