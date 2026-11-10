using System.Globalization;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>One reading of a server: what <see cref="SshStatsParser.Command"/> printed.</summary>
/// <param name="CpuTicks">The <c>cpu</c> line of <c>/proc/stat</c> (user, nice, system, idle, iowait, …), for a percentage between two readings.</param>
public sealed record SshStats(
    string System,
    double[] LoadAverage,
    long[]? CpuTicks,
    long MemTotalKb,
    long MemAvailableKb,
    IReadOnlyList<(string Mount, long SizeKb, long UsedKb)> Disks,
    TimeSpan Uptime)
{
    public bool IsLinux => string.Equals(System, "Linux", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one fixed, read-only command the SSH monitor runs, and its parser. Sections are marked (<c>@@name</c>) so a
/// missing file or an unexpected line spoils only its own section.
/// </summary>
public static class SshStatsParser
{
    /// <summary>Never built from user input: a <c>.c4k</c> file cannot add anything to it.</summary>
    public const string Command =
        "echo @@uname; uname -s; " +
        "echo @@loadavg; cat /proc/loadavg; " +
        "echo @@stat; head -1 /proc/stat; " +
        "echo @@meminfo; grep -E 'MemTotal|MemAvailable' /proc/meminfo; " +
        "echo @@df; df -P -k -x tmpfs -x devtmpfs -x overlay -x squashfs 2>/dev/null; " +
        "echo @@uptime; cat /proc/uptime";

    public static SshStats Parse(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = sections[line[2..]] = [];
            }
            else if (line.Length > 0)
            {
                current?.Add(line);
            }
        }

        List<string> Section(string name) => sections.TryGetValue(name, out var lines) ? lines : [];

        var load = Section("loadavg").FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray() ?? [];
        if (load.Length != 3 || load.Any(double.IsNaN))
        {
            load = [];
        }

        long[]? ticks = null;
        if (Section("stat").FirstOrDefault() is { } cpu && cpu.StartsWith("cpu ", StringComparison.Ordinal))
        {
            var values = cpu.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => long.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : -1).ToArray();
            ticks = values.Length >= 4 && values.All(v => v >= 0) ? values : null;
        }

        long Memory(string key) => Section("meminfo").Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1))
            .Select(v => long.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : 0).FirstOrDefault();

        var disks = new List<(string, long, long)>();
        foreach (var line in Section("df").Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size) && long.TryParse(parts[2], CultureInfo.InvariantCulture, out var used))
            {
                disks.Add((string.Join(' ', parts[5..]), size, used));
            }
        }

        var uptime = Section("uptime").FirstOrDefault()?.Split(' ').FirstOrDefault() is { } seconds
            && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;

        return new SshStats(Section("uname").FirstOrDefault() ?? string.Empty, load, ticks, Memory("MemTotal"), Memory("MemAvailable"), disks, uptime);
    }

    /// <summary>Busy share of the CPU between two readings (idle and iowait are not busy); <c>null</c> when no time passed.</summary>
    public static double? CpuPercent(long[] before, long[] after)
    {
        static (long Total, long Idle) Sum(long[] ticks) => (ticks.Sum(), ticks[3] + (ticks.Length > 4 ? ticks[4] : 0));
        var (total0, idle0) = Sum(before);
        var (total1, idle1) = Sum(after);
        var total = total1 - total0;
        return total <= 0 ? null : (total - (idle1 - idle0)) * 100.0 / total;
    }
}
