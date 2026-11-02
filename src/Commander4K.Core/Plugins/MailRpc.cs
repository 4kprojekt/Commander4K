using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// The <c>mail.*</c> requests of the plugin protocol: JSON (camelCase) in, <see cref="IMailHost"/> call, JSON out.
/// Flags travel as <c>seen|flagged|answered|draft</c>. A missing or refused login becomes error <c>-32010</c>
/// (<c>data: { authRequired: true, realm }</c>), an untrusted certificate <c>-32011</c> (<c>data: { host, fingerprint }</c>).
/// </summary>
public static class MailRpc
{
    public static bool Handles(string method) => method.StartsWith("mail.", StringComparison.Ordinal);

    public static async Task<object?> HandleAsync(string method, JsonElement? p, IMailHost mail, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(method, p ?? default, mail, ct).ConfigureAwait(false);
        }
        catch (MailAuthenticationRequiredException ex)
        {
            throw new JsonRpcException(JsonRpcException.MailAuthenticationRequired, ex.Message, new { authRequired = true, realm = ex.Realm });
        }
        catch (MailSendOutcomeUnknownException ex)
        {
            throw new JsonRpcException(JsonRpcException.MailSendOutcomeUnknown, ex.Message, new { outcomeUnknown = true });
        }
        catch (MailCertificateUntrustedException ex)
        {
            throw new JsonRpcException(JsonRpcException.MailCertificateUntrusted, ex.Message, new { host = ex.Host, fingerprint = ex.Fingerprint });
        }
    }

    private static async Task<object?> DispatchAsync(string method, JsonElement p, IMailHost mail, CancellationToken ct)
    {
        switch (method)
        {
            case "mail.folders":
                return (await mail.FoldersAsync(Account(p), ct).ConfigureAwait(false)).Select(f => new
                {
                    serverName = f.ServerName,
                    localName = f.LocalName,
                    separator = f.Separator.ToString(),
                    special = f.Special.ToString().ToLowerInvariant(),
                    uidValidity = f.UidValidity,
                    count = f.Count,
                    unread = f.Unread,
                }).ToList();
            case "mail.sync":
                var request = new MailSyncRequest(
                    Str(p, "folder"),
                    UInt(p, "uidValidity", required: false),
                    UInt(p, "lastUid", required: false),
                    (ulong)Long(p, "modSeq", 0, 0, long.MaxValue),
                    OptStr(p, "since") is { Length: > 0 } since
                        ? DateTimeOffset.Parse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                        : DateTimeOffset.UtcNow.AddMonths(-3),
                    Uids(p, "knownUids", required: false),
                    p.ValueKind == JsonValueKind.Object && p.TryGetProperty("limit", out var limit) && limit.ValueKind != JsonValueKind.Null
                        ? (int)Long(p, "limit", null, 1, 100000)
                        : null);
                var sync = await mail.SyncAsync(Account(p), request, ct).ConfigureAwait(false);
                return new
                {
                    uidValidity = sync.UidValidity,
                    uidValidityChanged = sync.UidValidityChanged,
                    modSeq = sync.ModSeq,
                    @new = sync.New.Select(h => new
                    {
                        uid = h.Uid,
                        date = h.Date.ToString("O", CultureInfo.InvariantCulture),
                        from = h.From,
                        subject = h.Subject,
                        flags = FlagNames(h.Flags),
                        size = h.Size,
                        fileName = h.FileName,
                    }).ToList(),
                    flagChanges = sync.FlagChanges.Select(c => new { uid = c.Uid, flags = FlagNames(c.Flags) }).ToList(),
                    removed = sync.Removed,
                    hasMore = sync.HasMore,
                };
            case "mail.fetch":
                return new { bytes = await mail.FetchAsync(Account(p), Str(p, "folder"), UInt(p, "uid", required: true), Str(p, "path"), ct).ConfigureAwait(false) };
            case "mail.setFlags":
                await mail.SetFlagsAsync(Account(p), Str(p, "folder"), Uids(p, "uids"), Flags(p, "add"), Flags(p, "remove"), ct).ConfigureAwait(false);
                return null;
            case "mail.move":
                await mail.MoveAsync(Account(p), Str(p, "folder"), Uids(p, "uids"), Str(p, "target"), ct).ConfigureAwait(false);
                return null;
            case "mail.delete":
                await mail.DeleteAsync(Account(p), Str(p, "folder"), Uids(p, "uids"), ct).ConfigureAwait(false);
                return null;
            case "mail.append":
                return new { uid = await mail.AppendAsync(Account(p), Str(p, "folder"), Str(p, "path"), Flags(p, "flags"), ct).ConfigureAwait(false) };
            case "mail.parse":
                var view = await mail.ParseAsync(Str(p, "path"), ct).ConfigureAwait(false);
                return new
                {
                    from = view.From is { } from ? Address(from) : null,
                    to = view.To.Select(Address).ToList(),
                    cc = view.Cc.Select(Address).ToList(),
                    subject = view.Subject,
                    date = view.Date?.ToString("O", CultureInfo.InvariantCulture),
                    messageId = view.MessageId,
                    references = view.References,
                    bodyText = view.BodyText,
                    attachments = view.Attachments.Select(a => new { index = a.Index, fileName = a.FileName, contentType = a.ContentType, size = a.Size }).ToList(),
                };
            case "mail.saveAttachment":
                await mail.SaveAttachmentAsync(Str(p, "path"), (int)Long(p, "index", null, 0, int.MaxValue), Str(p, "target"), ct).ConfigureAwait(false);
                return null;
            case "mail.reply":
                var mode = Str(p, "mode").ToLowerInvariant() switch
                {
                    "reply" => MailReplyMode.Reply,
                    "replyall" => MailReplyMode.ReplyAll,
                    "forward" => MailReplyMode.Forward,
                    var other => throw new ArgumentException(Loc.T("Nieznany tryb odpowiedzi „{0}”.", other)),
                };
                return DraftDto(await mail.CreateReplyAsync(Str(p, "path"), mode, OptStr(p, "self") ?? string.Empty, ct).ConfigureAwait(false));
            case "mail.compose":
                await mail.ComposeAsync(Str(p, "path"), Draft(p.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.Object
                    ? draft
                    : throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "draft"))), ct).ConfigureAwait(false);
                return null;
            case "mail.send":
                return new { messageId = await mail.SendAsync(Account(p), Str(p, "path"), ct).ConfigureAwait(false) };
            case "mail.discover":
                var found = await mail.DiscoverAsync(Str(p, "email"), ct).ConfigureAwait(false);
                return new
                {
                    imap = Server(found.Imap),
                    smtp = Server(found.Smtp),
                    authMethods = found.AuthMethods,
                    source = found.Source,
                    confidence = found.Confidence,
                    requiresUserConfirmation = found.RequiresUserConfirmation,
                    confirmationReason = found.ConfirmationReason,
                    attempts = found.Attempts,
                };
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, Loc.T("Nieznana metoda „{0}”.", method));
        }
    }

    private static MailAccount Account(JsonElement p)
    {
        if (!p.TryGetProperty("account", out var a) || a.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "account"));
        }

        return new MailAccount(
            Str(a, "imap"),
            OptStr(a, "smtp") is { Length: > 0 } smtp ? smtp : null,
            Str(a, "user"),
            !(a.TryGetProperty("interactive", out var interactive) && interactive.ValueKind == JsonValueKind.False));
    }

    private static MailDraft Draft(JsonElement d) => new(
        Addresses(d, "to"),
        Addresses(d, "cc"),
        Addresses(d, "bcc"),
        OptStr(d, "subject") ?? string.Empty,
        OptStr(d, "body") ?? string.Empty,
        OptStr(d, "inReplyTo") is { Length: > 0 } reply ? reply : null,
        Strings(d, "references"),
        Strings(d, "attachments"),
        d.TryGetProperty("from", out var from) && from.ValueKind is JsonValueKind.Object or JsonValueKind.String ? ParseAddress(from) : null);

    private static object DraftDto(MailDraft d) => new
    {
        to = d.To.Select(Address).ToList(),
        cc = d.Cc.Select(Address).ToList(),
        bcc = d.Bcc.Select(Address).ToList(),
        subject = d.Subject,
        body = d.Body,
        inReplyTo = d.InReplyTo,
        references = d.References,
        attachments = d.AttachmentPaths,
        from = d.From is { } from ? Address(from) : null,
    };

    private static object? Server(MailServerSettings? s) =>
        s is null ? null : new { host = s.Host, port = s.Port, security = s.Security, username = s.Username };

    private static object Address(MailAddress a) => new { name = a.Name, address = a.Address };

    private static MailAddress ParseAddress(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? new MailAddress(null, e.GetString() ?? string.Empty)
        : new MailAddress(OptStr(e, "name") is { Length: > 0 } name ? name : null, Str(e, "address"));

    private static List<MailAddress> Addresses(JsonElement p, string name) =>
        p.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(ParseAddress)] : [];

    private static List<string> Strings(JsonElement p, string name) =>
        p.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    private static List<uint> Uids(JsonElement p, string name, bool required = true)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            return [.. list.EnumerateArray().Select(x => x.TryGetUInt32(out var uid) ? uid : throw new ArgumentException(Loc.T("Nieprawidłowy parametr „{0}”.", name)))];
        }

        return required ? throw new ArgumentException(Loc.T("Brak parametru „{0}”.", name)) : [];
    }

    private static MailFlags Flags(JsonElement p, string name)
    {
        var flags = MailFlags.None;
        foreach (var text in Strings(p, name))
        {
            flags |= text.ToLowerInvariant() switch
            {
                "seen" => MailFlags.Seen,
                "flagged" => MailFlags.Flagged,
                "answered" => MailFlags.Answered,
                "draft" => MailFlags.Draft,
                _ => throw new ArgumentException(Loc.T("Nieznana flaga „{0}”.", text)),
            };
        }

        return flags;
    }

    private static string[] FlagNames(MailFlags flags) =>
        [.. new[] { MailFlags.Seen, MailFlags.Flagged, MailFlags.Answered, MailFlags.Draft }
            .Where(f => (flags & f) != 0)
            .Select(f => f.ToString().ToLowerInvariant())];

    private static string Str(JsonElement p, string name) =>
        OptStr(p, name) ?? throw new ArgumentException(Loc.T("Brak parametru „{0}”.", name));

    private static string? OptStr(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static uint UInt(JsonElement p, string name, bool required) =>
        (uint)Long(p, name, required ? null : 0, 0, uint.MaxValue);

    /// <summary>An integer parameter in range; a missing one is an error unless <paramref name="fallback"/> is given.</summary>
    private static long Long(JsonElement p, string name, long? fallback, long min, long max)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return fallback ?? throw new ArgumentException(Loc.T("Brak parametru „{0}”.", name));
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= min && n <= max
            ? n
            : throw new ArgumentException(Loc.T("Nieprawidłowy parametr „{0}”.", name));
    }
}
