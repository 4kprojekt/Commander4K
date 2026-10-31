using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// The folder preview for notes: an Obsidian vault (a <c>.obsidian</c> folder here or in a parent) or a folder that is
/// mostly Markdown. Reads the notes through the host (≤ 500 notes, ≤ 64 KB each, hidden folders and the trash
/// skipped) and describes them as Markdown: a Mermaid graph of how the notes link to each other, the most linked notes,
/// recently changed notes with excerpts, tags, orphans and links to notes that do not exist.
/// </summary>
public static partial class NotesOverview
{
    public const int MaxNotes = 500;
    private const int MaxNoteBytes = 64 * 1024;
    private const int MaxDepth = 6;
    private const int GraphNodes = 24;
    private const int GraphEdges = 60;

    public sealed record Note(string Name, string Path, string Folder, DateTimeOffset Modified, string Excerpt,
        IReadOnlyList<string> Links, IReadOnlyList<string> Tags, int Embeds);

    /// <summary>
    /// The vault root containing <paramref name="folder"/> (the folder itself or up to 5 parents with a <c>.obsidian</c>
    /// folder), or <c>null</c>. <paramref name="listing"/> is the folder's own listing, to avoid listing it twice.
    /// </summary>
    public static async Task<string?> FindVaultRootAsync(IPluginHost host, string folder, IReadOnlyList<VfsEntry> listing, CancellationToken cancellationToken)
    {
        if (listing.Any(e => e.Kind == VfsEntryKind.Directory && e.Name == ".obsidian"))
        {
            return folder;
        }

        // Parents are only walked on disks and shares (VFS paths like sftp:// or zip:// have their own parent rules).
        if (!Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        var current = folder;
        for (var level = 0; level < 5 && Path.GetDirectoryName(current.TrimEnd('\\', '/')) is { Length: > 0 } parent && parent != current; level++)
        {
            current = parent;
            try
            {
                if (await host.GetEntryAsync(Path.Combine(current, ".obsidian"), cancellationToken).ConfigureAwait(false) is { Kind: VfsEntryKind.Directory })
                {
                    return current;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null; // An inaccessible parent ends the search.
            }
        }

        return null;
    }

    /// <summary>A plain folder of notes: at least 3 Markdown files that make up at least half of the files.</summary>
    public static bool LooksLikeNotes(IReadOnlyList<VfsEntry> listing)
    {
        var files = listing.Where(e => e.Kind == VfsEntryKind.File).ToList();
        var markdown = files.Count(e => IsMarkdown(e.Name));
        return markdown >= 3 && markdown * 2 >= files.Count;
    }

    public static async Task<string> BuildAsync(IPluginHost host, string folder, string? vaultRoot, CancellationToken cancellationToken)
    {
        var notes = await ReadNotesAsync(host, folder, cancellationToken).ConfigureAwait(false);
        return Describe(notes, Path.GetFileName(folder.TrimEnd('\\', '/')), vaultRoot, folder);
    }

    /// <summary>The Markdown overview of parsed notes (separate from reading, for tests).</summary>
    public static string Describe(IReadOnlyList<Note> notes, string title, string? vaultRoot, string folder)
    {
        var byName = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            byName.TryAdd(note.Name, note);
        }

        Note? Resolve(string link)
        {
            var target = link.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(target.TrimEnd('/'));
            return byName.TryGetValue(name, out var found) ? found : null;
        }

        var outgoing = notes.ToDictionary(n => n, n => n.Links.Select(Resolve).OfType<Note>().Where(t => t != n).Distinct().ToList());
        var incoming = notes.ToDictionary(n => n, _ => new List<Note>());
        foreach (var (source, targets) in outgoing)
        {
            foreach (var target in targets)
            {
                incoming[target].Add(source);
            }
        }

        var unresolved = notes.SelectMany(n => n.Links.Where(l => Resolve(l) is null).Select(l => (Link: l, From: n.Name)))
            .GroupBy(x => x.Link, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
        var tags = notes.SelectMany(n => n.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var orphans = notes.Where(n => outgoing[n].Count == 0 && incoming[n].Count == 0).ToList();
        var linkCount = outgoing.Sum(p => p.Value.Count);
        int Degree(Note n) => outgoing[n].Count + incoming[n].Count;

        var text = new StringBuilder();
        text.AppendLine(vaultRoot is null
            ? $"# 📝 {title} — " + Loc.T("notatki Markdown")
            : $"# 🗂 {title} — " + (string.Equals(vaultRoot.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? Loc.T("sejf Obsidian")
                : Loc.T("folder w sejfie Obsidian „{0}”", Path.GetFileName(vaultRoot.TrimEnd('\\', '/')))));
        text.AppendLine();
        text.Append("**").Append(Loc.P(notes.Count, "{0} notatka|{0} notatki|{0} notatek")).Append(" · ")
            .Append(Loc.P(linkCount, "{0} powiązanie|{0} powiązania|{0} powiązań")).Append(" · ")
            .Append(Loc.P(tags.Count, "{0} tag|{0} tagi|{0} tagów")).Append(" · ")
            .Append(Loc.P(orphans.Count, "{0} bez powiązań|{0} bez powiązań|{0} bez powiązań")).Append("**");
        if (notes.Count > 0)
        {
            text.Append(" · ").Append(Loc.T("ostatnia zmiana {0:yyyy-MM-dd HH:mm}", notes.Max(n => n.Modified).LocalDateTime));
        }

        text.AppendLine().AppendLine();
        if (notes.Count >= MaxNotes)
        {
            text.AppendLine(Loc.T("_Pokazano pierwsze {0} notatek._", MaxNotes)).AppendLine();
        }

        // The graph: the most connected notes and the links among them.
        var hubs = notes.Where(n => Degree(n) > 0).OrderByDescending(Degree).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).Take(GraphNodes).ToList();
        if (hubs.Count > 1)
        {
            var ids = hubs.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => "n" + x.i.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("## " + Loc.T("Graf powiązań")).AppendLine().AppendLine("```mermaid").AppendLine("flowchart LR");
            foreach (var note in hubs)
            {
                text.AppendLine($"  {ids[note]}[\"{MermaidLabel(note.Name)}\"]");
            }

            var edges = 0;
            foreach (var source in hubs)
            {
                foreach (var target in outgoing[source].Where(ids.ContainsKey))
                {
                    if (++edges > GraphEdges)
                    {
                        break;
                    }

                    text.AppendLine($"  {ids[source]} --> {ids[target]}");
                }
            }

            var top = hubs.Take(3).Select(n => ids[n]);
            text.AppendLine("  classDef hub stroke-width:3px").AppendLine($"  class {string.Join(',', top)} hub").AppendLine("```").AppendLine();
        }

        var ranked = notes.Where(n => Degree(n) > 0).OrderByDescending(Degree).Take(12).ToList();
        if (ranked.Count > 0)
        {
            text.AppendLine("## " + Loc.T("Najbardziej powiązane")).AppendLine()
                .AppendLine($"| {Loc.T("Notatka")} | → | ← | {Loc.T("Tagi")} |").AppendLine("|---|---:|---:|---|");
            foreach (var note in ranked)
            {
                text.AppendLine($"| {Cell(note.Name)} | {outgoing[note].Count} | {incoming[note].Count} | {Cell(string.Join(" ", note.Tags.Take(4).Select(t => "#" + t)))} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## " + Loc.T("Ostatnio zmieniane")).AppendLine();
        foreach (var note in notes.OrderByDescending(n => n.Modified).Take(8))
        {
            text.AppendLine($"### {note.Name}");
            if (note.Excerpt.Length > 0)
            {
                text.AppendLine($"> {note.Excerpt}");
            }

            var details = new List<string> { $"{note.Modified.LocalDateTime:yyyy-MM-dd HH:mm}" };
            if (note.Folder.Length > 0)
            {
                details.Insert(0, note.Folder);
            }

            if (outgoing[note].Count > 0)
            {
                details.Add("→ " + string.Join(", ", outgoing[note].Take(5).Select(n => n.Name)));
            }

            if (incoming[note].Count > 0)
            {
                details.Add("← " + string.Join(", ", incoming[note].Take(5).Select(n => n.Name)));
            }

            text.AppendLine().AppendLine("_" + Cell(string.Join(" · ", details)) + "_").AppendLine();
        }

        if (tags.Count > 0)
        {
            text.AppendLine("## " + Loc.T("Tagi")).AppendLine()
                .AppendLine(string.Join(" · ", tags.Take(40).Select(g => $"`#{g.Key}` {g.Count()}"))).AppendLine();
        }

        if (orphans.Count > 0)
        {
            text.AppendLine("## " + Loc.T("Bez powiązań")).AppendLine()
                .AppendLine(string.Join(", ", orphans.Take(30).Select(n => n.Name)) + (orphans.Count > 30 ? ", …" : string.Empty)).AppendLine();
        }

        if (unresolved.Count > 0)
        {
            text.AppendLine("## " + Loc.T("Odnośniki do brakujących notatek")).AppendLine();
            foreach (var group in unresolved.Take(15))
            {
                text.AppendLine($"- `{Cell(group.Key)}` ← {string.Join(", ", group.Select(x => x.From).Distinct().Take(4))}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Links, tags and an excerpt from one note's Markdown.</summary>
    public static Note Parse(string name, string path, string folder, DateTimeOffset modified, string markdown)
    {
        var body = markdown.Replace("\r\n", "\n");
        var tags = new List<string>();
        if (body.StartsWith("---\n", StringComparison.Ordinal) && body.IndexOf("\n---", 4, StringComparison.Ordinal) is var end and > 0)
        {
            var frontmatter = body[4..end];
            body = body[(end + 4)..];
            if (FrontmatterTagsRegex().Match(frontmatter) is { Success: true } match)
            {
                tags.AddRange(match.Groups["list"].Value.Split([',', '[', ']', '\n', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.Trim('"', '\'', '#')).Where(t => t.Length > 0));
            }
        }

        var withoutCode = CodeRegex().Replace(body, string.Empty);
        var links = new List<string>();
        var embeds = 0;
        foreach (Match match in WikiLinkRegex().Matches(withoutCode))
        {
            var target = match.Groups["target"].Value.Trim();
            if (match.Groups["embed"].Success && !IsMarkdown(target) && Path.HasExtension(target))
            {
                embeds++; // ![[image.png]]: an attachment, not a note.
                continue;
            }

            if (target.Length > 0)
            {
                links.Add(target);
            }
        }

        foreach (Match match in MarkdownLinkRegex().Matches(withoutCode))
        {
            links.Add(Uri.UnescapeDataString(match.Groups["target"].Value));
        }

        tags.AddRange(TagRegex().Matches(withoutCode).Select(m => m.Groups["tag"].Value));
        return new Note(name, path, folder, modified, Excerpt(withoutCode), links,
            [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)], embeds);
    }

    private static async Task<IReadOnlyList<Note>> ReadNotesAsync(IPluginHost host, string folder, CancellationToken cancellationToken)
    {
        var files = new List<(VfsEntry Entry, string Folder)>();
        var pending = new Queue<(string Path, string Relative, int Depth)>([(folder, string.Empty, 0)]);
        while (pending.Count > 0 && files.Count < MaxNotes)
        {
            var (path, relative, depth) = pending.Dequeue();
            IReadOnlyList<VfsEntry> entries;
            try
            {
                entries = await host.ListAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Kind == VfsEntryKind.Directory && depth < MaxDepth && !entry.Name.StartsWith('.')
                    && !entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && (entry.Flags & VfsEntryFlags.ReparsePoint) == 0)
                {
                    pending.Enqueue((entry.Path, relative.Length == 0 ? entry.Name : relative + "/" + entry.Name, depth + 1));
                }
                else if (entry.Kind == VfsEntryKind.File && IsMarkdown(entry.Name) && files.Count < MaxNotes)
                {
                    files.Add((entry, relative));
                }
            }
        }

        var notes = new Note?[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (i, ct) =>
        {
            var (entry, relative) = files[i];
            try
            {
                var (data, length, _) = await PreviewReading.ReadHeadAsync(host, entry.Path, MaxNoteBytes, ct).ConfigureAwait(false);
                notes[i] = Parse(Path.GetFileNameWithoutExtension(entry.Name), entry.Path, relative, entry.LastWriteTime, PreviewReading.Decode(data, length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable note: left out.
            }
        }).ConfigureAwait(false);
        return [.. notes.OfType<Note>()];
    }

    private static bool IsMarkdown(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first real sentence: headings, links and Markdown markers stripped, at most 180 characters.</summary>
    private static string Excerpt(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("![[", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith('|'))
            {
                continue;
            }

            line = WikiLinkRegex().Replace(line, m => m.Groups["alias"].Success ? m.Groups["alias"].Value : m.Groups["target"].Value);
            line = MarkdownMarkersRegex().Replace(line, string.Empty).Trim();
            if (line.Length > 0)
            {
                return line.Length > 180 ? line[..180].TrimEnd() + "…" : line;
            }
        }

        return string.Empty;
    }

    // Text labels (no HTML) show entities literally, so quotes become apostrophes rather than #quot;.
    private static string MermaidLabel(string text) => text.Replace('"', '\'').Replace('\n', ' ');

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    [GeneratedRegex(@"(?<embed>!)?\[\[(?<target>[^\]\|#\^]*)(?:[#\^][^\]\|]*)?(?:\|(?<alias>[^\]]*))?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"(?<!!)\[[^\]]*\]\((?<target>(?!https?:)[^)\s]+\.md)(?:#[^)]*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?<![\w/#&])#(?<tag>[\p{L}_][\p{L}\p{N}_/\-]*)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^tags?:\s*(?<list>(?:\[.*?\]|.*)(?:\n\s*-\s*.*)*)", RegexOptions.Multiline)]
    private static partial Regex FrontmatterTagsRegex();

    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]*`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"[*_~`>]+|\[(?=[^\]]*\]\()|\]\([^)]*\)")]
    private static partial Regex MarkdownMarkersRegex();
}
