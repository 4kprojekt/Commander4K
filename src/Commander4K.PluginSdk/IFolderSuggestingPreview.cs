namespace Commander4K.PluginSdk;

/// <summary>
/// A preview that can tell whether a whole folder suits it. When the cursor rests on a folder, the host reads one bounded
/// listing and asks every such preview; the best answer becomes a hint ("switch to the gallery?"), and accepting it
/// writes the folder's <c>c4k.dir</c>.
/// </summary>
public interface IFolderSuggestingPreview : IPreviewPlugin
{
    /// <returns><c>null</c> when the folder does not suit this preview.</returns>
    Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken);
}

/// <summary>One listing shared by every candidate, so no plugin lists the folder itself for this.</summary>
/// <param name="Truncated">The listing stopped at the host's limit; <paramref name="Entries"/> is only its start.</param>
public sealed record FolderSnapshot(VfsEntry Folder, IReadOnlyList<VfsEntry> Entries, bool Truncated);

/// <param name="Score">0..1; the host ignores answers below 0.5 and shows the highest.</param>
/// <param name="Message">The hint text, already in the user's language.</param>
/// <param name="View">What the folder's <c>c4k.dir</c> gets as its <c>enter</c> and <c>preview</c> action.</param>
public sealed record FolderSuggestion(double Score, string Message, FolderSuggestionView View);

/// <summary>Either a built-in view (<see cref="View"/>: <c>gallery</c>, <c>notes</c>…) or a plugin preview.</summary>
/// <param name="OptionsJson">Raw JSON object written as the action's <c>options</c> (plugins only).</param>
public sealed record FolderSuggestionView(string? View = null, string? Plugin = null, string? Preview = null, string? OptionsJson = null);
