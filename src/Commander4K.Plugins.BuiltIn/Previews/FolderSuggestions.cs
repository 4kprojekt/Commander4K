using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// The built-in folder views offering themselves for a folder: the gallery for a folder of pictures, the notes overview
/// for an Obsidian vault or a folder of Markdown. Judged on the shared snapshot only (no parent walk, no reads).
/// </summary>
public static class FolderSuggestions
{
    private const int MinFiles = 5;
    private const double MinShare = 0.6;
    private const double Score = 0.7;

    public static FolderSuggestion? Gallery(FolderSnapshot folder)
    {
        var files = folder.Entries.Count(e => e.Kind == VfsEntryKind.File);
        var images = folder.Entries.Count(FolderViews.IsImage);
        return images >= MinFiles && images >= MinShare * files
            ? new FolderSuggestion(Score,
                Loc.P(images, "Ten folder zawiera {0} zdjęcie. Przełączyć w widok galerii?|Ten folder zawiera {0} zdjęcia. Przełączyć w widok galerii?|Ten folder zawiera {0} zdjęć. Przełączyć w widok galerii?"),
                new FolderSuggestionView(View: FolderViewIds.Gallery))
            : null;
    }

    public static FolderSuggestion? Notes(FolderSnapshot folder)
    {
        var view = new FolderSuggestionView(View: FolderViewIds.Notes);
        if (folder.Entries.Any(e => e.Kind == VfsEntryKind.Directory && string.Equals(e.Name, ".obsidian", StringComparison.OrdinalIgnoreCase)))
        {
            return new FolderSuggestion(Score, Loc.T("To jest vault Obsidiana. Pokazać jako notatki z powiązaniami?"), view);
        }

        var files = folder.Entries.Count(e => e.Kind == VfsEntryKind.File);
        var notes = folder.Entries.Count(e => e.Kind == VfsEntryKind.File && NotesOverview.IsMarkdown(e.Name));
        return notes >= MinFiles && notes >= MinShare * files
            ? new FolderSuggestion(Score,
                Loc.P(notes, "Ten folder zawiera {0} notatkę Markdown. Pokazać jako notatki?|Ten folder zawiera {0} notatki Markdown. Pokazać jako notatki?|Ten folder zawiera {0} notatek Markdown. Pokazać jako notatki?"),
                view)
            : null;
    }
}
