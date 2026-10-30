using System.Globalization;
using System.Text.RegularExpressions;

namespace Commander4K.Plugins.BuiltIn.Logs;

public enum LogLevel : byte
{
    Unknown,
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

public sealed record LogOccurrence(int LineNumber, DateTime? Timestamp, string Text);

/// <summary>All occurrences of one message template (variable parts replaced by &lt;*&gt;).</summary>
public sealed class LogPattern(LogLevel level, string template)
{
    public const int MaxSamples = 50;

    public LogLevel Level { get; } = level;

    public string Template { get; } = template;

    public int Count { get; internal set; }

    public DateTime? First { get; internal set; }

    public DateTime? Last { get; internal set; }

    public int FirstLine { get; internal set; }

    public List<LogOccurrence> Samples { get; } = [];
}

public sealed record LogAnalysis(
    string Path,
    int Lines,
    bool Truncated,
    IReadOnlyList<LogPattern> Patterns,
    IReadOnlyDictionary<DateTime, (int Errors, int Warnings, int Total)> Hourly)
{
    public int Count(LogLevel level) => Patterns.Where(p => p.Level == level).Sum(p => p.Count);
}

/// <summary>
/// Streaming log analysis: detects severity and timestamps per entry, attaches continuation lines (stack traces) to
/// the entry above, and groups entries into templates by masking the parts that vary (numbers, ids, paths, quotes).
/// </summary>
public static partial class LogAnalyzer
{
    public const int MaxLines = 5_000_000;

    public static async Task<LogAnalysis> AnalyzeAsync(string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var patterns = new Dictionary<(LogLevel, string), LogPattern>();
        var hourly = new SortedDictionary<DateTime, (int Errors, int Warnings, int Total)>();
        var lineNumber = 0;
        var truncated = false;
        LogPattern? current = null;
        LogOccurrence? currentOccurrence = null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (lineNumber > MaxLines)
            {
                truncated = true;
                break;
            }

            if (lineNumber % 200_000 == 0)
            {
                progress?.Report($"Analiza… {lineNumber:N0} linii");
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (IsContinuation(line) && current is not null && currentOccurrence is not null)
            {
                // Stack-trace line: keep it with the entry it belongs to (only for stored samples).
                var index = current.Samples.IndexOf(currentOccurrence);
                if (index >= 0 && currentOccurrence.Text.Length < 4000)
                {
                    currentOccurrence = currentOccurrence with { Text = currentOccurrence.Text + "\n" + line };
                    current.Samples[index] = currentOccurrence;
                }

                continue;
            }

            var level = DetectLevel(line);
            var timestamp = DetectTimestamp(line);
            var template = Template(line);
            if (!patterns.TryGetValue((level, template), out var pattern))
            {
                pattern = new LogPattern(level, template) { FirstLine = lineNumber };
                patterns[(level, template)] = pattern;
            }

            pattern.Count++;
            if (timestamp is { } t)
            {
                pattern.First ??= t;
                pattern.Last = t;
                var hour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
                var bucket = hourly.GetValueOrDefault(hour);
                hourly[hour] = (bucket.Errors + (level >= LogLevel.Error ? 1 : 0), bucket.Warnings + (level == LogLevel.Warning ? 1 : 0), bucket.Total + 1);
            }

            current = pattern;
            currentOccurrence = new LogOccurrence(lineNumber, timestamp, line);
            if (pattern.Samples.Count < LogPattern.MaxSamples)
            {
                pattern.Samples.Add(currentOccurrence);
            }
        }

        var ordered = patterns.Values
            .OrderByDescending(p => p.Level)
            .ThenByDescending(p => p.Count)
            .ToList();
        return new LogAnalysis(path, Math.Min(lineNumber, MaxLines), truncated, ordered, hourly);
    }

    public static LogLevel DetectLevel(string line)
    {
        var match = LevelRegex().Match(line.Length > 200 ? line[..200] : line);
        if (!match.Success)
        {
            return line.Contains("Exception", StringComparison.Ordinal) ? LogLevel.Error : LogLevel.Unknown;
        }

        return match.Value.ToUpperInvariant() switch
        {
            "FATAL" or "CRITICAL" or "CRIT" or "FTL" or "EMERG" or "ALERT" => LogLevel.Fatal,
            "ERROR" or "ERR" or "FAIL" or "FAILED" or "EXCEPTION" or "SEVERE" => LogLevel.Error,
            "WARN" or "WARNING" or "WRN" => LogLevel.Warning,
            "INFO" or "INFORMATION" or "INF" or "NOTICE" => LogLevel.Info,
            "DEBUG" or "DBG" => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
    }

    public static DateTime? DetectTimestamp(string line)
    {
        var match = TimestampRegex().Match(line.Length > 80 ? line[..80] : line);
        if (!match.Success)
        {
            return null;
        }

        var text = match.Value.Replace('T', ' ').Replace(',', '.');
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) ? value : null;
    }

    /// <summary>Masks variable parts so occurrences of the same message share one template.</summary>
    public static string Template(string line)
    {
        var text = TimestampRegex().Replace(line, string.Empty, 1);
        text = LevelBracketRegex().Replace(text, string.Empty, 1);
        text = GuidRegex().Replace(text, "<*>");
        text = QuotedRegex().Replace(text, "\"<*>\"");
        text = PathRegex().Replace(text, "<path>");
        text = IpRegex().Replace(text, "<ip>");
        text = HexRegex().Replace(text, "<*>");
        text = NumberRegex().Replace(text, "<*>");
        text = WhitespaceRegex().Replace(text, " ").Trim(' ', '-', ':', '|', ']');
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static bool IsContinuation(string line) =>
        line[0] is ' ' or '\t' && (line.TrimStart().StartsWith("at ", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("---", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("...", StringComparison.Ordinal))
        || line.StartsWith("Caused by:", StringComparison.Ordinal);

    [GeneratedRegex(@"\b(FATAL|CRITICAL|CRIT|FTL|EMERG|ALERT|ERROR|ERR|FAIL|FAILED|EXCEPTION|SEVERE|WARN|WARNING|WRN|INFO|INFORMATION|INF|NOTICE|DEBUG|DBG|TRACE|TRC|VERBOSE|VRB)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"[\[(]?\b(FATAL|CRITICAL|CRIT|FTL|ERROR|ERR|WARN|WARNING|WRN|INFO|INFORMATION|INF|DEBUG|DBG|TRACE|TRC|VERBOSE|VRB)\b[\])]?:?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelBracketRegex();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}([.,]\d+)?")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"""[^""]{1,200}""|'[^']{1,200}'")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"([A-Za-z]:\\|\\\\|/)[^\s""'<>|:]+([\\/][^\s""'<>|:]+)+")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\b[0-9a-fA-F]{12,}\b")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"\b\d+([.,]\d+)?\b")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();
}
