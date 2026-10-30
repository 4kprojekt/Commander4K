using System.Globalization;
using System.Text;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.Logs;

/// <summary>Opens the log file under the cursor as a panel of message patterns grouped by severity, plus an hourly timeline.</summary>
public sealed class LogAnalyzerPanelPlugin : IPanelPlugin
{
    public const string PluginId = "builtin.logs";

    public string Id => PluginId;

    public string DisplayName => "Log analyzer";

    public string Title => "Analiza logów";

    public string Description => "Grupuje wpisy pliku logu we wzorce wg poziomu (błędy, ostrzeżenia…), z liczbą wystąpień i osią czasu.";

    public bool RequiresArgument => true;

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session();

    private sealed class Session : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("pattern", "Wzorzec komunikatu", ColumnAlignment.Left, 560),
            new("count", "Liczba", ColumnAlignment.Right, 80),
            new("first", "Pierwszy", ColumnAlignment.Left, 140),
            new("last", "Ostatni", ColumnAlignment.Left, 140),
        ];

        private LogAnalysis? _analysis;

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("occurrences", "Pokaż wystąpienia", "F3"),
            new("report", "Raport podsumowania", "Ctrl+R", AppliesToRows: false),
        ];

        public async Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var path = request.Argument is null ? null : LocalFileSystemPlugin.ToLocalPath(request.Argument);
            if (path is null || !File.Exists(path))
            {
                throw new FileNotFoundException("Ustaw kursor na pliku logu (np. *.log) i otwórz analizę ponownie.", path);
            }

            _analysis = await LogAnalyzer.AnalyzeAsync(path, progress, cancellationToken).ConfigureAwait(false);
            var rows = new List<PanelRow>();
            foreach (var group in _analysis.Patterns.GroupBy(p => p.Level))
            {
                var levelId = "level:" + group.Key;
                rows.Add(new PanelRow(levelId, null, [LevelName(group.Key), group.Sum(p => p.Count).ToString("N0", CultureInfo.CurrentCulture), "", ""],
                    Severity(group.Key), [null, group.Sum(p => p.Count), null, null], ExpandedByDefault: group.Key >= LogLevel.Warning));
                var index = 0;
                foreach (var pattern in group)
                {
                    rows.Add(new PanelRow($"{levelId}/{index++}", levelId,
                        [pattern.Template, pattern.Count.ToString("N0", CultureInfo.CurrentCulture), Format(pattern.First), Format(pattern.Last)],
                        Severity(pattern.Level), [null, pattern.Count, pattern.First?.Ticks, pattern.Last?.Ticks]));
                }
            }

            if (_analysis.Hourly.Count > 0)
            {
                rows.Add(new PanelRow("timeline", null, ["Oś czasu (godziny)", _analysis.Hourly.Count.ToString(CultureInfo.CurrentCulture), Format(_analysis.Hourly.Keys.First()), Format(_analysis.Hourly.Keys.Last())], RowSeverity.Highlight));
                foreach (var (hour, counts) in _analysis.Hourly)
                {
                    var bar = new string('█', Math.Min(40, (int)Math.Ceiling(counts.Total * 40.0 / _analysis.Hourly.Values.Max(v => v.Total))));
                    rows.Add(new PanelRow("hour:" + hour.Ticks, "timeline",
                        [$"{hour:yyyy-MM-dd HH}:00  {bar}", counts.Total.ToString("N0", CultureInfo.CurrentCulture), $"błędy: {counts.Errors}", $"ostrz.: {counts.Warnings}"],
                        counts.Errors > 0 ? RowSeverity.Error : counts.Warnings > 0 ? RowSeverity.Warning : RowSeverity.Normal,
                        [hour.Ticks, counts.Total, counts.Errors, counts.Warnings]));
                }
            }

            var status = $"{Path.GetFileName(path)}: {_analysis.Lines:N0} linii{(_analysis.Truncated ? " (obcięto)" : "")} · " +
                $"błędy {_analysis.Count(LogLevel.Error) + _analysis.Count(LogLevel.Fatal):N0} · ostrzeżenia {_analysis.Count(LogLevel.Warning):N0} · wzorce {_analysis.Patterns.Count:N0}";
            return new PanelSnapshot(Columns, rows, status);
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken)
        {
            if (_analysis is not null && rowId.StartsWith("level:", StringComparison.Ordinal) && !rowId.Contains('/'))
            {
                // Level header: its patterns with counts.
                var level = Enum.Parse<LogLevel>(rowId[6..]);
                var list = _analysis.Patterns.Where(p => p.Level == level)
                    .Select(p => new KeyValuePair<string, string>($"{p.Count:N0}×", p.Template)).ToList();
                return Task.FromResult<PanelDetails?>(new PanelDetails([new(LevelName(level), list)]));
            }

            if (Find(rowId) is not { } pattern)
            {
                return Task.FromResult<PanelDetails?>(null);
            }

            var summary = new List<KeyValuePair<string, string>>
            {
                new("Poziom", LevelName(pattern.Level)),
                new("Wystąpienia", pattern.Count.ToString("N0", CultureInfo.CurrentCulture)),
                new("Pierwsza linia", pattern.FirstLine.ToString("N0", CultureInfo.CurrentCulture)),
                new("Zakres czasu", $"{Format(pattern.First)} – {Format(pattern.Last)}"),
                new("Wzorzec", pattern.Template),
            };
            var samples = pattern.Samples.Take(10).Select(s => new KeyValuePair<string, string>($"linia {s.LineNumber}", s.Text)).ToList();
            return Task.FromResult<PanelDetails?>(new PanelDetails([new("Wzorzec", summary), new("Przykłady", samples)]));
        }

        public Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            if (_analysis is null)
            {
                return Task.FromResult<string?>(null);
            }

            var text = new StringBuilder();
            if (actionId == "occurrences")
            {
                foreach (var pattern in rowIds.Select(Find).OfType<LogPattern>())
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"== [{LevelName(pattern.Level)}] {pattern.Template}  ({pattern.Count:N0}×; pokazano {pattern.Samples.Count}) ==");
                    foreach (var sample in pattern.Samples)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"{sample.LineNumber,8}: {sample.Text}");
                    }

                    text.AppendLine();
                }

                return Task.FromResult<string?>(text.Length == 0 ? "Wybierz wzorzec (nie nagłówek poziomu)." : text.ToString());
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"Analiza: {_analysis.Path}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Linie: {_analysis.Lines:N0}{(_analysis.Truncated ? " (obcięto)" : "")}");
            foreach (var level in Enum.GetValues<LogLevel>().Reverse())
            {
                var count = _analysis.Count(level);
                if (count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{LevelName(level),-12} {count,10:N0}");
                }
            }

            text.AppendLine().AppendLine("Najczęstsze błędy:");
            foreach (var pattern in _analysis.Patterns.Where(p => p.Level >= LogLevel.Error).Take(20))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{pattern.Count,8:N0}×  {pattern.Template}");
            }

            return Task.FromResult<string?>(text.ToString());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private LogPattern? Find(string rowId)
        {
            var slash = rowId.IndexOf('/');
            if (_analysis is null || slash < 0 || !rowId.StartsWith("level:", StringComparison.Ordinal))
            {
                return null;
            }

            var level = Enum.Parse<LogLevel>(rowId[6..slash]);
            var index = int.Parse(rowId[(slash + 1)..], CultureInfo.InvariantCulture);
            return _analysis.Patterns.Where(p => p.Level == level).ElementAtOrDefault(index);
        }
    }

    private static string Format(DateTime? value) => value?.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Fatal => "Krytyczne",
        LogLevel.Error => "Błędy",
        LogLevel.Warning => "Ostrzeżenia",
        LogLevel.Info => "Informacje",
        LogLevel.Debug => "Debug",
        LogLevel.Trace => "Trace",
        _ => "Bez poziomu",
    };

    private static RowSeverity Severity(LogLevel level) => level switch
    {
        LogLevel.Fatal or LogLevel.Error => RowSeverity.Error,
        LogLevel.Warning => RowSeverity.Warning,
        LogLevel.Debug or LogLevel.Trace or LogLevel.Unknown => RowSeverity.Muted,
        _ => RowSeverity.Normal,
    };
}
