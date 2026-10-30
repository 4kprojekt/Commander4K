namespace Commander4K.Core.Indexing;

public sealed record FrecencyEntry(string Path, int Visits, DateTimeOffset LastVisit, double Score);

/// <param name="OpenPorts">Ports that answered the last discovery probe (e.g. 445, 22, 21).</param>
public sealed record NetworkHostRecord(string Host, string? Address, IReadOnlyList<int> OpenPorts, DateTimeOffset LastSeen);

public readonly record struct CachedDirectorySize(long Bytes, long Files, DateTimeOffset ComputedAt);
