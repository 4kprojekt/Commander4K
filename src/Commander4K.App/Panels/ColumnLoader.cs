using System.Collections.Concurrent;
using Avalonia.Threading;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

/// <summary>A plugin column switched on in the settings.</summary>
public sealed record ActiveColumn(IColumnMetadataPlugin Plugin, ColumnDefinition Definition)
{
    /// <summary><c>pluginId/columnId</c>, as stored in <c>AppSettings.Columns</c>.</summary>
    public string Key => Plugin.Id + "/" + Definition.Id;
}

/// <summary>
/// Fills plugin columns for one tab in the background: rows from the top of the view first, four requests at a time,
/// everything cancelled when the tab navigates away. Values are keyed by path, size and time stamp, so a refresh only
/// recomputes changed files. <see cref="Updated"/> is raised on the UI thread at most ten times a second.
/// </summary>
public sealed class ColumnLoader(Func<PluginManifest, IPluginHost> hostFor)
{
    private const int Parallelism = 4;

    private readonly ConcurrentDictionary<string, ColumnValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private int _notifyPending;

    public IReadOnlyList<ActiveColumn> Columns { get; private set; } = [];

    /// <summary>Raised on the UI thread when new values arrived.</summary>
    public event Action? Updated;

    public ColumnValue? Get(ActiveColumn column, in VfsEntry entry) =>
        _values.TryGetValue(Key(column, entry), out var value) ? value : null;

    public void SetColumns(IReadOnlyList<ActiveColumn> columns) => Columns = columns;

    /// <param name="firstVisible">Index of the top row in view; loading starts there.</param>
    /// <param name="clear">Drop earlier values (new location) instead of keeping unchanged ones (refresh).</param>
    public void Start(IReadOnlyList<VfsEntry> entries, int firstVisible, bool clear)
    {
        Cancel();
        if (clear)
        {
            _values.Clear();
        }

        var columns = Columns;
        if (columns.Count == 0 || entries.Count == 0)
        {
            return;
        }

        var start = Math.Clamp(firstVisible, 0, entries.Count);
        var work = entries.Skip(start).Concat(entries.Take(start))
            .Where(e => e.Kind is not (VfsEntryKind.ParentLink or VfsEntryKind.Action))
            .SelectMany(e => columns.Where(c => c.Plugin.Supports(e) && !_values.ContainsKey(Key(c, e))).Select(c => (Entry: e, Column: c)))
            .ToList();
        if (work.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _run = cts;
        var hosts = columns.Select(c => c.Plugin).Distinct().ToDictionary(p => p, p => hostFor(p.Manifest));
        _ = Task.Run(
            () => Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cts.Token }, async (item, ct) =>
            {
                ColumnValue value;
                try
                {
                    value = await item.Column.Plugin.GetValueAsync(item.Entry, item.Column.Definition.Id, hosts[item.Column.Plugin], ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    value = new ColumnValue("⚠ " + ex.Message); // One unreadable file must not stop the column.
                }

                if (!ct.IsCancellationRequested)
                {
                    _values[Key(item.Column, item.Entry)] = value;
                    NotifySoon();
                }
            }),
            cts.Token).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    public void Cancel()
    {
        _run?.Cancel();
        _run = null;
    }

    private void NotifySoon()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(100);
                Interlocked.Exchange(ref _notifyPending, 0);
                Updated?.Invoke();
            });
        }
    }

    private static string Key(ActiveColumn column, in VfsEntry entry) =>
        $"{column.Key}\n{entry.Path}\n{entry.LastWriteTime.UtcTicks}\n{entry.Size}";
}
