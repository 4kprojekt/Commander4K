using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Folders;

public enum FolderConfigWrite
{
    Written,
    AlreadyExists,
    Failed,
}

/// <summary>Creates and edits <c>.c4k</c> item files.</summary>
public static class C4kWriter
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/c4k.schema.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary><c>mail.c4k</c>, then <c>mail 2.c4k</c>, <c>mail 3.c4k</c>…; illegal characters are dropped.</summary>
    public static string UniqueFileName(string baseName, Func<string, bool> exists)
    {
        var clean = new string([.. baseName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]).Trim();
        if (clean.Length == 0)
        {
            clean = "folder";
        }

        var name = clean + C4kReader.Extension;
        for (var n = 2; exists(name); n++)
        {
            name = $"{clean} {n}{C4kReader.Extension}";
        }

        return name;
    }

    public static string Create(JsonObject item)
    {
        var root = new JsonObject { ["$schema"] = SchemaUrl };
        foreach (var (key, value) in item.ToArray())
        {
            if (key != "$schema")
            {
                root[key] = value?.DeepClone();
            }
        }

        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>The item for a suggested view: <c>{ view }</c> or <c>{ plugin, preview?, options? }</c>.</summary>
    public static JsonObject ItemFor(PluginSdk.FolderSuggestionView view)
    {
        if (view.Plugin is null)
        {
            return new JsonObject { ["view"] = view.View ?? FolderViewIds.Files };
        }

        var item = new JsonObject { ["plugin"] = view.Plugin };
        if (view.Preview is not null)
        {
            item["preview"] = view.Preview;
        }

        if (view.OptionsJson is not null)
        {
            item["options"] = JsonNode.Parse(view.OptionsJson);
        }

        return item;
    }

    /// <exception cref="JsonException">The file is not a JSON object.</exception>
    public static string SetOptions(string existing, string options)
    {
        var root = Parse(existing);
        root["options"] = (JsonNode.Parse(options) as JsonObject ?? new JsonObject()).DeepClone();
        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary><see cref="TileSize.Small"/> removes the key, since it is the default.</summary>
    /// <exception cref="JsonException">The file is not a JSON object.</exception>
    public static string SetSize(string existing, TileSize size)
    {
        var root = Parse(existing);
        if (size == TileSize.Small)
        {
            root.Remove("size");
        }
        else
        {
            root["size"] = TileSizes.Name(size);
        }

        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>Comments are dropped when the file is written again, so the caller warns first.</summary>
    public static bool HasComments(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// Writes a new item file in <paramref name="folder"/> but never replaces one. A hung share is abandoned after
    /// <paramref name="timeout"/>; every failure is reported, never thrown.
    /// </summary>
    public static async Task<(FolderConfigWrite Result, string? Error, string? Path)> CreateNewAsync(string folder, string baseName, string json, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => WriteNewAsync(folder, baseName, json), CancellationToken.None).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return (FolderConfigWrite.Failed, ex.Message, null);
        }
    }

    private static async Task<(FolderConfigWrite Result, string? Error, string? Path)> WriteNewAsync(string folder, string baseName, string json)
    {
        var path = C4kJson.ChildPath(folder, UniqueFileName(baseName, name => File.Exists(C4kJson.ChildPath(folder, name))));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                return (FolderConfigWrite.Written, null, path);
            }
            catch (IOException) when (attempt == 0 && File.Exists(path))
            {
                // Lost a race for the name: take the next free one once.
                var taken = path;
                path = C4kJson.ChildPath(folder, UniqueFileName(baseName, name => File.Exists(C4kJson.ChildPath(folder, name)) || C4kJson.ChildPath(folder, name) == taken));
            }
            catch (Exception ex)
            {
                return (FolderConfigWrite.Failed, ex.Message, null);
            }
        }
    }

    private static JsonObject Parse(string json) =>
        JsonNode.Parse(json, documentOptions: Lenient) as JsonObject ?? throw new JsonException("A .c4k file must contain a JSON object.");
}
