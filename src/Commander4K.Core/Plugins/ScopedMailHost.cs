using System.Globalization;
using System.Text.RegularExpressions;
using Commander4K.Core.Mail;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using MimeKit;

namespace Commander4K.Core.Plugins;

/// <summary>
/// The <see cref="IMailHost"/> one plugin sees: needs the <c>mail</c> permission plus the file permission of every path
/// involved, and audits each call. The audit detail names the account and counts, never message content or passwords.
/// Files are opened through the <see cref="VfsRouter"/>, so they may live anywhere the user can open.
/// </summary>
public sealed partial class ScopedMailHost(PluginManifest manifest, VfsRouter router, IMailBackend? backend, AuditLog audit) : IMailHost
{
    private const string Read = PluginPermissions.FileRead;
    private const string Write = PluginPermissions.FileWrite;

    private IMailBackend Backend => backend ?? throw new NotSupportedException("Mail is not available in this host.");

    public Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(MailAccount account, CancellationToken cancellationToken = default) =>
        Gate("mail.folders", Describe(account), [], () => Backend.FoldersAsync(manifest.Id, account, cancellationToken));

    public Task<MailSyncResult> SyncAsync(MailAccount account, MailSyncRequest request, CancellationToken cancellationToken = default) =>
        Gate("mail.sync", Describe(account, request.Folder, request.KnownUids.Count, "known uid"), [],
            () => Backend.SyncAsync(manifest.Id, account, request, cancellationToken));

    public Task<long> FetchAsync(MailAccount account, string folder, uint uid, string targetPath, CancellationToken cancellationToken = default) =>
        Gate("mail.fetch", Describe(account, folder, 1, "uid") + " → " + targetPath, [Write], async () =>
        {
            // Written next to the target and moved into place, so a half-downloaded message never looks like a finished one.
            var part = targetPath + ".part";
            try
            {
                long written;
                await using (var stream = await router.OpenWriteAsync(part, overwrite: true, cancellationToken).ConfigureAwait(false))
                {
                    written = await Backend.FetchAsync(manifest.Id, account, folder, uid, stream, cancellationToken).ConfigureAwait(false);
                }

                await router.MoveAsync(part, targetPath, overwrite: true, cancellationToken).ConfigureAwait(false);
                return written;
            }
            catch
            {
                await TryDeleteAsync(part).ConfigureAwait(false);
                throw;
            }
        });

