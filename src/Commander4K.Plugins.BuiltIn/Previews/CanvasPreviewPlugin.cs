using System.Globalization;
using System.Text;
using System.Text.Json;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// Obsidian canvas (<c>.canvas</c>, JSON Canvas): the board as a Mermaid diagram (cards, notes, links and groups as
/// boxes, arrows with their labels) followed by the cards' text.
/// </summary>
public sealed class CanvasPreviewPlugin : IPreviewPlugin
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxNodes = 80;

    public string Id => "builtin.preview.canvas";

    public string DisplayName => "Canvas";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.canvas", Loc.T("Podgląd tablic Obsidian"),
        Loc.T("Tablice Obsidian (.canvas) jako diagram powiązań."), PluginPermissions.FileRead);

    public int Priority => 45;

    public bool CanPreview(VfsEntry entry) => entry.Kind == VfsEntryKind.File && entry.Name.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase);

    public async Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var (data, length, _) = await PreviewReading.ReadHeadAsync(host, entry.Path, MaxBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return new MarkdownPreview(Describe(Path.GetFileNameWithoutExtension(entry.Name), PreviewReading.Decode(data, length)), CanEdit: false);
        }
        catch (JsonException ex)
        {
            return new TextPreview(Loc.T("Nieprawidłowy plik .canvas: {0}", ex.Message) + "\n\n" + PreviewReading.Decode(data, length), CanEdit: false);
        }
    }

    public static string Describe(string title, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var nodes = root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array ? n.EnumerateArray().Take(MaxNodes).ToList() : [];
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = new StringBuilder($"# 🧩 {title}\n\n");
        text.AppendLine(Loc.P(nodes.Count, "{0} element|{0} elementy|{0} elementów") + " · " +
            Loc.P(root.TryGetProperty("edges", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0, "{0} połączenie|{0} połączenia|{0} połączeń"))
            .AppendLine().AppendLine("```mermaid").AppendLine("flowchart LR");
        var cards = new List<(string Label, string Text)>();
        foreach (var node in nodes)
        {
            var id = node.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : "text";
            var label = type switch
            {
                "file" => "📄 " + Path.GetFileNameWithoutExtension(Str(node, "file")),
                "link" => "🔗 " + Str(node, "url"),
                "group" => "▢ " + Str(node, "label"),
                _ => FirstLine(Str(node, "text")),
            };
            var key = "c" + ids.Count.ToString(CultureInfo.InvariantCulture);
            ids[id] = key;
            text.AppendLine(type == "group" ? $"  {key}[/\"{Label(label)}\"/]" : $"  {key}[\"{Label(label)}\"]");
            if (type == "text" && Str(node, "text") is { Length: > 0 } body)
            {
                cards.Add((label, body));
            }
        }

        if (root.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (ids.TryGetValue(Str(edge, "fromNode"), out var from) && ids.TryGetValue(Str(edge, "toNode"), out var to))
                {
                    var label = Str(edge, "label");
                    text.AppendLine(label.Length > 0 ? $"  {from} -->|\"{Label(label)}\"| {to}" : $"  {from} --> {to}");
                }
            }
        }

        text.AppendLine("```").AppendLine();
        if (cards.Count > 0)
        {
            text.AppendLine("## " + Loc.T("Karty")).AppendLine();
            foreach (var (label, body) in cards.Take(30))
            {
                text.AppendLine("### " + label).AppendLine().AppendLine(body).AppendLine();
            }
        }

        return text.ToString();

        static string Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        static string FirstLine(string value)
        {
            var line = value.Split('\n').Select(l => l.Trim().TrimStart('#', ' ')).FirstOrDefault(l => l.Length > 0) ?? "…";
            return line.Length > 60 ? line[..60] + "…" : line;
        }

        // Text labels (no HTML) show entities literally, so quotes become apostrophes rather than #quot;.
        static string Label(string value) => value.Replace('"', '\'').Replace('\n', ' ');
    }
}
