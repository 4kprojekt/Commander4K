using System.Globalization;
using System.Threading.Channels;

namespace Commander4K.Core.Plugins;

public enum AuditOutcome : byte
{
    Allowed,
    Denied,
    Failed,
    Info,
}

public sealed record AuditEntry(DateTimeOffset Time, string PluginId, string Operation, string Detail, AuditOutcome Outcome)
{
    public override string ToString() =>
        $"{Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} {Outcome,-7} {PluginId} {Operation} {Detail}";
}

/// <summary>
/// Record of everything plugins do through the host API: one line per call in <c>data/logs/plugins-YYYYMMDD.log</c>
/// (written by a background writer, never on the caller's thread) plus the most recent entries in memory for the
/// plugin manager panel.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private const int MemoryCapacity = 2000;

    private readonly string? _directory;
    private readonly Channel<AuditEntry> _pending = Channel.CreateUnbounded<AuditEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LinkedList<AuditEntry> _recent = new();
    private readonly Lock _gate = new();
    private readonly Task _writer;

    /// <param name="directory">Folder for log files, or <c>null</c> to keep entries in memory only (tests).</param>
    public AuditLog(string? directory)
    {
        _directory = directory;
        _writer = Task.Run(WriteLoopAsync);
    }

    public event Action<AuditEntry>? Recorded;

    public void Record(string pluginId, string operation, string detail, AuditOutcome outcome = AuditOutcome.Allowed)
    {
        var entry = new AuditEntry(DateTimeOffset.Now, pluginId, operation, Shorten(detail), outcome);
        lock (_gate)
        {
            _recent.AddLast(entry);
            if (_recent.Count > MemoryCapacity)
            {
                _recent.RemoveFirst();
            }
        }

        _pending.Writer.TryWrite(entry);
        Recorded?.Invoke(entry);
    }

    public IReadOnlyList<AuditEntry> Recent(string? pluginId = null)
    {
        lock (_gate)
        {
            return [.. _recent.Where(e => pluginId is null || string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_directory is null)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(_directory);
                var file = Path.Combine(_directory, $"plugins-{entry.Time:yyyyMMdd}.log");
                await File.AppendAllTextAsync(file, entry + Environment.NewLine).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Losing a log line must never break the plugin call that produced it.
            }
        }
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ⏎ ");
        return single.Length <= 400 ? single : string.Create(CultureInfo.InvariantCulture, $"{single[..400]}… ({single.Length} znaków)");
    }
}
