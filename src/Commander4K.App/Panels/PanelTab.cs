using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.Core.Streaming;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

/// <summary>
/// One tab of a file panel: location, rows, cursor, marks. Mutated on the UI thread only; listings are produced by
/// <see cref="VfsRouter.OpenListing"/> on a background producer and arrive here in ≤16 ms batches.
/// </summary>
public sealed class PanelTab
{
    private readonly AppHost _host;
    private CancellationTokenSource? _listing;
    private IDisposable? _watch;
    private long _generation;

    public PanelTab(AppHost host, string path)
    {
        _host = host;
        Path = path;
    }

    public string Path { get; private set; }

    public List<VfsEntry> Entries { get; private set; } = [];

    public HashSet<string> Marked { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory sizes computed with Space, keyed by path.</summary>
    public Dictionary<string, long> DirectorySizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SizesInProgress { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Cursor { get; set; }

    public int TopIndex { get; set; }

    public SortOrder Sort { get; private set; } = SortOrder.Default;

    public bool IsLoading { get; private set; }

    public string? Error { get; private set; }

    public string? Status { get; set; }

    public VfsEntry? CurrentEntry => Cursor >= 0 && Cursor < Entries.Count ? Entries[Cursor] : null;

    public string Title
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            var cut = trimmed.LastIndexOfAny(['\\', '/']);
            var name = cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
            return name.Length == 0 ? Path : name;
        }
    }

    /// <summary>Raised on the UI thread whenever rows, cursor or status change.</summary>
    public event Action? Changed;

    /// <summary>Raised when the location asks for credentials; the window shows a login dialog and retries.</summary>
    public event Action<PanelTab, VfsAuthenticationRequiredException>? AuthenticationRequired;

    public Task NavigateAsync(string path, string? focusName = null) => LoadAsync(path, focusName, preserveState: false);

    /// <summary>Re-lists the current location off-screen and swaps the result in, keeping cursor and marks.</summary>
    public Task RefreshAsync() => LoadAsync(Path, CurrentEntry?.Name, preserveState: true);

    public void CancelLoading()
    {
        if (IsLoading)
        {
            _listing?.Cancel();
        }
    }

    public void SetSort(SortOrder order)
    {
        Sort = order;
        var focus = CurrentEntry?.Path;
        ResortInPlace(focus);
        Changed?.Invoke();
    }

    public IReadOnlyList<VfsEntry> MarkedOrCurrent()
    {
        if (Marked.Count > 0)
        {
            return [.. Entries.Where(e => Marked.Contains(e.Path))];
        }

        return CurrentEntry is { Kind: not (VfsEntryKind.ParentLink or VfsEntryKind.Action) } current ? [current] : [];
    }

    public void ToggleMark(VfsEntry entry)
    {
        if (entry.Kind is VfsEntryKind.ParentLink or VfsEntryKind.Action)
        {
            return;
        }

        if (!Marked.Remove(entry.Path))
        {
            Marked.Add(entry.Path);
        }

        Changed?.Invoke();
    }

    public void MoveCursor(int index)
    {
        Cursor = Math.Clamp(index, 0, Math.Max(0, Entries.Count - 1));
        Changed?.Invoke();
    }

    /// <summary>Computes the size of a directory row in the background, updating the row as totals arrive.</summary>
    public async Task CalculateSizeAsync(VfsEntry entry)
    {
        if (!entry.IsContainer || entry.Kind == VfsEntryKind.ParentLink || !SizesInProgress.Add(entry.Path))
        {
            return;
        }

        var generation = _generation;
        var token = _listing?.Token ?? CancellationToken.None;
        Changed?.Invoke();
        try
        {
            var progress = new Progress<DirectorySize>(size =>
            {
                if (generation == _generation)
                {
                    DirectorySizes[entry.Path] = size.Bytes;
                    Changed?.Invoke();
                }
            });
            var result = await _host.DirectorySizes.CalculateAsync(entry.Path, progress, token);
            if (generation == _generation)
            {
                DirectorySizes[entry.Path] = result.Bytes;
                _host.Index?.SetDirectorySize(entry.Path, result.Bytes, result.Files);
            }
        }
        catch (OperationCanceledException)
        {
            DirectorySizes.Remove(entry.Path);
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się policzyć rozmiaru: {ex.Message}";
        }
        finally
        {
            SizesInProgress.Remove(entry.Path);
            Changed?.Invoke();
        }
    }

    public void Detach()
    {
        _listing?.Cancel();
        StopWatching();
    }

