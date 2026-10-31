using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using Commander4K.PluginSdk;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;
using MailKitService = MailKit.MailService;

namespace Commander4K.Core.Mail;

/// <summary>The app's mail operations, not yet gated: <c>ScopedMailHost</c> checks permissions and audits each call.</summary>
public interface IMailBackend
{
    Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct);

    Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct);

    Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct);

    Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct);

    Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct);

    Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct);

    Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct);

    Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct);
}

public sealed record MailServiceOptions
{
    /// <summary>Limit of one network step (connect, login, command); fetch, append and send add 1 s per 100 KB.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxConnections { get; init; } = 2;

    /// <summary>Tests: <c>false</c> forces the KnownUids fallback even when the server offers CONDSTORE.</summary>
    internal bool UseCondStore { get; init; } = true;

    /// <summary>Tests: extra ports with implicit TLS (GreenMail's 3993/3465). Never enables plain text.</summary>
    internal IReadOnlySet<int> ImplicitTlsPorts { get; init; } = new HashSet<int>();
}

/// <summary>
/// IMAP/SMTP through MailKit. TLS is required (implicit on 993/465, STARTTLS elsewhere); certificates that fail validation
/// need the user's confirmation (<see cref="MailCertificateStore"/>). At most <see cref="MailServiceOptions.MaxConnections"/>
/// IMAP connections per (plugin, server, user), closed after <see cref="MailServiceOptions.IdleTimeout"/> idle. Every network
/// step has a timeout, reported as <see cref="TimeoutException"/>; the caller's token cancels any step.
/// </summary>
public sealed class MailService : IMailBackend, IAsyncDisposable
{
    private const double BytesPerSecond = 100 * 1024;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly MailCredentials _credentials;
    private readonly MailCertificateStore _certificates;
    private readonly TimeProvider _time;
    private readonly MailServiceOptions _options;
    private readonly ConcurrentDictionary<(string Plugin, string Server, string User), Pool> _pools = new();
    private readonly ITimer _sweeper;
    private readonly Action<string>? _warning;
    private volatile bool _disposed;

    /// <param name="warning">Receives problems that do not fail the call, in English (for the audit log): today only a
    /// failed copy to Sent after a successful send. Never receives a password.</param>
    public MailService(
        MailCredentials credentials,
        MailCertificateStore certificates,
        TimeProvider? time = null,
        MailServiceOptions? options = null,
        Action<string>? warning = null)
    {
        _warning = warning;
        _credentials = credentials;
        _certificates = certificates;
        _time = time ?? TimeProvider.System;
        _options = options ?? new MailServiceOptions();
        var period = TimeSpan.FromTicks(Math.Clamp(_options.IdleTimeout.Ticks / 5, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(1).Ticks));
        _sweeper = _time.CreateTimer(_ => SweepIdle(), null, period, period);
    }

