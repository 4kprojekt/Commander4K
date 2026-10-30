namespace Commander4K.PluginSdk;

public readonly record struct ArchiveEntryInfo(
    string Path,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    DateTimeOffset LastWriteTime);

/// <param name="EntryPath">Path inside the archive, using <c>/</c> separators.</param>
public sealed record ArchiveInput(string EntryPath, Func<CancellationToken, ValueTask<Stream>> OpenAsync);

/// <summary>Archive handler (Total Commander WCX equivalent).</summary>
public interface IArchivePlugin : IPlugin
{
    /// <summary>Lower-case extensions including the dot, e.g. <c>.zip</c>.</summary>
    IReadOnlyList<string> Extensions { get; }

    IAsyncEnumerable<ArchiveEntryInfo> ListAsync(Stream archive, CancellationToken cancellationToken);

    ValueTask<Stream> OpenEntryAsync(Stream archive, string entryPath, CancellationToken cancellationToken);

    /// <param name="progress">Receives the cumulative number of uncompressed bytes written.</param>
    ValueTask PackAsync(
        Stream output,
        IReadOnlyList<ArchiveInput> inputs,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support packing.");
}
