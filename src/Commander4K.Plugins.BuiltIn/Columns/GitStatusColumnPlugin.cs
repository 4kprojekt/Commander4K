using System.Collections.Concurrent;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Columns;

/// <summary>
/// "Git" column: working-tree status of files and folders in a local repository (modified, new, staged, ignored,
/// conflict; folders show the strongest status inside). One <c>git status</c> per folder, run through the host
/// (<c>shell.run</c>) and cached for a few seconds, serves every row of a listing.
/// </summary>
public sealed class GitStatusColumnPlugin : IColumnMetadataPlugin
{
    public const string StatusColumn = "status";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, (DateTime Created, Lazy<Task<RepositoryStatus?>> Status)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "builtin.columns.git";

    public string DisplayName => "Git";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.columns.git", Loc.T("Kolumna Git"),
        Loc.T("Status plików w repozytorium Git (zmieniony, nowy, dodany, ignorowany, konflikt)."), PluginPermissions.ShellRun);

    public IReadOnlyList<ColumnDefinition> Columns { get; } = [new(StatusColumn, "Git", ColumnAlignment.Left, 96)];

    public bool Supports(in VfsEntry entry) =>
        entry.Kind is VfsEntryKind.File or VfsEntryKind.Directory && VfsPath.GetScheme(entry.Path) == VfsPath.FileScheme;

    public async ValueTask<ColumnValue> GetValueAsync(VfsEntry entry, string columnId, IPluginHost host, CancellationToken cancellationToken)
    {
        if (System.IO.Path.GetDirectoryName(entry.Path) is not { } folder
            || await StatusOf(folder, host, cancellationToken).ConfigureAwait(false) is not { } repository)
        {
            return ColumnValue.Empty;
        }

        var relative = System.IO.Path.GetRelativePath(repository.Root, entry.Path).Replace('\\', '/');
        if (entry.Kind == VfsEntryKind.Directory)
        {
            if (relative == ".git")
            {
                return ColumnValue.Empty;
            }

            // The folder's own entry ("dir/") counts as is; from its contents only real changes roll up — build output
            // ignored somewhere inside does not make the whole folder "ignored".
            var prefix = relative + "/";
            var inside = repository.Codes
                .Where(c => string.Equals(c.Key, prefix, StringComparison.OrdinalIgnoreCase)
                    || (c.Value != "!!" && c.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Value).ToList();
            return inside.Count == 0 ? ColumnValue.Empty : Describe(inside.MaxBy(Rank)!, folder: true);
        }

        if (repository.Codes.TryGetValue(relative, out var code))
        {
            return Describe(code, folder: false);
        }

        // Git lists a wholly untracked or ignored folder once ("dir/"), not the files inside it.
        var container = repository.Codes.FirstOrDefault(c => c.Key.EndsWith('/') && relative.StartsWith(c.Key, StringComparison.OrdinalIgnoreCase));
        return container.Key is null ? ColumnValue.Empty : Describe(container.Value, folder: false);
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z</c>: "XY path\0", renames add "\0old path".</summary>
    public static Dictionary<string, string> ParsePorcelain(string output)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length < 4)
            {
                continue;
            }

            var code = part[..2];
            codes[part[3..]] = code;
            if (code[0] is 'R' or 'C')
            {
                i++; // Skip the original path of a rename or copy.
            }
        }

        return codes;
    }

    private static ColumnValue Describe(string code, bool folder)
    {
        var text = code switch
        {
            "??" => Loc.T("nowy"),
            "!!" => Loc.T("ignorowany"),
            _ when code.Contains('U', StringComparison.Ordinal) || code is "AA" or "DD" => Loc.T("konflikt"),
            _ when code[1] == 'M' || code[1] == 'T' => Loc.T("zmieniony"),
            _ when code[1] == 'D' => Loc.T("usunięty"),
            _ when code[0] == 'A' => Loc.T("dodany"),
            _ when code[0] == 'R' => Loc.T("przeniesiony"),
            _ when code[0] is 'M' or 'T' => Loc.T("w indeksie"),
            _ when code[0] == 'D' => Loc.T("usunięty"),
            _ => code.Trim(),
        };
        return new ColumnValue(folder && code != "!!" ? "● " + text : text, Rank(code));
    }

    /// <summary>Which status a folder shows when its contents differ: conflicts beat changes beat new files.</summary>
    private static int Rank(string code) => code switch
    {
        _ when code.Contains('U', StringComparison.Ordinal) || code is "AA" or "DD" => 6,
        _ when code[1] is 'M' or 'D' or 'T' => 5,
        _ when code[0] is 'A' or 'M' or 'R' or 'D' or 'T' or 'C' => 4,
        "??" => 3,
        "!!" => 1,
        _ => 2,
    };

    private Task<RepositoryStatus?> StatusOf(string folder, IPluginHost host, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cached = _cache.AddOrUpdate(
            folder,
            _ => (now, new Lazy<Task<RepositoryStatus?>>(() => LoadAsync(folder, host, cancellationToken))),
            (_, existing) => now - existing.Created < CacheLifetime && !existing.Status.Value.IsCanceled
                ? existing
                : (now, new Lazy<Task<RepositoryStatus?>>(() => LoadAsync(folder, host, cancellationToken))));
        if (_cache.Count > 256)
        {
            foreach (var stale in _cache.Where(c => now - c.Value.Created > CacheLifetime).Select(c => c.Key).ToList())
            {
                _cache.TryRemove(stale, out _);
            }
        }

        return cached.Status.Value;
    }

    private static async Task<RepositoryStatus?> LoadAsync(string folder, IPluginHost host, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(15);
        var root = await host.RunProcessAsync(new ProcessRunRequest("git", ["-C", folder, "rev-parse", "--show-toplevel"], Timeout: timeout), cancellationToken)
            .ConfigureAwait(false);
        if (root.ExitCode != 0 || root.Output.Trim() is not { Length: > 0 } top)
        {
            return null; // Not inside a repository (or git missing): the column stays empty.
        }

        var status = await host.RunProcessAsync(
            new ProcessRunRequest("git", ["-C", folder, "-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--ignored=matching", "--untracked-files=normal", "--", "."], Timeout: timeout),
            cancellationToken).ConfigureAwait(false);
        return status.ExitCode == 0
            ? new RepositoryStatus(System.IO.Path.GetFullPath(top.Replace('/', '\\')), ParsePorcelain(status.Output))
            : null;
    }

    private sealed record RepositoryStatus(string Root, Dictionary<string, string> Codes);
}
