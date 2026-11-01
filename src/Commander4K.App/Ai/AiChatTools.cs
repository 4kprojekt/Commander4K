using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Ai;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.Ai;

/// <summary>
/// Functions the in-app chat offers to the model: the app state, folder listings (any VFS path), a text file (only
/// after the user agrees, because its content goes to the provider) and the plugin guide. All read-only.
/// </summary>
public sealed class AiChatTools(AiAppContext context, Func<string, Task<bool>> allowRead)
{
    private const int MaxListing = 300;
    private const int MaxFileChars = 60_000;

    public static IReadOnlyList<AiTool> Definitions { get; } =
    [
        new("get_context", "Current state of Commander4K: both panels (path, mode, cursor, marked entries, listing sample), plugin runtimes and installed plugins. Call it whenever the user refers to 'here', 'this file', the selection or the app.",
            Schema()),
        new("list_directory", "List a folder (local disk, UNC share, sftp://, ftp://, zip://, reg://). Returns up to 300 entries with kind, size and date.",
            Schema(("path", "Folder path as shown in the panel"))),
        new("read_text_file", "Read a text file (up to 60 000 characters). The user is asked for permission first because the content is sent to you.",
            Schema(("path", "Full path of the file"))),
        new("get_plugin_guide", "The complete guide for writing Commander4K plugins (protocol, manifest schema, permissions) and an example plugin. Call it before writing or fixing a plugin.",
            Schema()),
    ];

    /// <summary>Runs one call; errors come back as text for the model rather than exceptions.</summary>
    public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = ParseArguments(call.Arguments);
            return call.Name switch
            {
                "get_context" => context.Describe(),
                "list_directory" => await ListAsync(Required(arguments, "path"), cancellationToken),
                "read_text_file" => await ReadAsync(Required(arguments, "path"), cancellationToken),
                "get_plugin_guide" => context.PluginGuide() + "\n\n" + context.Example() + "\n\n" + Core.Plugins.PluginAuthoring.OutputFormat,
                _ => $"Unknown tool {call.Name}.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
        {
            return "Error: " + ex.Message;
        }
    }

    /// <summary>One line for the transcript, e.g. "🔧 list_directory C:\Repos".</summary>
    public static string Describe(AiToolCall call)
    {
        var arguments = ParseArguments(call.Arguments);
        var detail = arguments.TryGetPropertyValue("path", out var path) ? " " + path : string.Empty;
        return call.Name switch
        {
            "get_context" => Loc.T("🔧 sprawdza stan aplikacji (panele, zaznaczenie, wtyczki)"),
            "list_directory" => Loc.T("🔧 przegląda folder{0}", detail),
            "read_text_file" => Loc.T("🔧 czyta plik{0}", detail),
            "get_plugin_guide" => Loc.T("🔧 czyta przewodnik po wtyczkach"),
            _ => "🔧 " + call.Name,
        };
    }

    private async Task<string> ListAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var reader = context.Host.Router.OpenListing(path, timeout.Token);
        var text = new StringBuilder($"Listing of {path}:\n");
        var count = 0;
        try
        {
            await foreach (var entry in reader.ReadAllAsync(timeout.Token))
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

                text.AppendLine(entry.IsContainer
                    ? $"{entry.Name}/  [{entry.Kind}]"
                    : $"{entry.Name}  {(entry.Size >= 0 ? entry.Size + " B" : string.Empty)}  {entry.LastWriteTime.LocalDateTime:yyyy-MM-dd HH:mm}");
            }
        }
        finally
        {
            // Stops the router's producer (and the provider's enumerator) when we leave early at the cap.
            await timeout.CancelAsync();
        }

        return count == 0 ? $"{path} is empty." : text.ToString();
    }

    private async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!await allowRead(path))
        {
            return "The user did not allow reading this file.";
        }

        await using var stream = await context.Host.Router.OpenReadAsync(path, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxFileChars + 1];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        var text = new string(buffer, 0, Math.Min(read, MaxFileChars));
        if (text.Contains('\0'))
        {
            return $"{path} looks like a binary file; not shown.";
        }

        return read > MaxFileChars ? text + "\n… (file truncated)" : text;
    }

    private static JsonObject ParseArguments(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Required(JsonObject arguments, string name) =>
        arguments[name]?.ToString() is { Length: > 0 } value ? value : throw new ArgumentException($"Missing argument \"{name}\".");

    private static JsonObject Schema(params (string Name, string Description)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, description) in properties)
        {
            props[name] = new JsonObject { ["type"] = "string", ["description"] = description };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Name)]),
        };
    }
}
