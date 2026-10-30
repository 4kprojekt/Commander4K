using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Channels;
using Commander4K.PluginSdk;
using Microsoft.Win32;

namespace Commander4K.Plugins.BuiltIn.Registry;

/// <summary>
/// Windows Registry as a file system (<c>reg://</c>): keys are directories, values are files whose content is their
/// textual data. Search walks keys on all cores via <see cref="Parallel.ForEachAsync{TSource}(IAsyncEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// over a shared work queue, streaming hits as they are found.
/// </summary>
public sealed class WindowsRegistryVfsPlugin : IVfsProviderPlugin, ISearchProviderPlugin
{
    public const string DefaultValueDisplayName = "(Domyślna)";

    private const int PreviewLength = 120;

    public string Id => "builtin.registry";

    public string DisplayName => "Windows Registry";

    public IReadOnlyList<string> Schemes { get; } = [RegistryPath.Scheme];

    public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.CreateDirectory | VfsCapabilities.Delete;

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parsed = RegistryPath.Parse(path);
        if (parsed.IsRoot)
        {
            foreach (var (name, hive) in RegistryPath.Hives)
            {
                yield return new VfsEntry(name, RegistryPath.RootPath + name, VfsEntryKind.RegistryKey, Description: hive.Name);
            }

            yield break;
        }

        using var key = OpenKey(parsed, writable: false);
        var keyPath = parsed.KeyPath;

        foreach (var name in key.GetSubKeyNames())
        {
            yield return new VfsEntry(name, RegistryPath.ForSubKey(keyPath, name), VfsEntryKind.RegistryKey);
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in key.GetValueNames())
        {
            yield return CreateValueEntry(key, keyPath, name);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = RegistryPath.Parse(path);
        if (parsed.IsRoot)
        {
            return ValueTask.FromResult<VfsEntry?>(new VfsEntry(DisplayName, RegistryPath.RootPath, VfsEntryKind.RegistryKey));
        }

        using var key = TryOpenKey(parsed, writable: false);
        if (key is null)
        {
            return ValueTask.FromResult<VfsEntry?>(null);
        }

        if (parsed.IsValue)
        {
            var exists = key.GetValueNames().Contains(parsed.ValueName, StringComparer.OrdinalIgnoreCase);
            return ValueTask.FromResult<VfsEntry?>(exists ? CreateValueEntry(key, parsed.KeyPath, parsed.ValueName!) : null);
        }

        var name = parsed.SubKey.Length == 0 ? parsed.HiveName! : parsed.SubKey[(parsed.SubKey.LastIndexOf('\\') + 1)..];
        return ValueTask.FromResult<VfsEntry?>(new VfsEntry(name, parsed.KeyPath, VfsEntryKind.RegistryKey));
    }

    public string? GetParentPath(string path)
    {
        var parsed = RegistryPath.Parse(path);
        if (parsed.IsRoot)
        {
            return null;
        }

        if (parsed.IsValue)
        {
            return parsed.KeyPath;
        }

        if (parsed.SubKey.Length == 0)
        {
            return RegistryPath.RootPath;
        }

        var separator = parsed.SubKey.LastIndexOf('\\');
        var parentSubKey = separator < 0 ? string.Empty : parsed.SubKey[..separator];
        return (parsed with { SubKey = parentSubKey }).KeyPath;
    }

    public string Combine(string directoryPath, string name) =>
        RegistryPath.ForSubKey(RegistryPath.Parse(directoryPath).KeyPath, name);

    /// <summary>Value data as UTF-8 text (what F3 shows).</summary>
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = RegistryPath.Parse(path);
        if (!parsed.IsValue)
        {
            throw new NotSupportedException("Only registry values have content.");
        }