    public Task SetFlagsAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken cancellationToken = default) =>
        Gate("mail.setFlags", Describe(account, folder, uids.Count, "uid") + $" +{add} -{remove}", [], async () =>
        {
            await Backend.SetFlagsAsync(manifest.Id, account, folder, uids, add, remove, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task MoveAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken cancellationToken = default) =>
        Gate("mail.move", Describe(account, folder, uids.Count, "uid") + " → " + targetFolder, [], async () =>
        {
            await Backend.MoveAsync(manifest.Id, account, folder, uids, targetFolder, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task DeleteAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default) =>
        Gate("mail.delete", Describe(account, folder, uids.Count, "uid"), [], async () =>
        {
            await Backend.DeleteAsync(manifest.Id, account, folder, uids, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task<uint?> AppendAsync(MailAccount account, string folder, string path, MailFlags flags, CancellationToken cancellationToken = default) =>
        Gate("mail.append", Describe(account, folder, 1, "file") + " ← " + path, [Read], async () =>
        {
            await using var stream = await router.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            return await Backend.AppendAsync(manifest.Id, account, folder, stream, flags, cancellationToken).ConfigureAwait(false);
        });

    public Task<MailMessageView> ParseAsync(string path, CancellationToken cancellationToken = default) =>
        Gate("mail.parse", path, [Read], async () => MailDrafts.View(await LoadAsync(path, cancellationToken).ConfigureAwait(false)));

    public Task SaveAttachmentAsync(string path, int index, string targetPath, CancellationToken cancellationToken = default) =>
        Gate("mail.saveAttachment", $"{path}#{index.ToString(CultureInfo.InvariantCulture)} → {targetPath}", [Read, Write], async () =>
        {
            var message = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            var entity = message.Attachments.ElementAtOrDefault(index)
                ?? throw new ArgumentException($"Attachment {index} not found.", nameof(index));

            // The target is the plugin's explicit choice; the attachment's own (untrusted) name is never used for a path.
            await using var stream = await router.OpenWriteAsync(targetPath, overwrite: true, cancellationToken).ConfigureAwait(false);
            if (entity is MimePart { Content: { } content })
            {
                await content.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await entity.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            return true;
        });

    public Task<MailDraft> CreateReplyAsync(string path, MailReplyMode mode, string selfAddress, CancellationToken cancellationToken = default) =>
        Gate("mail.reply", $"{mode} {path}", [Read], async () =>
            MailDrafts.CreateReply(await LoadAsync(path, cancellationToken).ConfigureAwait(false), path, mode, selfAddress));

    public Task ComposeAsync(string path, MailDraft draft, CancellationToken cancellationToken = default) =>
        Gate("mail.compose", $"{path} ({draft.AttachmentPaths.Count.ToString(CultureInfo.InvariantCulture)} attachments)",
            draft.AttachmentPaths.Count > 0 ? [Write, Read] : [Write], async () =>
            {
                // Attachments are read up front through the router, so a failure leaves no half-written draft.
                var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var attachment in draft.AttachmentPaths)
                {
                    var source = AttachmentSource(attachment);
                    if (!loaded.ContainsKey(source))
                    {
                        loaded[source] = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
                    }
                }

                var message = MailDrafts.Build(draft, source => new MemoryStream(loaded[source], writable: false));
                await using var stream = await router.OpenWriteAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
                await message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
                return true;
            });

    public Task<string> SendAsync(MailAccount account, string draftPath, CancellationToken cancellationToken = default) =>
        Gate("mail.send", $"{account.User}@{account.Imap} via {account.Smtp ?? "-"} ← {draftPath}", [Read], async () =>
        {
            await using var stream = await router.OpenReadAsync(draftPath, cancellationToken).ConfigureAwait(false);
            return await Backend.SendAsync(manifest.Id, account, stream, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>The file behind an attachment path: <c>x.eml#2</c> (an attachment of a forwarded message) is read from <c>x.eml</c>.</summary>
    private static string AttachmentSource(string attachment) =>
        EmlReference().Match(attachment) is { Success: true } m ? m.Groups[1].Value : attachment;

    private static string Describe(MailAccount account) => $"{account.User}@{account.Imap}";

    private static string Describe(MailAccount account, string folder, int count, string unit) =>
        $"{account.User}@{account.Imap} {folder} {count.ToString(CultureInfo.InvariantCulture)} {unit}";

    private async Task<MimeMessage> LoadAsync(string path, CancellationToken ct)
    {
        await using var stream = await router.OpenReadAsync(path, ct).ConfigureAwait(false);
        return await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadAllAsync(string path, CancellationToken ct)
    {
        await using var stream = await router.OpenReadAsync(path, ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    private async Task TryDeleteAsync(string path)
    {
        try
        {
            await router.DeleteAsync(path, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best effort: the leftover is a ".part" file the sync never reads.
        }
    }

    private async Task<T> Gate<T>(string operation, string detail, string[] filePermissions, Func<Task<T>> action)
    {
        foreach (var permission in (string[])[PluginPermissions.Mail, .. filePermissions])
        {
            if (!manifest.Allows(permission))
            {
                audit.Record(manifest.Id, operation, detail, AuditOutcome.Denied);
                throw new PluginPermissionException(manifest.Id, permission);
            }
        }

        try
        {
            var result = await action().ConfigureAwait(false);
            audit.Record(manifest.Id, operation, detail);
            return result;
        }
        catch (Exception ex) when (ex is not PluginPermissionException)
        {
            audit.Record(manifest.Id, operation, $"{detail} → {ex.GetType().Name}: {ex.Message}", AuditOutcome.Failed);
            throw;
        }
    }

    [GeneratedRegex(@"^(.+\.eml)#(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EmlReference();
}