    public Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct) =>
        ImapAsync<IReadOnlyList<MailFolderInfo>>(pluginId, account, async (client, op) =>
        {
            var result = new List<MailFolderInfo>();
            foreach (var (folder, special) in await ListAsync(client, op.Token).ConfigureAwait(false))
            {
                try
                {
                    await folder.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity, op.Token).ConfigureAwait(false);
                }
                catch (ImapCommandException)
                {
                    // The server refused STATUS for this folder: list it without counts.
                }

                result.Add(new MailFolderInfo(
                    folder.FullName,
                    MailFileNames.LocalFolderName(folder.FullName, folder.DirectorySeparator, special),
                    folder.DirectorySeparator,
                    special,
                    folder.UidValidity,
                    folder.Count,
                    folder.Unread));
            }

            return result;
        }, ct);

    public Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct) =>
        ImapAsync(pluginId, account, async (client, op) =>
        {
            var folder = await client.GetFolderAsync(request.Folder, op.Token).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, op.Token).ConfigureAwait(false);
            var validity = folder.UidValidity;
            var changed = request.UidValidity != 0 && request.UidValidity != validity;
            var lastUid = changed ? 0 : request.LastUid;
            var known = changed ? [] : request.KnownUids.Distinct().ToHashSet();

            // New: within Since, above LastUid.
            var inRange = await folder.SearchAsync(MailKit.Search.SearchQuery.DeliveredAfter(request.Since.Date), op.Token).ConfigureAwait(false);
            var fresh = inRange.Where(u => u.Id > lastUid && !known.Contains(u.Id)).ToList();
            var headers = new List<MailHeader>(fresh.Count);
            if (fresh.Count > 0)
            {
                // A large first sync gets more time: about 2 KB of envelope per message.
                op.AllowFor(fresh.Count * 2048L);
                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                    | MessageSummaryItems.Size | MessageSummaryItems.InternalDate;
                foreach (var s in await folder.FetchAsync(fresh, new FetchRequest(items), op.Token).ConfigureAwait(false))
                {
                    var date = s.Envelope?.Date ?? s.InternalDate ?? _time.GetUtcNow();
                    var from = s.Envelope?.From.Mailboxes.FirstOrDefault()?.ToString() ?? "";
                    var subject = s.Envelope?.Subject ?? "";
                    headers.Add(new MailHeader(s.UniqueId.Id, date, from, subject, ToFlags(s.Flags), (long)(s.Size ?? 0), MailFileNames.For(date, from, subject, s.UniqueId.Id)));
                }
            }

            // Known: which still exist (exact, so messages that fell out of Since are not "removed"), and their flags.
            var flagChanges = new List<(uint Uid, MailFlags Flags)>();
            var removed = new List<uint>();
            if (known.Count > 0)
            {
                var knownIds = known.Order().Select(u => new UniqueId(u)).ToList();
                var present = (await folder.SearchAsync(MailKit.Search.SearchQuery.Uids(knownIds), op.Token).ConfigureAwait(false)).Select(u => u.Id).ToHashSet();
                removed.AddRange(known.Where(u => !present.Contains(u)).Order());
                if (present.Count > 0)
                {
                    var condStore = _options.UseCondStore && (client.Capabilities & ImapCapabilities.CondStore) != 0 && request.ModSeq > 0;
                    var flagsRequest = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
                    if (condStore)
                    {
                        flagsRequest.ChangedSince = request.ModSeq;
                    }

                    var ids = present.Order().Select(u => new UniqueId(u)).ToList();
                    foreach (var s in await folder.FetchAsync(ids, flagsRequest, op.Token).ConfigureAwait(false))
                    {
                        flagChanges.Add((s.UniqueId.Id, ToFlags(s.Flags)));
                    }
                }
            }

            return new MailSyncResult(validity, changed, folder.HighestModSeq, headers, flagChanges, removed);
        }, ct);

    public Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct) =>
        ImapAsync(pluginId, account, async (client, op) =>
        {
            var f = await OpenAsync(client, folder, FolderAccess.ReadOnly, op.Token).ConfigureAwait(false);
            var id = new UniqueId(uid);
            var summary = await f.FetchAsync([id], new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Size), op.Token).ConfigureAwait(false);
            if (summary.Count == 0)
            {
                throw new MessageNotFoundException($"Message {uid} is not in {folder}.");
            }

            op.AllowFor((long)(summary[0].Size ?? 0));
            await using var source = await f.GetStreamAsync(id, op.Token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, op.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), op.Token).ConfigureAwait(false);
                total += read;
            }

            return total;
        }, ct);

    public Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct) =>
        ImapAsync(pluginId, account, async (client, op) =>
        {
            var f = await OpenAsync(client, folder, FolderAccess.ReadWrite, op.Token).ConfigureAwait(false);
            var ids = Ids(uids);
            if (ids.Count > 0 && add != MailFlags.None)
            {
                await f.StoreAsync(ids, new StoreFlagsRequest(StoreAction.Add, ToMessageFlags(add)) { Silent = true }, op.Token).ConfigureAwait(false);
            }

            if (ids.Count > 0 && remove != MailFlags.None)
            {
                await f.StoreAsync(ids, new StoreFlagsRequest(StoreAction.Remove, ToMessageFlags(remove)) { Silent = true }, op.Token).ConfigureAwait(false);
            }

            return true;
        }, ct);

    public Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct) =>
        ImapAsync(pluginId, account, async (client, op) =>
        {
            var f = await OpenAsync(client, folder, FolderAccess.ReadWrite, op.Token).ConfigureAwait(false);
            var target = await client.GetFolderAsync(targetFolder, op.Token).ConfigureAwait(false);
            if (uids.Count > 0)
            {
                await f.MoveToAsync(Ids(uids), target, op.Token).ConfigureAwait(false);
            }

            return true;
        }, ct);

    public Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct) =>
        ImapAsync(pluginId, account, async (client, op) =>
        {
            if (uids.Count == 0)
            {
                return true;
            }

            var trash = (await ListAsync(client, op.Token).ConfigureAwait(false)).FirstOrDefault(x => x.Special == MailSpecialFolder.Trash).Folder;
            var f = await OpenAsync(client, folder, FolderAccess.ReadWrite, op.Token).ConfigureAwait(false);
            var ids = Ids(uids);
            if (trash is not null && !string.Equals(trash.FullName, f.FullName, StringComparison.Ordinal))
            {
                await f.MoveToAsync(ids, trash, op.Token).ConfigureAwait(false);
                return true;
            }

            // In Trash (or without one): expunge. Without UIDPLUS, EXPUNGE also removes other messages already marked \Deleted.
            await f.StoreAsync(ids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, op.Token).ConfigureAwait(false);
            if ((client.Capabilities & ImapCapabilities.UidPlus) != 0)
            {
                await f.ExpungeAsync(ids, op.Token).ConfigureAwait(false);
            }
            else
            {
                await f.ExpungeAsync(op.Token).ConfigureAwait(false);
            }

            return true;
        }, ct);

    public async Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct)
    {
        var size = message.CanSeek ? message.Length - message.Position : 0;
        var parsed = await MimeMessage.LoadAsync(message, ct).ConfigureAwait(false);
        return await ImapAsync(pluginId, account, async (client, op) =>
        {
            var f = await client.GetFolderAsync(folder, op.Token).ConfigureAwait(false);
            op.AllowFor(size);
            var uid = await f.AppendAsync(new AppendRequest(parsed, ToMessageFlags(flags)), op.Token).ConfigureAwait(false);
            return uid?.Id;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends over SMTP, then appends the message to the Sent folder (created when the account has none) as \Seen. Once SMTP
    /// accepted the message this never throws: a failed copy to Sent is reported through the constructor's warning hook.
    /// </summary>
    public async Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (account.Smtp is null)
        {
            throw new InvalidOperationException("The mail account has no SMTP server.");
        }

        var size = message.CanSeek ? message.Length - message.Position : 0;
        var parsed = await MimeMessage.LoadAsync(message, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(parsed.MessageId))
        {
            var domain = parsed.From.Mailboxes.FirstOrDefault()?.Domain;
            parsed.MessageId = MimeUtils.GenerateMessageId(string.IsNullOrEmpty(domain) ? MailCredentials.SmtpHost(account) : domain);
        }

        var endpoint = Endpoint(account.Smtp, 465);
        var password = await _credentials.GetSmtpPasswordAsync(pluginId, account, afterRefusal: false, ct).ConfigureAwait(false);
        var smtp = await ConnectAsync(() => new SmtpClient(), endpoint, account.Interactive, ct).ConfigureAwait(false);
        try
        {
            await AuthenticateAsync(smtp, pluginId, account, password, isSmtp: true, ct).ConfigureAwait(false);
            await TimedAsync(async op =>
            {
                op.AllowFor(size);
                return await smtp.SendAsync(parsed, op.Token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await CloseQuietlyAsync(smtp).ConfigureAwait(false);
        }

        // The message is sent; a failed copy to Sent must not make the caller send it again.
        try
        {
            await ImapAsync(pluginId, account, async (client, op) =>
            {
                var sent = (await ListAsync(client, op.Token).ConfigureAwait(false)).FirstOrDefault(x => x.Special == MailSpecialFolder.Sent).Folder
                    ?? await CreateSentAsync(client, op.Token).ConfigureAwait(false);
                op.AllowFor(size);
                return (await sent.AppendAsync(new AppendRequest(parsed, MessageFlags.Seen), op.Token).ConfigureAwait(false))?.Id;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure here (refused login, certificate, timeout, even the caller's cancellation) leaves Sent without the
            // copy; the send itself succeeded, so the Message-ID is still returned and the failure goes to the warning hook.
            Warn(string.Create(CultureInfo.InvariantCulture,
                $"Mail {parsed.MessageId} was sent for {MailCredentials.Realm(account)} (plugin {pluginId}), but the copy to Sent failed: {ex.GetType().Name}: {ex.Message}"));
        }

        return parsed.MessageId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _sweeper.DisposeAsync().ConfigureAwait(false);
        var closing = new List<Task>();
        foreach (var pool in _pools.Values)
        {
            closing.AddRange(pool.Shutdown().Select(CloseQuietlyAsync));
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
    }

    private async Task<T> ImapAsync<T>(string pluginId, MailAccount account, Func<ImapClient, Operation, Task<T>> body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpoint = Endpoint(account.Imap, 993);
        var key = (pluginId, $"{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}", account.User);
        var pool = _pools.GetOrAdd(key, _ => new Pool(_options.MaxConnections));
        await pool.Slots.WaitAsync(ct).ConfigureAwait(false);
        ImapClient? client = null;
        var reusable = false;
        try
        {
            client = await LeaseAsync(pool, ct).ConfigureAwait(false) ?? await OpenImapAsync(pluginId, account, endpoint, ct).ConfigureAwait(false);
            try
            {
                var result = await TimedAsync(op => body(client, op), ct).ConfigureAwait(false);
                reusable = true;
                return result;
            }
            catch (Exception ex) when (ex is CommandException or FolderNotFoundException or MessageNotFoundException)
            {
                // The server answered NO/BAD: the connection itself is fine.
                reusable = true;
                throw;
            }
        }
        finally
        {
            if (client is not null)
            {
                var pooled = reusable && client.IsConnected && client.IsAuthenticated && pool.Return(client, _time.GetUtcNow());
                if (!pooled)
                {
                    _ = CloseQuietlyAsync(client);
                }
            }

            pool.Slots.Release();
        }
    }

    /// <summary>An idle connection, checked with NOOP when it has rested a while; <c>null</c> when a new one is needed.</summary>
    private async Task<ImapClient?> LeaseAsync(Pool pool, CancellationToken ct)
    {
        while (pool.Take() is { } idle)
        {
            if (!idle.Client.IsConnected || !idle.Client.IsAuthenticated)
            {
                _ = CloseQuietlyAsync(idle.Client);
                continue;
            }

            if (_time.GetUtcNow() - idle.Since < StaleAfter)
            {
                return idle.Client;
            }

            try
            {
                await TimedAsync(async op =>
                {
                    await idle.Client.NoOpAsync(op.Token).ConfigureAwait(false);
                    return true;
                }, ct).ConfigureAwait(false);
                return idle.Client;
            }
            catch (Exception ex) when (ex is ProtocolException or CommandException or IOException or SocketException or TimeoutException or ServiceNotConnectedException)
            {
                _ = CloseQuietlyAsync(idle.Client);
            }
            catch (OperationCanceledException)
            {
                _ = CloseQuietlyAsync(idle.Client);
                throw;
            }
        }

        return null;
    }

    private async Task<ImapClient> OpenImapAsync(string pluginId, MailAccount account, MailEndpoint endpoint, CancellationToken ct)
    {
        // The password first: a background account without one fails here, before any network traffic.
        var password = await _credentials.GetImapPasswordAsync(pluginId, account, afterRefusal: false, ct).ConfigureAwait(false);
        var client = await ConnectAsync(() => new ImapClient(), endpoint, account.Interactive, ct).ConfigureAwait(false);
        try
        {
            await AuthenticateAsync(client, pluginId, account, password, isSmtp: false, ct).ConfigureAwait(false);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<TClient> ConnectAsync<TClient>(Func<TClient> create, MailEndpoint endpoint, bool interactive, CancellationToken ct)
        where TClient : MailKitService
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = create();
            client.Timeout = (int)Math.Min(int.MaxValue, _options.OperationTimeout.TotalMilliseconds);
            string? untrusted = null;
            client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                {
                    return true;
                }

                if (certificate is null)
                {
                    return false;
                }

                var fingerprint = MailCertificateStore.Fingerprint(certificate);
                if (_certificates.IsTrusted(endpoint.Host, endpoint.Port, fingerprint))
                {
                    return true;
                }

                untrusted = fingerprint;
                return false;
            };

            try
            {
                await TimedAsync(async op =>
                {
                    await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security, op.Token).ConfigureAwait(false);
                    return true;
                }, ct).ConfigureAwait(false);
                return client;
            }
            catch (SslHandshakeException) when (untrusted is not null)
            {
                client.Dispose();
                // Asked outside the network timeout: the user may take a while to decide.
                if (attempt > 0 || !await _certificates.ValidateAsync(endpoint.Host, endpoint.Port, untrusted, interactive).WaitAsync(ct).ConfigureAwait(false))
                {
                    throw new MailCertificateUntrustedException($"{endpoint.Host}:{endpoint.Port}", untrusted);
                }
            }
            catch (Exception)
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>A refused login asks once more (interactive) or reports that a login is required (background).</summary>
    private async Task AuthenticateAsync(MailKitService client, string pluginId, MailAccount account, string password, bool isSmtp, CancellationToken ct)
    {
        for (var refused = false; ; refused = true)
        {
            try
            {
                await TimedAsync(async op =>
                {
                    await client.AuthenticateAsync(account.User, password, op.Token).ConfigureAwait(false);
                    return true;
                }, ct).ConfigureAwait(false);
                _credentials.Accept(pluginId, account, isSmtp, password);
                return;
            }
            catch (MailKitAuthenticationException) when (account.Interactive && !refused)
            {
                _credentials.Reject(pluginId, account, isSmtp, password);
                password = isSmtp
                    ? await _credentials.GetSmtpPasswordAsync(pluginId, account, afterRefusal: true, ct).ConfigureAwait(false)
                    : await _credentials.GetImapPasswordAsync(pluginId, account, afterRefusal: true, ct).ConfigureAwait(false);
            }
            catch (MailKitAuthenticationException)
            {
                _credentials.Reject(pluginId, account, isSmtp, password);
                throw new MailAuthenticationRequiredException(MailCredentials.Realm(account));
            }
        }
    }

    private async Task<T> TimedAsync<T>(Func<Operation, Task<T>> body, CancellationToken ct)
    {
        using var op = new Operation(_options.OperationTimeout, _time, ct);
        try
        {
            return await body(op).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (op.TimedOut && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"The mail server did not answer within {op.Limit.TotalSeconds:0.#} s."), ex);
        }
    }

    private MailEndpoint Endpoint(string hostPort, int defaultPort)
    {
        var endpoint = MailEndpoints.Parse(hostPort, defaultPort);
        return _options.ImplicitTlsPorts.Contains(endpoint.Port) ? endpoint with { Security = SecureSocketOptions.SslOnConnect } : endpoint;
    }

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string name, FolderAccess access, CancellationToken ct)
    {
        var folder = await client.GetFolderAsync(name, ct).ConfigureAwait(false);
        if (!folder.IsOpen || folder.Access < access)
        {
            await folder.OpenAsync(access, ct).ConfigureAwait(false);
        }

        return folder;
    }

    /// <summary>Selectable personal folders with their special use: SPECIAL-USE attributes first, then names; one folder per kind.</summary>
    private static async Task<List<(IMailFolder Folder, MailSpecialFolder Special)>> ListAsync(ImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = (await client.GetFoldersAsync(ns, StatusItems.None, subscribedOnly: false, ct).ConfigureAwait(false))
            .Where(f => (f.Attributes & (FolderAttributes.NonExistent | FolderAttributes.NoSelect)) == 0)
            .ToList();
        if (!folders.Any(f => string.Equals(f.FullName, client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            folders.Insert(0, client.Inbox);
        }

        // An empty name matches no fallback name, so that pass sees attributes only.
        Func<IMailFolder, MailSpecialFolder>[] passes =
        [
            f => string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase) ? MailSpecialFolder.Inbox : MailSpecialFolder.None,
            f => MailFileNames.Classify("", f.Attributes),
            f => MailFileNames.Classify(f.Name, FolderAttributes.None) is var k && k != MailSpecialFolder.Inbox ? k : MailSpecialFolder.None,
        ];
        var specials = new MailSpecialFolder[folders.Count];
        var taken = new HashSet<MailSpecialFolder>();
        foreach (var pass in passes)
        {
            for (var i = 0; i < folders.Count; i++)
            {
                if (specials[i] == MailSpecialFolder.None && pass(folders[i]) is var kind && kind != MailSpecialFolder.None && taken.Add(kind))
                {
                    specials[i] = kind;
                }
            }
        }

        return folders.Select((f, i) => (f, specials[i])).ToList();
    }

    private static async Task<IMailFolder> CreateSentAsync(ImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var root = await client.GetFolderAsync(ns.Path, ct).ConfigureAwait(false);
        return await root.CreateAsync("Sent", isMessageFolder: true, ct).ConfigureAwait(false) ?? throw new FolderNotFoundException("Sent");
    }

    private static List<UniqueId> Ids(IReadOnlyList<uint> uids) => uids.Distinct().Select(u => new UniqueId(u)).ToList();

    private static MailFlags ToFlags(MessageFlags? flags)
    {
        var f = flags ?? MessageFlags.None;
        var result = MailFlags.None;
        if ((f & MessageFlags.Seen) != 0) result |= MailFlags.Seen;
        if ((f & MessageFlags.Flagged) != 0) result |= MailFlags.Flagged;
        if ((f & MessageFlags.Answered) != 0) result |= MailFlags.Answered;
        if ((f & MessageFlags.Draft) != 0) result |= MailFlags.Draft;
        return result;
    }

    private static MessageFlags ToMessageFlags(MailFlags flags)
    {
        var result = MessageFlags.None;
        if ((flags & MailFlags.Seen) != 0) result |= MessageFlags.Seen;
        if ((flags & MailFlags.Flagged) != 0) result |= MessageFlags.Flagged;
        if ((flags & MailFlags.Answered) != 0) result |= MessageFlags.Answered;
        if ((flags & MailFlags.Draft) != 0) result |= MessageFlags.Draft;
        return result;
    }

    private void SweepIdle()
    {
        var limit = _time.GetUtcNow() - _options.IdleTimeout;
        foreach (var pool in _pools.Values)
        {
            foreach (var client in pool.TakeOlderThan(limit))
            {
                _ = CloseQuietlyAsync(client);
            }
        }
    }

    private void Warn(string message)
    {
        try
        {
            _warning?.Invoke(message);
        }
        catch (Exception)
        {
            // A failing hook must not turn a delivered message into a failed send.
        }
    }

    /// <summary>LOGOUT/QUIT with a short limit, then dispose; never throws (it runs fire-and-forget from the pool and the timer).</summary>
    private static async Task CloseQuietlyAsync(MailKitService client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.DisconnectAsync(quit: true, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Closing anyway: nothing on this disposal path may fault an unobserved task.
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                // Already broken; nothing left to release.
            }
        }
    }

    private sealed class Pool(int max)
    {
        private readonly Lock _gate = new();
        private readonly List<(ImapClient Client, DateTimeOffset Since)> _idle = [];
        private bool _shutdown;

        public SemaphoreSlim Slots { get; } = new(max, max);

        public (ImapClient Client, DateTimeOffset Since)? Take()
        {
            lock (_gate)
            {
                if (_idle.Count == 0)
                {
                    return null;
                }

                var last = _idle[^1];
                _idle.RemoveAt(_idle.Count - 1);
                return last;
            }
        }

        /// <summary><c>false</c> after shutdown: the caller closes the client.</summary>
        public bool Return(ImapClient client, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_shutdown)
                {
                    return false;
                }

                _idle.Add((client, now));
                return true;
            }
        }

        public List<ImapClient> TakeOlderThan(DateTimeOffset limit)
        {
            lock (_gate)
            {
                var old = _idle.Where(x => x.Since <= limit).Select(x => x.Client).ToList();
                _idle.RemoveAll(x => x.Since <= limit);
                return old;
            }
        }

        public List<ImapClient> Shutdown()
        {
            lock (_gate)
            {
                _shutdown = true;
                var all = _idle.Select(x => x.Client).ToList();
                _idle.Clear();
                return all;
            }
        }
    }

    private sealed class Operation : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;
        private readonly TimeSpan _base;

        public Operation(TimeSpan timeout, TimeProvider time, CancellationToken ct)
        {
            _base = timeout;
            Limit = timeout;
            _timeout = new CancellationTokenSource(timeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _timeout.Token);
        }

        public CancellationToken Token => _linked.Token;

        public bool TimedOut => _timeout.IsCancellationRequested;

        public TimeSpan Limit { get; private set; }

        /// <summary>Restarts the limit as base + 1 s per 100 KB of <paramref name="bytes"/> (fetch, append, send).</summary>
        public void AllowFor(long bytes)
        {
            Limit = _base + TimeSpan.FromSeconds(Math.Max(0, bytes) / BytesPerSecond);
            if (!_timeout.IsCancellationRequested)
            {
                _timeout.CancelAfter(Limit);
            }
        }

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }
}