        using var key = OpenKey(parsed, writable: false);
        var text = FormatData(key.GetValue(parsed.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), int.MaxValue);
        return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false));
    }

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = RegistryPath.Parse(path);
        if (parsed.IsRoot || parsed.IsValue || parsed.SubKey.Length == 0)
        {
            throw new NotSupportedException("Keys can only be created below a hive.");
        }

        parsed.Hive.CreateSubKey(parsed.SubKey, writable: false).Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var parsed = RegistryPath.Parse(path);
        if (parsed.IsRoot || (!parsed.IsValue && parsed.SubKey.Length == 0))
        {
            throw new NotSupportedException("Hives cannot be deleted.");
        }

        if (parsed.IsValue)
        {
            using var key = OpenKey(parsed, writable: true);
            key.DeleteValue(parsed.ValueName!, throwOnMissingValue: true);
        }
        else if (recursive)
        {
            parsed.Hive.DeleteSubKeyTree(parsed.SubKey, throwOnMissingSubKey: true);
        }
        else
        {
            parsed.Hive.DeleteSubKey(parsed.SubKey, throwOnMissingSubKey: true);
        }

        return ValueTask.CompletedTask;
    }

    public bool CanSearch(string rootPath) =>
        string.Equals(VfsPath.GetScheme(rootPath), RegistryPath.Scheme, StringComparison.Ordinal);

    /// <remarks>
    /// <see cref="SearchQuery.NamePattern"/> filters key and value names; <see cref="SearchQuery.ContainsText"/>, when
    /// set, additionally requires a value's data (or name) to contain the text, and then keys themselves never match.
    /// </remarks>
    public async IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = RegistryPath.Parse(query.RootPath);
        var hits = Channel.CreateBounded<SearchHit>(new BoundedChannelOptions(4096) { SingleReader = true });
        var work = Channel.CreateUnbounded<(RegistryKey Hive, string HiveName, string SubKey)>();
        var outstanding = 0;

        void Schedule(RegistryKey hive, string hiveName, string subKey)
        {
            Interlocked.Increment(ref outstanding);
            work.Writer.TryWrite((hive, hiveName, subKey));
        }

        if (root.IsRoot)
        {
            foreach (var (name, hive) in RegistryPath.Hives)
            {
                Schedule(hive, name, string.Empty);
            }
        }
        else
        {
            Schedule(root.Hive, root.HiveName!, root.SubKey);
        }

        var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = query.MaxDegreeOfParallelism > 0 ? query.MaxDegreeOfParallelism : Environment.ProcessorCount,
                        CancellationToken = cancellationToken,
                    };
                    await Parallel.ForEachAsync(work.Reader.ReadAllAsync(cancellationToken), options, async (item, ct) =>
                    {
                        try
                        {
                            foreach (var child in await SearchKeyAsync(item.Hive, item.HiveName, item.SubKey, query, comparison, hits.Writer, ct).ConfigureAwait(false))
                            {
                                if (query.Recursive)
                                {
                                    Schedule(item.Hive, item.HiveName, child);
                                }
                            }
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref outstanding) == 0)
                            {
                                work.Writer.TryComplete();
                            }
                        }
                    }).ConfigureAwait(false);
                    hits.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    hits.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        await foreach (var hit in hits.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }

        await producer.ConfigureAwait(false);
    }

    /// <summary>Emits the hits of one key and returns the sub-key paths still to visit.</summary>
    private static async Task<List<string>> SearchKeyAsync(
        RegistryKey hive,
        string hiveName,
        string subKey,
        SearchQuery query,
        StringComparison comparison,
        ChannelWriter<SearchHit> hits,
        CancellationToken cancellationToken)
    {
        var children = new List<string>();
        RegistryKey? key;
        try
        {
            key = subKey.Length == 0 ? hive : hive.OpenSubKey(subKey, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return children;
        }

        if (key is null)
        {
            return children;
        }

        try
        {
            var keyPath = subKey.Length == 0 ? RegistryPath.RootPath + hiveName : $@"{RegistryPath.RootPath}{hiveName}\{subKey}";
            string[] subKeyNames, valueNames;
            try
            {
                subKeyNames = key.GetSubKeyNames();
                valueNames = key.GetValueNames();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return children;
            }

            foreach (var name in subKeyNames)
            {
                children.Add(subKey.Length == 0 ? name : subKey + @"\" + name);
                if (query.ContainsText is null && MatchesName(query, name))
                {
                    var path = RegistryPath.ForSubKey(keyPath, name);
                    await hits.WriteAsync(new SearchHit(new VfsEntry(name, path, VfsEntryKind.RegistryKey)), cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var name in valueNames)
            {
                var displayName = name.Length == 0 ? DefaultValueDisplayName : name;
                if (!MatchesName(query, displayName))
                {
                    continue;
                }

                string? snippet = null;
                if (query.ContainsText is { } text)
                {
                    var data = FormatData(SafeGetValue(key, name), int.MaxValue);
                    var position = data.IndexOf(text, comparison);
                    if (position < 0 && !name.Contains(text, comparison))
                    {
                        continue;
                    }

                    snippet = position < 0 ? null : Snippet(data, position, text.Length);
                }

                var entry = CreateValueEntry(key, keyPath, name);
                await hits.WriteAsync(new SearchHit(entry, Snippet: snippet ?? entry.Description), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!ReferenceEquals(key, hive))
            {
                key.Dispose();
            }
        }

        return children;
    }

    private static bool MatchesName(SearchQuery query, string name) =>
        query.NamePattern is "*" or "" || FileSystemName.MatchesSimpleExpression(query.NamePattern, name, ignoreCase: !query.CaseSensitive);

    private static VfsEntry CreateValueEntry(RegistryKey key, string keyPath, string name)
    {
        RegistryValueKind kind;
        try
        {
            kind = key.GetValueKind(name);
        }
        catch (IOException)
        {
            kind = RegistryValueKind.Unknown;
        }

        var data = SafeGetValue(key, name);
        return new VfsEntry(
            name.Length == 0 ? DefaultValueDisplayName : name,
            RegistryPath.ForValue(keyPath, name),
            VfsEntryKind.RegistryValue,
            DataLength(data),
            Description: $"{kind}: {FormatData(data, PreviewLength)}");
    }

    private static object? SafeGetValue(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static long DataLength(object? data) => data switch
    {
        null => 0,
        string s => (s.Length + 1) * sizeof(char),
        string[] lines => lines.Sum(l => (l.Length + 1) * sizeof(char)) + sizeof(char),
        byte[] bytes => bytes.Length,
        int => sizeof(int),
        long => sizeof(long),
        _ => -1,
    };

    internal static string FormatData(object? data, int maxLength)
    {
        var text = data switch
        {
            null => string.Empty,
            string s => s,
            string[] lines => string.Join(" | ", lines),
            byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, maxLength == int.MaxValue ? bytes.Length : maxLength / 2))),
            int dword => $"0x{dword:X8} ({(uint)dword})",
            long qword => $"0x{qword:X16} ({(ulong)qword})",
            _ => Convert.ToString(data, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static string Snippet(string data, int position, int length)
    {
        const int context = 30;
        var start = Math.Max(0, position - context);
        var end = Math.Min(data.Length, position + length + context);
        return (start > 0 ? "…" : string.Empty) + data[start..end] + (end < data.Length ? "…" : string.Empty);
    }

    private static RegistryKey OpenKey(RegistryPath path, bool writable) =>
        TryOpenKey(path, writable) ?? throw new DirectoryNotFoundException($"Registry key '{path.KeyPath}' does not exist.");

    private static RegistryKey? TryOpenKey(RegistryPath path, bool writable)
    {
        try
        {
            return path.SubKey.Length == 0 ? path.Hive : path.Hive.OpenSubKey(path.SubKey, writable);
        }
        catch (SecurityException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }
}
