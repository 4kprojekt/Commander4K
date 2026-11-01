using System.Security.Authentication;

namespace Commander4K.PluginSdk;

/// <param name="Imap"><c>"imap.host:993"</c>.</param>
/// <param name="Smtp"><c>"smtp.host:465"</c>; <c>null</c> = this account cannot send.</param>
/// <param name="Interactive"><c>false</c> never shows dialogs (background work).</param>
public sealed record MailAccount(string Imap, string? Smtp, string User, bool Interactive = true);

[Flags]
public enum MailFlags
{
    None = 0,
    Seen = 1,
    Flagged = 2,
    Answered = 4,
    Draft = 8,
}

public enum MailSpecialFolder
{
    None,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Archive,
    Junk,
}

public sealed record MailFolderInfo(string ServerName, string LocalName, char Separator, MailSpecialFolder Special, uint UidValidity, int Count, int Unread);

public sealed record MailSyncRequest(string Folder, uint UidValidity, uint LastUid, ulong ModSeq, DateTimeOffset Since, IReadOnlyList<uint> KnownUids, int? Limit = null);

public sealed record MailHeader(uint Uid, DateTimeOffset Date, string From, string Subject, MailFlags Flags, long Size, string FileName);

public sealed record MailSyncResult(
    uint UidValidity,
    bool UidValidityChanged,
    ulong ModSeq,
    IReadOnlyList<MailHeader> New,
    IReadOnlyList<(uint Uid, MailFlags Flags)> FlagChanges,
    IReadOnlyList<uint> Removed,
    bool HasMore = false);

public sealed record MailAddress(string? Name, string Address);

public sealed record MailAttachment(int Index, string FileName, string ContentType, long Size);

public sealed record MailMessageView(
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string Subject,
    DateTimeOffset? Date,
    string? MessageId,
    IReadOnlyList<string> References,
    string BodyText,
    IReadOnlyList<MailAttachment> Attachments);

public enum MailReplyMode
{
    Reply,
    ReplyAll,
    Forward,
}

public sealed record MailDraft(
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    string Subject,
    string Body,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<string> AttachmentPaths,
    MailAddress? From = null);

/// <summary>
/// Mail through the app: connections, credentials and certificates stay with the host. Every member requires
/// <see cref="PluginPermissions.Mail"/>; the file permission noted per member is required as well.
/// </summary>
public interface IMailHost
{
    Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(MailAccount account, CancellationToken cancellationToken = default);

    Task<MailSyncResult> SyncAsync(MailAccount account, MailSyncRequest request, CancellationToken cancellationToken = default);

    /// <summary>Downloads a message to <paramref name="targetPath"/> (also requires fs.write); returns the bytes written.</summary>
    Task<long> FetchAsync(MailAccount account, string folder, uint uid, string targetPath, CancellationToken cancellationToken = default);

    Task SetFlagsAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken cancellationToken = default);

    Task MoveAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken cancellationToken = default);

    /// <summary>Moves to Trash; in Trash the messages are expunged. Outside Trash an account without a Trash folder throws <see cref="InvalidOperationException"/>.</summary>
    Task DeleteAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default);

    /// <summary>Uploads a message file (also requires fs.read); returns the new UID when the server reports it.</summary>
    Task<uint?> AppendAsync(MailAccount account, string folder, string path, MailFlags flags, CancellationToken cancellationToken = default);

    /// <summary>Parses a message file (also requires fs.read).</summary>
    Task<MailMessageView> ParseAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Saves an attachment of a message file (also requires fs.read and fs.write).</summary>
    Task SaveAttachmentAsync(string path, int index, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>Builds a reply or forward draft from a message file (also requires fs.read).</summary>
    Task<MailDraft> CreateReplyAsync(string path, MailReplyMode mode, string selfAddress, CancellationToken cancellationToken = default);

    /// <summary>Writes a draft as a message file (also requires fs.write, and fs.read for attachments).</summary>
    Task ComposeAsync(string path, MailDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Sends a draft file (also requires fs.read) and appends it to Sent; returns the Message-ID.</summary>
    Task<string> SendAsync(MailAccount account, string draftPath, CancellationToken cancellationToken = default);
}

/// <summary>The server refused the login and the account has no usable credentials (non-interactive calls).</summary>
public sealed class MailAuthenticationRequiredException(string realm)
    : UnauthorizedAccessException($"Mail login required for {realm}.")
{
    public string Realm { get; } = realm;
}

/// <summary>
/// The SMTP step ended without an answer (timeout, lost connection) after the server may already have accepted the
/// message: it may or may not have been sent.
/// </summary>
public sealed class MailSendOutcomeUnknownException(Exception inner)
    : Exception("The mail server did not confirm the message; it may have been sent.", inner);

/// <summary>The server certificate is not trusted yet.</summary>
public sealed class MailCertificateUntrustedException(string host, string fingerprint)
    : AuthenticationException($"Untrusted certificate of {host} ({fingerprint}).")
{
    public string Host { get; } = host;

    public string Fingerprint { get; } = fingerprint;
}
