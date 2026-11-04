using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.Preview;

/// <summary>
/// A <see cref="MailPreview"/> as the reader of the design "Tryb panelu — klient poczty": the plugin's actions with the
/// Treść · Tekst · Źródło switch, the subject, the sender with an avatar, attachment tiles, the text and a quick reply.
/// </summary>
internal static partial class MailReaderView
{
    /// <param name="actionButton">Builds a button for an action; the values function supplies form values (the quick reply's text).</param>
    public static Control Build(MailPreview mail, IReadOnlyList<PreviewAction> actions, SkinBrushes b,
        Func<PreviewAction, Func<IReadOnlyDictionary<string, string>>?, Control> actionButton, Action<string>? openLink = null)
    {
        var root = new DockPanel();
        var body = new ContentControl();
        var views = new List<string> { Loc.T("Treść"), Loc.T("Tekst") };
        if (mail.Source is not null)
        {
            views.Add(Loc.T("Źródło"));
        }

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconsStarted = false;
        foreach (var action in actions)
        {
            if (action.Style == "icon" && !iconsStarted && bar.Children.Count > 0)
            {
                bar.Children.Add(new Border { Width = 1, Height = 18, Background = b.PanelBorder, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            }

            iconsStarted |= action.Style == "icon";
            var button = actionButton(action, null);
            button.Margin = new Thickness(0, 2, 6, 2);
            bar.Children.Add(button);
        }

        var switcher = Ds.Segmented(b, views, 0, index => body.Content = Content(mail, b, index, openLink), onToolbar: true);
        switcher.VerticalAlignment = VerticalAlignment.Center;
        var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        barGrid.Children.Add(bar);
        Grid.SetColumn(switcher, 1);
        barGrid.Children.Add(switcher);
        var barBorder = new Border
        {
            MinHeight = 38,
            Padding = new Thickness(8, 4),
            BorderBrush = b.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = barGrid,
        };
        DockPanel.SetDock(barBorder, Dock.Top);
        root.Children.Add(barBorder);

        var page = new StackPanel { Spacing = 16, Margin = new Thickness(28, 20, 28, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        if (mail.Note is { Length: > 0 } note)
        {
            page.Children.Add(Ds.Notice(b, note));
        }

        page.Children.Add(new SelectableTextBlock { Text = mail.Subject, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = b.FileForeground, TextWrapping = TextWrapping.Wrap, LineHeight = 26 });
        page.Children.Add(Sender(mail, b));
        if (mail.Attachments.Count > 0)
        {
            page.Children.Add(Attachments(mail, b, actionButton));
        }

        page.Children.Add(new Border { Height = 1, Background = b.PanelBorder });
        body.Content = Content(mail, b, 0, openLink);
        page.Children.Add(body);
        if (mail.QuickReplyTarget is { } replyTarget)
        {
            page.Children.Add(QuickReply(mail, replyTarget, b, actionButton));
        }

        root.Children.Add(new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return root;
    }

    private static Control Sender(MailPreview mail, SkinBrushes b)
    {
        IBrush[] avatarColours = [b.Accent, b.NetworkForeground, b.ExecutableForeground, b.ArchiveForeground, b.DirectoryForeground, b.SelectedForeground];
        var name = mail.From.Length > 0 ? mail.From : mail.FromAddress ?? "?";
        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = avatarColours[(int)((uint)StableHash(mail.FromAddress ?? name) % avatarColours.Length)],
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = Initials(name),
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = b.PanelBackground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var who = new WrapPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(new SelectableTextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = b.FileForeground, Margin = new Thickness(0, 0, 6, 0) });
        if (mail.FromAddress is { Length: > 0 } address && address != name)
        {
            who.Children.Add(new SelectableTextBlock { Text = $"<{address}>", FontSize = 12, Foreground = b.MutedForeground, VerticalAlignment = VerticalAlignment.Bottom });
        }

        var lines = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 12, 0) };
        lines.Children.Add(who);
        if (mail.To is { Length: > 0 } to)
        {
            lines.Children.Add(new SelectableTextBlock { Text = Loc.T("do: {0}", to), FontSize = 12, Foreground = b.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(avatar);
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        if (mail.Date is { Length: > 0 } date)
        {
            var when = new TextBlock { Text = date, FontSize = 12, Foreground = b.MutedForeground, Margin = new Thickness(0, 2, 0, 0) };
            Grid.SetColumn(when, 2);
            row.Children.Add(when);
        }

        return row;
    }

    private static Control Attachments(MailPreview mail, SkinBrushes b, Func<PreviewAction, Func<IReadOnlyDictionary<string, string>>?, Control> actionButton)
    {
        var section = new StackPanel { Spacing = 6 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = Loc.T("Załączniki · {0}", mail.Attachments.Count), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = b.HeaderForeground });
        if (mail.Attachments.Any(a => a.Target is not null))
        {
            heading.Children.Add(new TextBlock { Text = Loc.T("kliknij, aby zapisać w drugim panelu"), FontSize = 12, Foreground = b.MutedForeground });
        }

        section.Children.Add(heading);
        var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var attachment in mail.Attachments)
        {
            var picture = IsPicture(attachment.Name);
            var icon = PreviewIcons.Create(picture ? PreviewIcons.Picture : PreviewIcons.Document, picture ? b.NetworkForeground : DocumentColour(attachment.Name, b), 20, 1.1);
            var text = new StackPanel { Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = attachment.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = b.FileForeground, TextTrimming = TextTrimming.CharacterEllipsis });
            if (attachment.Size is { Length: > 0 } size)
            {
                text.Children.Add(new TextBlock { Text = size, FontSize = 11, Foreground = b.MutedForeground });
            }

            var tile = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Center };
            tile.Children.Add(icon);
            Grid.SetColumn(text, 1);
            tile.Children.Add(text);
            if (attachment.Target is not null)
            {
                var download = PreviewIcons.Create(PreviewIcons.Download, b.MutedForeground, 14, 1.4);
                Grid.SetColumn(download, 2);
                tile.Children.Add(download);
            }

            var frame = new Border
            {
                Width = 230,
                Height = 40,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                BorderBrush = b.PanelBorder,
                BorderThickness = new Thickness(1),
                Background = b.HeaderBackground,
                Child = tile,
            };
            if (attachment.Target is { } target && actionButton(new PreviewAction(attachment.Name, target), null) is Button button)
            {
                // The tile is the button: the host's binding saves the attachment, the tile keeps the design's look.
                button.Content = frame;
                button.Height = double.NaN; // the kit's button height would clip the 40 px tile
                button.MinHeight = 0;
                button.Padding = new Thickness(0);
                button.Background = Brushes.Transparent;
                button.BorderThickness = new Thickness(0);
                button.Margin = new Thickness(0);
                ToolTip.SetTip(button, Loc.T("Zapisz „{0}”", attachment.Name));
                tiles.Children.Add(button);
            }
            else
            {
                tiles.Children.Add(frame);
            }
        }

        section.Children.Add(tiles);
        return section;
    }

    /// <summary>Treść: paragraphs at reading size; Tekst: the text as sent, monospaced; Źródło: the message source.</summary>
    private static Control Content(MailPreview mail, SkinBrushes b, int view, Action<string>? openLink)
    {
        if (view == 0)
        {
            var paragraphs = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
            var body = Entity().IsMatch(mail.Body) ? WebUtility.HtmlDecode(mail.Body) : mail.Body;
            foreach (var paragraph in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                paragraphs.Children.Add(Paragraph(paragraph.Trim('\n'), paragraph.StartsWith('>') ? b.MutedForeground : b.FileForeground, b, openLink));
            }

            return paragraphs;
        }

        var text = new SelectableTextBlock
        {
            Text = view == 1 ? mail.Body : mail.Source,
            FontFamily = b.MonospaceFamily,
            FontSize = 12.5,
            LineHeight = view == 1 ? 21 : 19,
            Foreground = b.FileForeground,
            TextWrapping = TextWrapping.Wrap,
        };
        return view == 1
            ? text
            : new Border { Padding = new Thickness(14, 12), BorderBrush = b.PanelBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = b.HeaderBackground, Child = text };
    }

    [GeneratedRegex(@"&(#\d+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);")]
    private static partial Regex Entity();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <summary>A paragraph whose http(s) addresses are underlined and open on a click (not after a drag that selects text).</summary>
    private static SelectableTextBlock Paragraph(string text, IBrush foreground, SkinBrushes b, Action<string>? openLink)
    {
        var block = new SelectableTextBlock
        {
            FontSize = 14,
            LineHeight = 23,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var links = new List<(int Start, int End, string Url)>();
        var last = 0;
        foreach (Match match in Url().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
            {
                url = url[..^1].TrimEnd('.', ',', ';', ':', '!', '?');
            }

            if (url.Length <= 8)
            {
                continue;
            }

            if (match.Index > last)
            {
                block.Inlines!.Add(new Run(text[last..match.Index]));
            }

            block.Inlines!.Add(new Run(url) { Foreground = b.Accent, TextDecorations = TextDecorations.Underline });
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            links.Add((match.Index, match.Index + url.Length, href));
            last = match.Index + url.Length;
        }

        if (links.Count == 0)
        {
            block.Text = text;
            return block;
        }

        if (last < text.Length)
        {
            block.Inlines!.Add(new Run(text[last..]));
        }

        string? UrlAt(PointerEventArgs e)
        {
            var hit = block.TextLayout.HitTestPoint(e.GetPosition(block));
            if (!hit.IsInside)
            {
                return null;
            }

            foreach (var (start, end, url) in links)
            {
                if (hit.TextPosition >= start && hit.TextPosition < end)
                {
                    return url;
                }
            }

            return null;
        }

        Point? pressed = null;
        block.PointerPressed += (_, e) => pressed = e.GetCurrentPoint(block).Properties.IsLeftButtonPressed ? e.GetPosition(block) : null;
        block.PointerMoved += (_, e) => block.Cursor = UrlAt(e) is null ? null : new Cursor(StandardCursorType.Hand);
        block.PointerReleased += (_, e) =>
        {
            if (pressed is { } from && openLink is not null && Math.Abs(e.GetPosition(block).X - from.X) < 4 && Math.Abs(e.GetPosition(block).Y - from.Y) < 4
                && UrlAt(e) is { } url)
            {
                openLink(url);
            }

            pressed = null;
        };
        return block;
    }

    private static Control QuickReply(MailPreview mail, string target, SkinBrushes b, Func<PreviewAction, Func<IReadOnlyDictionary<string, string>>?, Control> actionButton)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 240,
            FontSize = 13,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Background = b.HeaderBackground,
            PlaceholderText = mail.QuickReplyPlaceholder ?? Loc.T("Odpowiedz…"),
        };
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = Loc.T("Szybka odpowiedź"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = b.HeaderForeground });
        panel.Children.Add(box);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var send = actionButton(new PreviewAction(Loc.T("Wyślij"), target, "Ctrl+Enter", "send", "primary"),
            () => new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = box.Text ?? string.Empty });
        row.Children.Add(send);
        if (mail.QuickReplyFrom is { Length: > 0 } from)
        {
            var sender = new TextBlock { Text = Loc.T("Wyśle z: {0}", from), FontSize = 12, Foreground = b.MutedForeground, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sender, 2);
            row.Children.Add(sender);
        }

        panel.Children.Add(row);
        return panel;
    }

    private static bool IsPicture(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".svg";

    private static IBrush DocumentColour(string name, SkinBrushes b) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => b.Error,
        ".xls" or ".xlsx" or ".csv" or ".ods" => b.ExecutableForeground,
        ".zip" or ".7z" or ".rar" or ".gz" => b.ArchiveForeground,
        _ => b.FileForeground,
    };

    private static string Initials(string name) =>
        string.Concat(name.Split([' ', '·', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    /// <summary>The same sender gets the same colour in every run (string.GetHashCode is randomised per process).</summary>
    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text.ToLowerInvariant())
        {
            hash = unchecked(hash * 31 + c);
        }

        return hash;
    }
}
