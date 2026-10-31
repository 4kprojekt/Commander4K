using System.Text;
using System.Text.RegularExpressions;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>One file of a plugin written by an AI assistant.</summary>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>A parsed and validated plugin, ready to be written to the plugins folder.</summary>
public sealed record PluginDraft(PluginManifest Manifest, IReadOnlyList<GeneratedFile> Files, string TargetFolder, bool TargetExists);

/// <summary>
/// Lets AI assistants write plugins: the request prompt (guide + app context + wish, with a strict output format), a
/// parser that finds the files in the answer (Markdown from the in-app chat or code blocks read from a web page) and
/// an installer that validates the manifest in a staging folder before anything lands in <c>plugins/</c>. Installed
/// plugins stay disabled until the user approves their permissions, like any other plugin.
/// </summary>
public static partial class PluginAuthoring
{
    public const int MaxFiles = 30;
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>SDK client file names per runtime; added from the app's copy when the answer refers to one but omits it.</summary>
    public static readonly IReadOnlyDictionary<string, string> SdkClients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "c4k.py",
        ["node"] = "c4k.js",
        ["powershell"] = "c4k.ps1",
    };

    /// <summary>How the assistant must format files so <see cref="ParseFiles"/> finds them (part of every plugin prompt).</summary>
    public const string OutputFormat =
        "Output format (required, the app installs the plugin from your answer):\n" +
        "- For EVERY file write a line `File: <relative path>` (e.g. `File: plugin.json`, `File: main.py`), then the complete file in ONE fenced code block.\n" +
        "- Write all files of the plugin folder: plugin.json, the entry file and any data files. You may skip the SDK client " +
        "(c4k.py / c4k.js / c4k.ps1) — the app adds its own copy.\n" +
        "- No placeholders or \"…\" inside files; paths are relative to the plugin folder, without `..`.\n" +
        "- After the files, explain briefly what the plugin does and which permissions it asks for, in the user's language.";

    /// <summary>The full request for an assistant: guide, app context, example and the user's wish.</summary>
    public static string BuildPrompt(string guide, string context, string? example, string wish)
    {
        var prompt = new StringBuilder()
            .AppendLine("You are writing a plugin for Commander4K, a dual-pane file manager for Windows 11.")
            .AppendLine("Follow the plugin guide below exactly: smallest possible permissions, one folder, and the golden rule — ALL file, folder, process, " +
                "network, system and settings work goes through the SDK client (c4k: read_text/read_bytes/write_text/list_dir/stat/move/remove/run/" +
                "http_request/system_info/storage_get…), never through your language's own I/O (open, fs, subprocess, fetch, Get-Content…), " +
                "and nothing is written to stdout. Code that bypasses the SDK is rejected by the installer.")
            .AppendLine("Answer in the language of the user's wish.")
            .AppendLine()
            .AppendLine(OutputFormat)
            .AppendLine()
            .AppendLine("<plugin-guide>")
            .AppendLine(guide.Trim())
            .AppendLine("</plugin-guide>")
            .AppendLine()
            .AppendLine("<app-context>")
            .AppendLine(context.Trim())
            .AppendLine("</app-context>");
        if (!string.IsNullOrWhiteSpace(example))
        {
            prompt.AppendLine().AppendLine("<example-plugin>").AppendLine(example.Trim()).AppendLine("</example-plugin>");
        }

        return prompt.AppendLine().AppendLine("<user-wish>").AppendLine(wish.Trim()).AppendLine("</user-wish>").ToString();
    }

    /// <summary>Files in a Markdown answer: <c>File: path</c> lines (also <c>Plik:</c>, headings, bold) each followed by a fenced block, or a fence carrying the path.</summary>
    public static IReadOnlyList<GeneratedFile> ParseFiles(string markdown)
    {
        var files = new List<GeneratedFile>();
        string? pending = null;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FenceRegex().Match(line) is { Success: true } fence)
            {
                var marker = fence.Groups["fence"].Value;
                var body = new StringBuilder();
                var j = i + 1;
                for (; j < lines.Length && !lines[j].TrimStart().StartsWith(marker, StringComparison.Ordinal); j++)
                {
                    body.Append(lines[j]).Append('\n');
                }

                var path = PathFromInfo(fence.Groups["info"].Value) ?? pending;
                if (path is not null)
                {
                    files.Add(new GeneratedFile(path, body.ToString()));
                }

                pending = null;
                i = j;
                continue;
            }

            if (FileLabel(line) is { } label)
            {
                pending = label;
            }
        }

        return Deduplicate(files);
    }

    /// <summary>
    /// Validates the files in a staging folder (paths, sizes, manifest) and returns where they would go. Throws
    /// <see cref="InvalidOperationException"/> with a readable reason.
    /// </summary>
    public static PluginDraft Prepare(IReadOnlyList<GeneratedFile> files, string pluginsRoot, Func<string, string?> sdkClient)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No files found. Each file needs a line \"File: <path>\" followed by a code block.");
        }

        if (files.Count > MaxFiles)
        {
            throw new InvalidOperationException($"Too many files ({files.Count}, at most {MaxFiles}).");
        }

        foreach (var file in files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                throw new InvalidOperationException($"Unsafe file path \"{file.Path}\": only relative paths inside the plugin folder are allowed.");
            }

            if (Encoding.UTF8.GetByteCount(file.Content) > MaxFileBytes)
            {
                throw new InvalidOperationException($"\"{file.Path}\" is larger than 1 MB.");
            }
        }

        var manifestFile = files.FirstOrDefault(f => string.Equals(Normalize(f.Path), ManifestReader.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The answer has no plugin.json in the plugin folder's root.");

        var staging = Path.Combine(Path.GetTempPath(), "c4k-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, ManifestReader.FileName), manifestFile.Content);
            foreach (var file in files)
            {
                var path = Path.Combine(staging, Normalize(file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }

            var result = new List<GeneratedFile>(files.Select(f => f with { Path = Normalize(f.Path) }));
            var discovered = ManifestReader.Read(staging);
            if (discovered.Manifest is { Runtime.Type: { } runtime } && SdkClients.TryGetValue(runtime, out var client)
                && !result.Any(f => string.Equals(f.Path, client, StringComparison.OrdinalIgnoreCase))
                && result.Any(f => f.Content.Contains(Path.GetFileNameWithoutExtension(client), StringComparison.Ordinal))
                && sdkClient(runtime) is { } sdk)
            {
                File.WriteAllText(Path.Combine(staging, client), sdk);
                result.Add(new GeneratedFile(client, sdk));
                discovered = ManifestReader.Read(staging);
            }

            if (discovered.Manifest is null || discovered.Errors.Count > 0)
            {
                throw new InvalidOperationException("plugin.json is not valid: " + string.Join(" ", discovered.Errors));
            }

            var target = Path.Combine(pluginsRoot, discovered.Manifest.Id);
            return new PluginDraft(discovered.Manifest, result, target, Directory.Exists(target));
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Writes the draft; an existing folder of the same plugin is replaced only with <paramref name="overwrite"/>.</summary>
    public static void Install(PluginDraft draft, bool overwrite)
    {
        if (draft.TargetExists && !overwrite)
        {
            throw new InvalidOperationException($"\"{draft.TargetFolder}\" already exists.");
        }

        var temporary = draft.TargetFolder + ".new-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var file in draft.Files)
        {
            var path = Path.Combine(temporary, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }

        if (Directory.Exists(draft.TargetFolder))
        {
            Directory.Delete(draft.TargetFolder, recursive: true);
        }

        Directory.Move(temporary, draft.TargetFolder);
    }

    public static bool IsSafeRelativePath(string path)
    {
        var normalized = Normalize(path);
        return normalized.Length is > 0 and < 200
            && !path.EndsWith(' ')
            && !Path.IsPathRooted(normalized)
            && !normalized.Contains(':')
            && normalized.Split(Path.DirectorySeparatorChar).All(IsSafeSegment);
    }

    /// <summary>The segments of a relative path, normalised exactly as <see cref="IsSafeRelativePath"/> does.</summary>
    public static string[] SplitRelativePath(string path) => Normalize(path).Split(Path.DirectorySeparatorChar);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsSafeSegment(string part)
    {
        if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part[^1] is '.' or ' ')
        {
            return false;
        }

        var dot = part.IndexOf('.');
        return !ReservedDeviceNames.Contains((dot < 0 ? part : part[..dot]).TrimEnd());
    }

    /// <summary>Platform separators, without a leading <c>./</c>; <c>..</c> and rooted paths are left for <see cref="IsSafeRelativePath"/> to reject.</summary>
    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? FileLabel(string line) =>
        FileLabelRegex().Match(line) is { Success: true } match ? match.Groups["path"].Value.Trim() : null;

    private static string? PathFromInfo(string info) =>
        InfoPathRegex().Match(info) is { Success: true } match ? match.Groups["path"].Value : null;

    /// <summary>The last version of each path wins (assistants often repeat a corrected file).</summary>
    private static List<GeneratedFile> Deduplicate(List<GeneratedFile> files) =>
        [.. files.GroupBy(f => f.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Select(g => g.Last())];

    [GeneratedRegex(@"^\s*(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex FenceRegex();

    // "File: main.py", "**File:** `main.py`", "### Plik: plugin.json", "File `main.py`:".
    [GeneratedRegex(@"^\s*(?:#+\s*)?(?:\d+\.\s*)?(?:\*\*|__)?(?:File|Plik|Filename|Nazwa pliku)(?:\*\*|__)?\s*:?\s*(?:\*\*|__)?\s*:?\s*`?(?<path>[\w\-./\\]+\.[\w]+|[\w\-./\\]*plugin\.json)`?\s*:?\s*(?:\*\*|__)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLabelRegex();

    [GeneratedRegex(@"(?:title|file|path|filename)\s*=\s*""?(?<path>[\w\-./\\]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex InfoPathRegex();
}
