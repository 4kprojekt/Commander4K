using System.Globalization;
using System.Text.RegularExpressions;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using MimeKit;

namespace Commander4K.Core.Mail;

/// <summary>Replies, forwards and message views built from MimeKit messages. Pure: no network, no file access of its own.</summary>
public static partial class MailDrafts
{
    /// <param name="originalPath">Path of the original <c>.eml</c>; a forward lists its attachments as <c>path#index</c>.</param>
    public static MailDraft CreateReply(MimeMessage original, string originalPath, MailReplyMode mode, string selfAddress)
    {
        var view = View(original);
        var forward = mode == MailReplyMode.Forward;
        var subject = Prefixed(original.Subject, forward);
        var quote = Quote(view);

        var to = new List<MailAddress>();
        var cc = new List<MailAddress>();
        var attachments = new List<string>();
        string? inReplyTo = null;
        var references = new List<string>();

        if (forward)
        {
            for (var i = 0; i < view.Attachments.Count; i++)
            {
                attachments.Add(string.Create(CultureInfo.InvariantCulture, $"{originalPath}#{i}"));
            }
        }
        else
        {
            bool IsSelf(MailAddress a) => string.Equals(a.Address, selfAddress, StringComparison.OrdinalIgnoreCase);

            var target = (original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes).Select(ToAddress).ToList();
            to.AddRange(target.Where(a => !IsSelf(a)));
            if (to.Count == 0 && mode == MailReplyMode.Reply)
            {
                to.AddRange(target);
            }

            if (mode == MailReplyMode.ReplyAll)
            {
                var seen = new HashSet<string>(to.Select(a => a.Address), StringComparer.OrdinalIgnoreCase);
                foreach (var a in original.To.Mailboxes.Concat(original.Cc.Mailboxes).Select(ToAddress))
                {
                    if (!IsSelf(a) && seen.Add(a.Address))
                    {
                        cc.Add(a);
                    }
                }

                // Replying to our own message: the old recipients become the recipients.
                if (to.Count == 0 && cc.Count > 0)
                {
                    to.AddRange(cc);
                    cc.Clear();
                }
            }

            inReplyTo = original.MessageId;
            references.AddRange(original.References);
            if (!string.IsNullOrEmpty(original.MessageId))
            {
                references.Add(original.MessageId);
            }
        }

        return new MailDraft(to, cc, [], subject, quote, inReplyTo, references, attachments);
    }

