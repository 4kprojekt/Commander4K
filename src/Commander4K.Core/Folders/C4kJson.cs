using System.Text.Json;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>JSON and path helpers shared by <c>c4k.dir</c> (<see cref="FolderConfig"/>) and <c>*.c4k</c> items (<see cref="C4kItem"/>).</summary>
internal static class C4kJson
{
    /// <summary><c>"10s"</c>, <c>"5m"</c>, <c>"1h"</c>; anything under 2 s becomes 2 s so a dashboard cannot hammer a server.</summary>
    public static TimeSpan? ParseRefresh(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length < 2 || !int.TryParse(text.AsSpan(0, text.Length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            throw new FormatException($"\"refresh\" '{text}' must look like 10s, 5m or 1h.");
        }

        var seconds = text[^1] switch
        {
            's' => (double)amount,
            'm' => amount * 60.0,
            'h' => amount * 3600.0,
            _ => throw new FormatException($"\"refresh\" '{text}' must look like 10s, 5m or 1h."),
        };
        if (seconds > MaxRefresh.TotalSeconds)
        {
            throw new FormatException($"\"refresh\" '{text}' is longer than 24h.");
        }

        var interval = TimeSpan.FromSeconds(seconds);
        return interval < MinRefresh ? MinRefresh : interval;
    }

    private static readonly TimeSpan MinRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRefresh = TimeSpan.FromHours(24);

    public static ApiRequest ParseApi(JsonElement api)
    {
        if (api.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("\"api\" must be an object.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (api.TryGetProperty("headers", out var h))
        {
            if (h.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("\"headers\" must be an object of strings.");
            }

            foreach (var header in h.EnumerateObject())
            {
                headers[header.Name] = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString()! : throw new FormatException($"Header '{header.Name}' must be a string.");
            }
        }

        var fields = new List<ApiField>();
        if (api.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in f.EnumerateArray())
            {
                fields.Add(new ApiField(
                    String(field, "path") ?? throw new FormatException("Every API field needs a \"path\"."),
                    String(field, "label"),
                    String(field, "as") switch
                    {
                        null or "value" => ApiFieldKind.Value,
                        "chart" => ApiFieldKind.Chart,
                        "status" => ApiFieldKind.Status,
                        var other => throw new FormatException($"Unknown field \"as\" '{other}' (value, chart, status)."),
                    },
                    String(field, "unit"),
                    field.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.Array ? [.. ok.EnumerateArray().Select(o => o.ToString())] : []));
            }
        }

        return new ApiRequest(
            String(api, "url") ?? throw new FormatException("\"api\" needs a \"url\"."),
            (String(api, "method") ?? "GET").ToUpperInvariant(),
            headers,
            String(api, "body"),
            String(api, "contentType"),
            fields);
    }

    /// <summary><c>"run": "script.ps1"</c> or <c>{ "command": "pwsh", "args": ["-File", "build.ps1"], "cwd": "src", "hidden": false }</c>.</summary>
    public static FolderRun ParseRun(JsonElement run) => run.ValueKind switch
    {
        JsonValueKind.String => new FolderRun(run.GetString() ?? string.Empty, []),
        JsonValueKind.Object => new FolderRun(
            String(run, "command") ?? throw new FormatException("\"run\" needs a \"command\"."),
            run.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array ? [.. args.EnumerateArray().Select(a => a.ToString())] : [],
            String(run, "cwd"),
            run.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True),
        _ => throw new FormatException("\"run\" must be a command or an object."),
    };

    public static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    /// <summary>A child of a VFS folder: backslashes on disks and shares, slashes elsewhere (SFTP, archives).</summary>
    public static string ChildPath(string folder, string name)
    {
        var backslash = VfsPath.GetScheme(folder) is VfsPath.FileScheme or VfsPath.SmbScheme && !folder.Contains("://", StringComparison.Ordinal);
        var separator = backslash ? '\\' : '/';
        return folder.EndsWith('/') || folder.EndsWith('\\') ? folder + name : folder + separator + name;
    }

    /// <summary>A path from the file, resolved against the folder: absolute paths and URIs stay, <c>~</c> is the user's profile.</summary>
    public static string Resolve(string folder, string path)
    {
        if (path.StartsWith('~'))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/', '\\').Replace('/', '\\'));
        }

        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal) || Path.IsPathRooted(path) && path.Length > 1 && path[1] == ':')
        {
            return path;
        }

        var result = folder;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            result = part switch
            {
                "." => result,
                ".." => ParentOf(result),
                _ => ChildPath(result, part),
            };
        }

        return result;
    }

    public static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut <= 0 || trimmed[..cut].EndsWith(':') && !trimmed[..cut].Contains("://", StringComparison.Ordinal) ? trimmed[..(cut + 1)] : trimmed[..cut];
    }
}
