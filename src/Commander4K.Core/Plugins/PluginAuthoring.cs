using System.Text;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>One file of a plugin. When <paramref name="Bytes"/> is set the file is binary: written as-is and <paramref name="Content"/> is empty.</summary>
public sealed record GeneratedFile(string Path, string Content, byte[]? Bytes = null);

/// <summary>Size limits for a set of plugin files.</summary>
public sealed record PluginLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)
{
    /// <summary>Files sent in one MCP call, or a catalog download.</summary>
    public static readonly PluginLimits Answer = new(30, 1024 * 1024, 30L * 1024 * 1024);

    /// <summary>A plugin folder with assets (e.g. a bundled PDF viewer).</summary>
    public static readonly PluginLimits Folder = new(500, 8L * 1024 * 1024, 64L * 1024 * 1024);
}

/// <summary>A parsed and validated plugin, ready to be written to the plugins folder.</summary>
public sealed record PluginDraft(PluginManifest Manifest, IReadOnlyList<GeneratedFile> Files, string TargetFolder, bool TargetExists);

/// <summary>
/// Plugin files from an MCP client or the official catalog: reads a plugin folder, validates the manifest in a staging
/// folder before anything lands in <c>plugins/</c>, and installs it. Installed plugins stay disabled until the user
/// approves their permissions, like any other plugin.
/// </summary>
public static class PluginAuthoring
{
    public const int MaxFiles = 30;
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>SDK client file names per runtime; added from the app's copy when the plugin refers to one but omits it.</summary>
    public static readonly IReadOnlyDictionary<string, string> SdkClients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "c4k.py",
        ["node"] = "c4k.js",
        ["powershell"] = "c4k.ps1",
    };

    /// <summary>
    /// Validates the files in a staging folder (paths, sizes, manifest) and returns where they would go. Throws
    /// <see cref="InvalidOperationException"/> with a readable reason.
    /// </summary>
    public static PluginDraft Prepare(IReadOnlyList<GeneratedFile> files, string pluginsRoot, Func<string, string?> sdkClient, PluginLimits? limits = null)
    {
        limits ??= PluginLimits.Answer;
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No files found. Each file needs a line \"File: <path>\" followed by a code block.");
        }

        if (files.Count > limits.MaxFiles)
        {
            throw new InvalidOperationException($"Too many files ({files.Count}, at most {limits.MaxFiles}).");
        }

        long total = 0;
        foreach (var file in files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                throw new InvalidOperationException($"Unsafe file path \"{file.Path}\": only relative paths inside the plugin folder are allowed.");
            }

            var size = file.Bytes?.Length ?? Encoding.UTF8.GetByteCount(file.Content);
            if (size > limits.MaxFileBytes)
            {
                throw new InvalidOperationException($"\"{file.Path}\" is larger than {limits.MaxFileBytes / (1024 * 1024)} MB.");
            }

            total += size;
        }

        if (total > limits.MaxTotalBytes)
        {
            throw new InvalidOperationException($"The files are larger than {limits.MaxTotalBytes / (1024 * 1024)} MB in total.");
        }

        var manifestFile = files.FirstOrDefault(f => string.Equals(Normalize(f.Path), ManifestReader.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("There is no plugin.json in the plugin folder's root.");

        var staging = Path.Combine(Path.GetTempPath(), "c4k-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, ManifestReader.FileName), manifestFile.Content);
            foreach (var file in files)
            {
                WriteFile(Path.Combine(staging, Normalize(file.Path)), file);
            }

            var result = new List<GeneratedFile>(files.Select(f => f with { Path = Normalize(f.Path) }));
            var discovered = ManifestReader.Read(staging);
            if (discovered.Manifest is { Runtime.Type: { } runtime } && SdkClients.TryGetValue(runtime, out var client)
                && !result.Any(f => string.Equals(f.Path, client, StringComparison.OrdinalIgnoreCase))
                && result.Any(f => f.Bytes is null && f.Content.Contains(Path.GetFileNameWithoutExtension(client), StringComparison.Ordinal))
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

        RemoveLeftovers(draft.TargetFolder);
        var temporary = draft.TargetFolder + TemporarySuffix + Guid.NewGuid().ToString("N")[..8];
        foreach (var file in draft.Files)
        {
            WriteFile(Path.Combine(temporary, file.Path), file);
        }

        // Windows briefly keeps freshly written files (Defender, the indexer) and a just-stopped plugin's folder
        // open: renaming then fails with "access denied". Retry, and copy as a last resort.
        Retry(() =>
        {
            if (Directory.Exists(draft.TargetFolder))
            {
                Directory.Delete(draft.TargetFolder, recursive: true);
            }
        });
        try
        {
            Retry(() => Directory.Move(temporary, draft.TargetFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopyTree(temporary, draft.TargetFolder);
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A leftover ".new-…" folder sits in plugins/ too: without its manifest it is never loaded as a second copy.
                try
                {
                    Retry(() => File.Delete(Path.Combine(temporary, ManifestReader.FileName)));
                }
                catch (Exception manifest) when (manifest is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private const string TemporarySuffix = ".new-";

    /// <summary>A folder an install writes into before it becomes the plugin's folder.</summary>
    public static bool IsTemporaryFolder(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Contains(TemporarySuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Temporary folders that an earlier, interrupted install of the same plugin left behind.</summary>
    private static void RemoveLeftovers(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        foreach (var leftover in Directory.EnumerateDirectories(parent, Path.GetFileName(target) + TemporarySuffix + "*"))
        {
            try
            {
                Directory.Delete(leftover, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by something; Discover skips it anyway, the next install tries again.
            }
        }
    }

    /// <summary>Up to 8 attempts over about 3 s for file operations that fail only while something else holds the files.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Retry(() => File.Copy(file, destination, overwrite: true));
        }
    }

    private static void WriteFile(string path, GeneratedFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (file.Bytes is { } bytes)
        {
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            File.WriteAllText(path, file.Content);
        }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".js", ".mjs", ".cjs", ".ts", ".py", ".ps1", ".psm1", ".md", ".txt", ".html", ".css", ".csv", ".xml", ".yml", ".yaml", ".svg",
    };

    /// <summary>
    /// Reads a local plugin folder: known text extensions as UTF-8 text (when valid and free of NUL), everything else as
    /// bytes. Skips <c>.git</c>, <c>__pycache__</c>, <c>node_modules/.cache</c> and links. Blocking; throws
    /// <see cref="InvalidOperationException"/> when a limit is exceeded.
    /// </summary>
    public static IReadOnlyList<GeneratedFile> FromFolder(string folder, PluginLimits limits, CancellationToken ct = default)
    {
        var result = new List<GeneratedFile>();
        long total = 0;
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    var rel = Path.GetRelativePath(folder, sub.FullName).Replace('\\', '/');
                    if (sub.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || sub.Name == "__pycache__"
                        || rel.Equals("node_modules/.cache", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith("/node_modules/.cache", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Push(sub.FullName);
                    continue;
                }

                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = (FileInfo)entry;
                var relative = Path.GetRelativePath(folder, info.FullName);
                if (result.Count + 1 > limits.MaxFiles)
                {
                    throw new InvalidOperationException($"Too many files (more than {limits.MaxFiles}).");
                }

                if (info.Length > limits.MaxFileBytes)
                {
                    throw new InvalidOperationException($"\"{relative}\" is larger than {limits.MaxFileBytes / (1024 * 1024)} MB.");
                }

                total += info.Length;
                if (total > limits.MaxTotalBytes)
                {
                    throw new InvalidOperationException($"The files are larger than {limits.MaxTotalBytes / (1024 * 1024)} MB in total.");
                }

                var bytes = File.ReadAllBytes(info.FullName);
                if (LintedExtensions.Contains(info.Extension))
                {
                    // Code the lint must see: never binary, whatever its encoding.
                    result.Add(new GeneratedFile(relative, DecodeLeniently(bytes)));
                }
                else if (TextExtensions.Contains(info.Extension) && Array.IndexOf(bytes, (byte)0) < 0 && TryDecode(strict, bytes) is { } text)
                {
                    result.Add(new GeneratedFile(relative, text));
                }
                else
                {
                    result.Add(new GeneratedFile(relative, string.Empty, bytes));
                }
            }
        }

        return result;
    }

    private static readonly HashSet<string> LintedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".js", ".mjs", ".cjs", ".ts", ".ps1", ".psm1",
    };

    /// <summary>Honours a UTF-8 / UTF-16 BOM, otherwise UTF-8 with replacement characters.</summary>
    private static string DecodeLeniently(byte[] b) => b switch
    {
        [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(b, 3, b.Length - 3),
        [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(b, 2, b.Length - 2),
        [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2),
        _ => Encoding.UTF8.GetString(b),
    };

    private static string? TryDecode(UTF8Encoding strict, byte[] bytes)
    {
        try
        {
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
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
}
