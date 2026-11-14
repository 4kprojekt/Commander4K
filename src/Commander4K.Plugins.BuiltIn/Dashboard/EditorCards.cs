using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>The built-in widget values of <c>card</c> with the options <see cref="WidgetCards"/> reads, for the editor's schema.</summary>
public static class EditorCards
{
    public static IReadOnlyList<C4kEditorCard> All { get; } =
    [
        new(WidgetCards.Clock, "Clock; local time or the time of options.timeZone.",
            [new OptionContribution("timeZone", "string", "IANA or Windows time zone id, e.g. Europe/Warsaw.")]),
        new(WidgetCards.Note, "A note; text from options.text or from the text file in open.",
            [new OptionContribution("text", "string", "The note text (otherwise the file named in open).")]),
        new(WidgetCards.Disks, "Fixed disks with free space; open limits it to one disk.", []),
    ];
}