    private async Task LoadAsync(string path, string? focusName, bool preserveState)
    {
        _listing?.Cancel();
        StopWatching();
        var cts = new CancellationTokenSource();
        _listing = cts;
        var generation = ++_generation;

        string? parent;
        try
        {
            parent = _host.Router.GetParentPath(path);
        }
        catch (VfsProviderNotFoundException ex)
        {
            Error = ex.Message;
            IsLoading = false;
            Changed?.Invoke();
            return;
        }

        var previousTop = TopIndex;
        Path = path;
        Error = null;
        Status = null;
        IsLoading = true;
        if (!preserveState)
        {
            Entries = parent is null ? [] : [ParentRow(parent)];
            Marked.Clear();
            DirectorySizes.Clear();
            Cursor = 0;
            TopIndex = 0;
        }

        Changed?.Invoke();

        var all = new List<VfsEntry>();
        try
        {
            var reader = _host.Router.OpenListing(path, cts.Token);
            await Task.Run(
                async () =>
                {
                    var showHidden = _host.Settings.ShowHidden;
                    await foreach (var raw in reader.ReadBatchesAsync(ChannelBatching.FrameInterval, cancellationToken: cts.Token))
                    {
                        var batch = showHidden ? raw : [.. raw.Where(e => (e.Flags & (VfsEntryFlags.Hidden | VfsEntryFlags.System)) == 0)];
                        all.AddRange(batch);
                        if (!preserveState)
                        {
                            Dispatcher.UIThread.Post(() => AppendStreamed(generation, batch, focusName));
                        }
                    }
                },
                cts.Token);

            var order = Sort;
            var sizes = new Dictionary<string, long>(DirectorySizes, StringComparer.OrdinalIgnoreCase);
            await Task.Run(() => PanelSorting.Sort(all, order, sizes), cts.Token);
            if (generation != _generation)
            {
                return;
            }

            var focusPath = preserveState ? CurrentEntry?.Path : null;
            Entries = parent is null ? all : [ParentRow(parent), .. all];
            if (preserveState)
            {
                Marked.IntersectWith(all.Select(e => e.Path));
                TopIndex = previousTop;
            }

            FocusBy(focusPath, focusName);
            IsLoading = false;
            Remember(path, all);
            StartWatching(path, generation);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (generation == _generation)
            {
                IsLoading = false;
                Status = $"Przerwano po {Entries.Count} pozycjach (Esc).";
            }
        }
        catch (VfsAuthenticationRequiredException ex)
        {
            if (generation == _generation)
            {
                IsLoading = false;
                Error = "Wymagane logowanie: " + (ex.Realm ?? ex.Path);
                AuthenticationRequired?.Invoke(this, ex);
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                IsLoading = false;
                Error = ex.Message;
            }
        }

        if (generation == _generation)
        {
            Changed?.Invoke();
        }
    }

    private void AppendStreamed(long generation, IReadOnlyList<VfsEntry> batch, string? focusName)
    {
        // Late batches of a superseded or already finalised listing must not duplicate rows.
        if (generation != _generation || !IsLoading)
        {
            return;
        }

        Entries.AddRange(batch);
        if (focusName is not null && Cursor == 0)
        {
            FocusBy(null, focusName);
        }

        Changed?.Invoke();
    }

    private void FocusBy(string? path, string? name)
    {
        var index = -1;
        if (path is not null)
        {
            index = Entries.FindIndex(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0 && name is not null)
        {
            index = Entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        Cursor = index >= 0 ? index : Math.Clamp(Cursor, 0, Math.Max(0, Entries.Count - 1));
    }

    private void ResortInPlace(string? focusPath)
    {
        var parent = Entries.Count > 0 && Entries[0].Kind == VfsEntryKind.ParentLink ? Entries[0] : (VfsEntry?)null;
        var rest = parent is null ? Entries : Entries.Skip(1).ToList();
        PanelSorting.Sort(rest, Sort, DirectorySizes);
        Entries = parent is { } p ? [p, .. rest] : rest;
        FocusBy(focusPath, null);
    }

    private void Remember(string path, List<VfsEntry> entries)
    {
        if (_host.Index is not { } index)
        {
            return;
        }

        index.RecordVisit(path);
        if (VfsPath.GetScheme(path) is VfsPath.FileScheme or VfsPath.SmbScheme)
        {
            index.IndexEntries(entries);
        }
    }

    private void StartWatching(string path, long generation)
    {
        if (!_host.Router.TryResolve(path, out var provider) || provider is not IVfsChangeNotifier notifier)
        {
            return;
        }

        // Starting a FileSystemWatcher on a UNC path can block; never on the UI thread.
        _ = _host.Executor.RunAsync(
            ct => notifier.Watch(path, () => Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation && !IsLoading)
                {
                    _ = RefreshAsync();
                }
            })),
            CancellationToken.None,
            TimeSpan.FromSeconds(10)).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _generation)
                        {
                            _watch = t.Result;
                        }
                        else
                        {
                            t.Result.Dispose();
                        }
                    });
                }
            },
            TaskScheduler.Default);
    }

    private void StopWatching()
    {
        if (_watch is { } watch)
        {
            _watch = null;
            _ = Task.Run(watch.Dispose);
        }
    }

    private static VfsEntry ParentRow(string parent) => new("..", parent, VfsEntryKind.ParentLink);
}
