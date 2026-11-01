using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace Commander4K.App.Preview;

/// <summary>
/// Markdown → Avalonia controls (no HTML, no browser): headings, paragraphs with emphasis/code/links, nested lists
/// with task boxes, quotes, code blocks, tables and rules, all coloured from the active skin.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static Control Render(string markdown, SkinBrushes brushes)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var root = new StackPanel { Spacing = 10, Margin = new Thickness(20, 16), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var block in document)
        {
            root.Children.Add(RenderBlock(block, brushes));
        }

        return root;
    }

    private static Control RenderBlock(Block block, SkinBrushes b) => block switch
    {
        // Design system: h1 22 bold, h2 16 semibold over a rule, smaller headings in the body size.
        HeadingBlock { Level: 2 } heading => new Border
        {
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 4),
            Margin = new Thickness(0, 10, 0, 0),
            Child = Text(heading.Inline, b, size: b.FontSize + 3, weight: FontWeight.SemiBold),
        },
        HeadingBlock heading => Text(heading.Inline, b, size: heading.Level switch { 1 => b.FontSize + 9, 3 => b.FontSize + 1, _ => b.FontSize },
            weight: heading.Level == 1 ? FontWeight.Bold : FontWeight.SemiBold, margin: new Thickness(0, 6, 0, 0)),
        ParagraphBlock paragraph => Text(paragraph.Inline, b),
        ListBlock list => RenderList(list, b),
        QuoteBlock quote => new Border
        {
            BorderBrush = b.Accent,
            Background = b.HighlightBackground,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, b.IsModern ? 4 : 0, b.IsModern ? 4 : 0, 0),
            Padding = new Thickness(12, 6, 10, 6),
            Child = Container(quote, b, foreground: b.MutedForeground),
        },
        FencedCodeBlock { Info: { } info } fenced when info.Trim().Equals("mermaid", StringComparison.OrdinalIgnoreCase) =>
            Mermaid(fenced.Lines.ToString().TrimEnd(), b),
        FencedCodeBlock or CodeBlock => new Border
        {
            Background = b.CodeBackground,
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(b.IsModern ? 6 : 0),
            Padding = new Thickness(12, 10),
            Child = new SelectableTextBlock
            {
                Text = ((LeafBlock)block).Lines.ToString().TrimEnd(),
                FontFamily = b.MonospaceFamily,
                FontSize = b.FontSize - 1,
                Foreground = b.FileForeground,
                TextWrapping = TextWrapping.Wrap,
            },
        },
        ThematicBreakBlock => new Border { Height = 1, Background = b.PanelBorder, Margin = new Thickness(0, 6) },
        MdTable table => RenderTable(table, b),
        HtmlBlock html => new SelectableTextBlock { Text = html.Lines.ToString(), Foreground = b.MutedForeground, FontFamily = b.MonospaceFamily, TextWrapping = TextWrapping.Wrap },
        ContainerBlock container => Container(container, b),
        LeafBlock leaf => new SelectableTextBlock { Text = leaf.Lines.ToString(), Foreground = b.FileForeground, TextWrapping = TextWrapping.Wrap },
        _ => new Panel(),
    };

    /// <summary>
    /// A ```mermaid block: drawn by <see cref="MermaidRenderer"/> once the block is on screen (offline, skin colours);
    /// the code itself when WebView2 is unavailable or the diagram has an error (with the message).
    /// </summary>
    private static Control Mermaid(string code, SkinBrushes b)
    {
        if (!MermaidRenderer.IsAvailable)
        {
            return CodeBox(code, b, Loc.T("Diagram Mermaid (do rysowania potrzebny jest Microsoft Edge WebView2)"));
        }

        var holder = new Border
        {
            MinHeight = 32,
            Padding = new Thickness(0, 4),
            Child = new TextBlock { Text = Loc.T("Rysowanie diagramu…"), Foreground = b.MutedForeground, FontSize = b.FontSize - 1 },
        };
        var started = false;
        holder.AttachedToVisualTree += async (_, _) =>
        {
            if (started)
            {
                return;
            }

            started = true;
            try
            {
                var image = await MermaidRenderer.RenderAsync(code, b);
                var view = new Image
                {
                    Source = image.Bitmap,
                    MaxWidth = image.Width,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                ToolTip.SetTip(view, code);
                holder.Child = view;
            }
            catch (Exception ex) when (ex is MermaidRenderException or TimeoutException or InvalidOperationException
                or System.Runtime.InteropServices.COMException or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                holder.Child = CodeBox(code, b, Loc.T("Nie udało się narysować diagramu: {0}", ex.Message));
            }
        };
        return holder;
    }

    private static Control CodeBox(string code, SkinBrushes b, string note) =>
        new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = note, Foreground = b.MutedForeground, FontSize = b.FontSize - 1, TextWrapping = TextWrapping.Wrap },
                new Border
                {
                    Background = b.CodeBackground,
                    BorderBrush = b.PanelBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(b.IsModern ? 6 : 0),
                    Padding = new Thickness(12, 10),
                    Child = new SelectableTextBlock { Text = code, FontFamily = b.MonospaceFamily, FontSize = b.FontSize - 1, Foreground = b.FileForeground, TextWrapping = TextWrapping.Wrap },
                },
            },
        };

    private static StackPanel Container(ContainerBlock container, SkinBrushes b, IBrush? foreground = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in container)
        {
            var control = RenderBlock(child, b);
            if (foreground is not null && control is SelectableTextBlock text)
            {
                text.Foreground = foreground;
            }

            panel.Children.Add(control);
        }

        return panel;
    }

    private static StackPanel RenderList(ListBlock list, SkinBrushes b)
    {
        var panel = new StackPanel { Spacing = 3 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.Descendants<TaskList>().FirstOrDefault();
            var marker = task is not null ? (task.Checked ? "☑" : "☐") : list.IsOrdered ? $"{number++}." : "•";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new TextBlock { Text = marker, Foreground = b.Accent, FontSize = b.FontSize, Margin = new Thickness(4, 0, 8, 0), MinWidth = 18 });
            var content = Container(item, b);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Border RenderTable(MdTable table, SkinBrushes b)
    {
        var modern = b.IsModern;
        var radius = new CornerRadius(modern ? 6 : 0);
        var grid = new Grid();
        var columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        var rows = table.OfType<TableRow>().Count();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var inline = cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline;
                var text = Text(inline, b, weight: row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal, foreground: row.IsHeader ? b.HeaderForeground : null);
                var border = new Border
                {
                    BorderBrush = b.PanelBorder,
                    BorderThickness = new Thickness(0, 0, col == columns - 1 ? 0 : 1, rowIndex == rows - 1 ? 0 : 1),
                    Padding = new Thickness(10, 5),
                    Background = row.IsHeader ? b.HeaderBackground : null,
                    Child = text,
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, col++);
                grid.Children.Add(border);
            }

            rowIndex++;
        }

        return new Border
        {
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = radius,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
    }

    private static SelectableTextBlock Text(ContainerInline? inline, SkinBrushes b, double? size = null, FontWeight? weight = null, IBrush? foreground = null, Thickness? margin = null)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? b.FileForeground,
            FontSize = size ?? b.FontSize,
            FontWeight = weight ?? FontWeight.Normal,
            Margin = margin ?? default,
        };
        if (inline is not null)
        {
            block.Inlines ??= [];
            foreach (var child in inline)
            {
                AddInline(block.Inlines, child, b);
            }
        }

        return block;
    }

    private static void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, SkinBrushes b)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case EmphasisInline emphasis:
                var span = new Span();
                if (emphasis.DelimiterChar == '~')
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }
                else if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeight.Bold;
                }
                else
                {
                    span.FontStyle = FontStyle.Italic;
                }

                foreach (var child in emphasis)
                {
                    AddInline(span.Inlines, child, b);
                }

                target.Add(span);
                break;
            case CodeInline code:
                target.Add(new Run(code.Content) { FontFamily = b.MonospaceFamily, Background = b.CodeBackground, Foreground = b.FileForeground });
                break;
            case LinkInline { IsImage: true } image:
                target.Add(new Run(Loc.T("[obraz: {0}]", image.Url ?? string.Empty)) { Foreground = b.MutedForeground, FontStyle = FontStyle.Italic });
                break;
            case LinkInline link:
                var linkSpan = new Span { Foreground = b.Accent, TextDecorations = TextDecorations.Underline };
                foreach (var child in link)
                {
                    AddInline(linkSpan.Inlines, child, b);
                }

                if (linkSpan.Inlines.Count == 0)
                {
                    linkSpan.Inlines.Add(new Run(link.Url ?? string.Empty));
                }

                target.Add(linkSpan);
                break;
            case AutolinkInline autolink:
                target.Add(new Run(autolink.Url) { Foreground = b.Accent, TextDecorations = TextDecorations.Underline });
                break;
            case LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;
            case TaskList:
                break; // Rendered as the list marker.
            case HtmlInline html:
                target.Add(new Run(html.Tag) { Foreground = b.HiddenForeground });
                break;
            case ContainerInline container:
                foreach (var child in container)
                {
                    AddInline(target, child, b);
                }

                break;
        }
    }
}
