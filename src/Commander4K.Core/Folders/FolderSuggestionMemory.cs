using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commander4K.Core.Folders;

/// <summary>
/// What the user said about folder suggestions: folders declined for good (<c>data/folder-suggestions.json</c>, never a
/// file in the folder itself), folders postponed for this session, and how often the "back to files" tour was seen.
/// </summary>
public sealed class FolderSuggestionMemory(string? filePath)
{
    private const int TourShows = 3;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _session = new(StringComparer.OrdinalIgnoreCase);
    private FolderSuggestionFile _file = new();

    public static async Task<FolderSuggestionMemory> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        var memory = new FolderSuggestionMemory(filePath);
        try
        {
            await using var stream = File.OpenRead(filePath);
            memory._file = await JsonSerializer.DeserializeAsync(stream, FolderSuggestionJsonContext.Default.FolderSuggestionFile, cancellationToken).ConfigureAwait(false)
                ?? new FolderSuggestionFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or broken preference data: start empty.
        }

        return memory;
    }

    public bool IsMuted(string folder)
    {
        lock (_gate)
        {
            return _session.Contains(Normalize(folder)) || _file.Declined.Contains(Normalize(folder), StringComparer.OrdinalIgnoreCase);
        }
    }

    public void MuteForSession(string folder)
    {
        lock (_gate)
        {
            _session.Add(Normalize(folder));
        }
    }

    public Task DeclineAsync(string folder)
    {
        lock (_gate)
        {
            if (!_file.Declined.Contains(Normalize(folder), StringComparer.OrdinalIgnoreCase))
            {
                _file.Declined.Add(Normalize(folder));
            }
        }

        return SaveAsync();
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            _file.Declined.Clear();
            _session.Clear();
        }

        return SaveAsync();
    }

    public bool ShouldShowTour
    {
        get
        {
            lock (_gate)
            {
                return _file.TourDismissals < TourShows && !_file.TourUsed;
            }
        }
    }

    public Task RecordTourDismissedAsync()
    {
        lock (_gate)
        {
            _file.TourDismissals++;
        }

        return SaveAsync();
    }

    public Task RecordTourUsedAsync()
    {
        lock (_gate)
        {
            _file.TourUsed = true;
        }

        return SaveAsync();
    }

    private static string Normalize(string folder) => folder.TrimEnd('\\', '/');

    private async Task SaveAsync()
    {
        if (filePath is null)
        {
            return;
        }

        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_file, FolderSuggestionJsonContext.Default.FolderSuggestionFile);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preference data only: losing a write costs one more hint.
        }
    }
}

public sealed class FolderSuggestionFile
{
    public List<string> Declined { get; set => field = value ?? []; } = [];

    public int TourDismissals { get; set; }

    public bool TourUsed { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FolderSuggestionFile))]
internal sealed partial class FolderSuggestionJsonContext : JsonSerializerContext;
