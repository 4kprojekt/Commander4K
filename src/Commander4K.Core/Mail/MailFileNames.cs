using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using MailKit;
using MimeKit;

namespace Commander4K.Core.Mail;

/// <summary>Local names of messages and folders: valid on Windows, unique through the UID, bounded in length.</summary>
public static partial class MailFileNames
{
    public const string UnreadPrefix = "● ";

    private const int MaxSender = 40;
    private const int MaxSubject = 80;

    /// <summary>The read form (<c>yyyy-MM-dd HHmm sender - subject [uid N].eml</c>); unread messages get <see cref="UnreadPrefix"/>.</summary>
    public static string For(DateTimeOffset date, string from, string subject, uint uid)
    {
        var sender = Segment(SenderName(from), MaxSender);
        if (sender.Length == 0)
        {
            sender = "_";
        }

        var subj = Segment(subject, MaxSubject);
        if (subj.Length == 0)
        {
            subj = Loc.T("(bez tematu)");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd HHmm} {sender} - {subj} [uid {uid}].eml");
    }

    public static bool TryParseUid(string fileName, out uint uid)
    {
        uid = 0;
        var m = UidSuffix().Match(fileName);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uid);
    }

    public static string LocalFolderName(string serverName, char separator, MailSpecialFolder special)
    {
        if (special != MailSpecialFolder.None)
        {
            return special switch
            {
                MailSpecialFolder.Inbox => Loc.T("Odebrane"),
                MailSpecialFolder.Sent => Loc.T("Wysłane"),
                MailSpecialFolder.Drafts => Loc.T("Szkice"),
                MailSpecialFolder.Trash => Loc.T("Kosz"),
                MailSpecialFolder.Archive => Loc.T("Archiwum"),
                _ => Loc.T("Spam"),
            };
        }

        var parts = separator == '\0' ? [serverName] : serverName.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var leaves = parts.Select(p => Segment(p, 80)).Where(p => p.Length > 0).ToArray();
        return leaves.Length == 0 ? "_" : string.Join('\\', leaves);
    }

    public static MailSpecialFolder Classify(string serverName, FolderAttributes attributes)
    {
        if (string.Equals(serverName, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return MailSpecialFolder.Inbox;
        }

        if ((attributes & FolderAttributes.Sent) != 0) return MailSpecialFolder.Sent;
        if ((attributes & FolderAttributes.Drafts) != 0) return MailSpecialFolder.Drafts;
        if ((attributes & FolderAttributes.Trash) != 0) return MailSpecialFolder.Trash;
        if ((attributes & FolderAttributes.Archive) != 0) return MailSpecialFolder.Archive;
        if ((attributes & FolderAttributes.Junk) != 0) return MailSpecialFolder.Junk;

        return serverName.Trim().ToLowerInvariant() switch
        {
            "sent" or "sent items" or "sent messages" => MailSpecialFolder.Sent,
            "drafts" => MailSpecialFolder.Drafts,
            "trash" or "deleted items" => MailSpecialFolder.Trash,
            "archive" => MailSpecialFolder.Archive,
            "junk" or "spam" => MailSpecialFolder.Junk,
            _ => MailSpecialFolder.None,
        };
    }

    private static string SenderName(string from)
    {
        if (MailboxAddress.TryParse(from, out var mailbox))
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Name))
            {
                return mailbox.Name;
            }

            var at = mailbox.Address.IndexOf('@');
            return at > 0 ? mailbox.Address[..at] : mailbox.Address;
        }

        return from;
    }

    /// <summary>Replaces invalid characters, collapses whitespace, truncates (never inside a surrogate pair) and trims dots/spaces.</summary>
    private static string Segment(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsWhiteSpace(c) ? ' ' : Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var s = Spaces().Replace(sb.ToString(), " ").Trim();
        if (s.Length > max)
        {
            var cut = max;
            if (char.IsHighSurrogate(s[cut - 1]))
            {
                cut--;
            }

            s = s[..cut];
        }

        return s.Trim(' ', '.');
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\[uid (\d+)\]\.eml$", RegexOptions.IgnoreCase)]
    private static partial Regex UidSuffix();
}