    /// <summary>Splits the <c>&lt;file&gt;.eml#&lt;n&gt;</c> form (attachment <c>n</c> of that message file).</summary>
    public static bool TryParseEmlReference(string path, out string emlPath, out int index)
    {
        var m = EmlReference().Match(path);
        emlPath = m.Success ? m.Groups[1].Value : path;
        index = 0;
        return m.Success && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    public static MimeMessage Build(MailDraft draft, Func<string, Stream> openAttachment)
    {
        var message = new MimeMessage();
        if (draft.From is not null) message.From.Add(ToMailbox(draft.From));
        message.To.AddRange(draft.To.Select(ToMailbox));
        message.Cc.AddRange(draft.Cc.Select(ToMailbox));
        message.Bcc.AddRange(draft.Bcc.Select(ToMailbox));
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        if (!string.IsNullOrEmpty(draft.InReplyTo)) message.InReplyTo = draft.InReplyTo;
        foreach (var r in draft.References)
        {
            message.References.Add(r);
        }

        var body = new BodyBuilder { TextBody = draft.Body };
        foreach (var path in draft.AttachmentPaths)
        {
            if (TryParseEmlReference(path, out var emlPath, out var index))
            {
                using var stream = openAttachment(emlPath);
                var source = MimeMessage.Load(stream);
                var entity = AttachmentsOf(source).ElementAtOrDefault(index)
                    ?? throw new InvalidOperationException($"Attachment {index} not found.");
                body.Attachments.Add(entity);
            }
            else
            {
                using var stream = openAttachment(path);
                body.Attachments.Add(Path.GetFileName(path), stream);
            }
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    public static MailMessageView View(MimeMessage message)
    {
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(message.HtmlBody))
        {
            text = HtmlText.ToText(message.HtmlBody);
        }

        var attachments = AttachmentsOf(message).Select((entity, i) => entity switch
        {
            MimePart part => new MailAttachment(i, part.FileName ?? part.ContentType.Name ?? Fallback(part, i), part.ContentType.MimeType, DecodedLength(part)),
            MessagePart attached => new MailAttachment(i, attached.ContentDisposition?.FileName ?? attached.ContentType.Name ?? (attached.Message?.Subject is { Length: > 0 } s ? s + ".eml" : Fallback(attached, i)),
                attached.ContentType.MimeType, MessageLength(attached)),
            _ => new MailAttachment(i, entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? Fallback(entity, i), entity.ContentType.MimeType, 0),
        }).ToList();

        return new MailMessageView(
            message.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            message.To.Mailboxes.Select(ToAddress).ToList(),
            message.Cc.Mailboxes.Select(ToAddress).ToList(),
            message.Subject ?? "",
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            message.MessageId,
            message.References.ToList(),
            text ?? "",
            attachments);
    }

    /// <summary>
    /// The files of a message, in the order every index refers to (view, save, forward): parts marked as attachments,
    /// and also parts sent "inline" with a file name or a Content-ID (Apple Mail, Outlook, pictures in HTML), and
    /// attached messages. The text and HTML bodies themselves are not files.
    /// </summary>
    public static IReadOnlyList<MimeEntity> AttachmentsOf(MimeMessage message)
    {
        var text = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain && !p.IsAttachment && p.FileName is null);
        var html = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml && !p.IsAttachment && p.FileName is null);
        return [.. message.BodyParts.Where(entity => entity switch
        {
            MessagePart => true,
            MimePart part when ReferenceEquals(part, text) || ReferenceEquals(part, html) => false,
            MimePart part => part.IsAttachment || part.FileName is not null || (part is not TextPart && part.ContentId is not null),
            _ => false,
        })];
    }

    /// <summary>The size of the file itself, not of its base64 (about a third bigger).</summary>
    private static long DecodedLength(MimePart part)
    {
        if (part.Content is null)
        {
            return 0;
        }

        using var measure = new MimeKit.IO.MeasuringStream();
        part.Content.DecodeTo(measure);
        return measure.Length;
    }

    private static long MessageLength(MessagePart part)
    {
        if (part.Message is null)
        {
            return 0;
        }

        using var measure = new MimeKit.IO.MeasuringStream();
        part.Message.WriteTo(measure);
        return measure.Length;
    }

    /// <summary>A name for a file sent without one (a picture in HTML): its type's extension.</summary>
    private static string Fallback(MimeEntity entity, int index)
    {
        var extension = MimeTypes.TryGetExtension(entity.ContentType.MimeType, out var known) ? known : ".bin";
        return string.Create(CultureInfo.InvariantCulture, $"{Loc.T("załącznik")} {index + 1}{extension}");
    }

    private static string Quote(MailMessageView view)
    {
        var from = view.From is null ? "" : string.IsNullOrWhiteSpace(view.From.Name) ? view.From.Address : view.From.Name;
        var date = view.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        var header = Loc.T("{0}, {1} napisał(a):", date, from);
        var lines = view.BodyText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        return "\n\n" + header + "\n" + string.Join('\n', lines.Select(l => "> " + l));
    }

    private static string Prefixed(string? subject, bool forward)
    {
        var s = (subject ?? "").Trim();
        var pattern = forward ? FwdPrefix() : RePrefix();
        while (pattern.Match(s) is { Success: true } m)
        {
            s = s[m.Length..].TrimStart();
        }

        return (forward ? "Fwd: " : "Re: ") + s;
    }

    private static MailAddress ToAddress(MailboxAddress m) => new(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address);

    private static MailboxAddress ToMailbox(MailAddress a) => new(a.Name ?? "", a.Address);

    [GeneratedRegex(@"^(?:re|odp)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RePrefix();

    [GeneratedRegex(@"^(?:fwd?|pd)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FwdPrefix();

    [GeneratedRegex(@"^(.+\.eml)#(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EmlReference();
}
