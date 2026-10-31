using System.Text;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Jobs;

/// <summary>Sends files from a file panel to a plugin panel (F5 onto a plugin panel).</summary>
public sealed class PanelReceiveJob(IPanelSession session, string title, PanelReceiver receiver, IReadOnlyList<TransferItem> items) : IJob
{
    public string Title { get; } = Loc.T("Wysyłanie {0} → {1}", Describe(items), receiver.Title);

    /// <summary>The plugin's answer; set once the job has finished.</summary>
    public ReceiveResult? Result { get; private set; }

    /// <summary>Title of the plugin panel the items were sent to.</summary>
    public string PanelTitle { get; } = title;

    public async Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken) =>
        Result = await session.ReceiveAsync(receiver.Id, items, new Relay(progress), cancellationToken).ConfigureAwait(false);

    private static string Describe(IReadOnlyList<TransferItem> items) =>
        items.Count == 1 ? Loc.T("„{0}”", items[0].Name) : Loc.P(items.Count, "{0} element|{0} elementy|{0} elementów");
}

/// <summary>Maps plugin progress to job metrics. Synchronous and cheap: external plugins report from the JSON-RPC reader thread.</summary>
internal sealed class Relay(IProgress<JobMetrics> target) : IProgress<TransferProgress>
{
    public void Report(TransferProgress value) =>
        target.Report(new JobMetrics(value.Bytes, 0, value.Done, value.Total, value.Text));
}

/// <summary>Copies files a plugin panel provides into a folder (F5 from a plugin panel onto a file panel).</summary>
/// <param name="audit">
/// Records (operation, English detail, outcome) under the providing plugin's id: every file written, skipped or failed.
/// </param>
public sealed class PanelProvideJob(
    IPanelSession session,
    PanelProvider provider,
    IReadOnlyList<string> rowIds,
    VfsRouter router,
    string targetFolder,
    OverwritePolicy overwrite,
    Func<Uri, CancellationToken, Task<Stream>> openUrl,
    Action<string, string, AuditOutcome>? audit = null) : IJob
{
    /// <summary>Inline content (<c>Data</c>/<c>Text</c>) above this size is refused; larger files must come as a url.</summary>
    public const int MaxInlineBytes = PanelTransferLimits.MaxInlineBytes;

    private const int BufferSize = 256 * 1024;

    private readonly List<string> _errors = [];

    public string Title { get; } = Loc.T("Pobieranie z „{0}” → {1}", provider.Title, targetFolder);

    public ProvideResult? Result { get; private set; }

    /// <summary>Files that were not written, each with the reason; they do not fail the job.</summary>
    public IReadOnlyList<string> Errors => _errors;

    public async Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        audit?.Invoke("provide", $"{provider.Id}: {rowIds.Count} rows -> {targetFolder} ({overwrite})", AuditOutcome.Info);
        var result = await session.ProvideAsync(provider.Id, rowIds, new Relay(progress), cancellationToken).ConfigureAwait(false);
        Result = result;

        // A target typed into the dialog that does not exist yet is created, as F5 between file panels does.
        if (result.Files.Count > 0 && await router.GetEntryAsync(targetFolder, cancellationToken).ConfigureAwait(false) is null)
        {
            await router.CreateDirectoryAsync(targetFolder, cancellationToken).ConfigureAwait(false);
        }

        var done = 0;
        long bytes = 0;
        foreach (var file in result.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobMetrics(bytes, 0, done, result.Files.Count, file.Name));
            bytes += await WriteAsync(file, cancellationToken).ConfigureAwait(false);
            progress.Report(new JobMetrics(bytes, 0, ++done, result.Files.Count, file.Name));
        }
    }

    private async Task<long> WriteAsync(ProvidedFile file, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(file.Error))
        {
            Fail(file.Name, file.Error, $"plugin error: {file.Error}");
            return 0;
        }

        if (!PluginAuthoring.IsSafeRelativePath(file.Name))
        {
            Fail(file.Name, Loc.T("niedozwolona nazwa pliku"), "unsafe file name");
            return 0;
        }

        var inline = file.Data;
        if (inline is null && file.Text is not null)
        {
            if (Encoding.UTF8.GetByteCount(file.Text) > MaxInlineBytes)
            {
                Fail(file.Name, TooLarge, "inline content over the limit");
                return 0;
            }

            inline = Encoding.UTF8.GetBytes(file.Text);
        }

        if (inline is { Length: > MaxInlineBytes })
        {
            Fail(file.Name, TooLarge, "inline content over the limit");
            return 0;
        }

        if (inline is null && (file.Url is null || file.Url.Scheme is not ("http" or "https")))
        {
            Fail(file.Name, Loc.T("brak zawartości lub niedozwolony adres"), "no content or url not allowed");
            return 0;
        }

        var parts = PluginAuthoring.SplitRelativePath(file.Name);
        var target = targetFolder;
        var createdTarget = false;
        var overwriting = false;
        try
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                target = router.Combine(target, parts[i]);
                if (await router.GetEntryAsync(target, cancellationToken).ConfigureAwait(false) is null)
                {
                    await router.CreateDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }

            target = router.Combine(target, parts[^1]);
            var existing = await router.GetEntryAsync(target, cancellationToken).ConfigureAwait(false);
            if (existing is { } found && (overwrite == OverwritePolicy.Skip || found.IsContainer))
            {
                Fail(file.Name, Loc.T("już istnieje, pominięto"), $"exists: {target}", "provide.skip", AuditOutcome.Info);
                return 0;
            }

            // The source is opened before the target, so a failing download never truncates an existing file.
            await using var input = inline is null ? await openUrl(file.Url!, cancellationToken).ConfigureAwait(false) : null;
            long size;
            await using (var output = await router.OpenWriteAsync(target, overwrite == OverwritePolicy.Overwrite, cancellationToken).ConfigureAwait(false))
            {
                createdTarget = existing is null;
                overwriting = existing is not null;
                if (inline is not null)
                {
                    await output.WriteAsync(inline, cancellationToken).ConfigureAwait(false);
                    size = inline.Length;
                }
                else
                {
                    size = await CopyAsync(input!, output, cancellationToken).ConfigureAwait(false);
                }
            }

            audit?.Invoke("provide.write", $"{target} ({size} B)", AuditOutcome.Allowed);
            return size;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DiscardAsync(createdTarget ? target : null).ConfigureAwait(false);
            if (overwriting)
            {
                Fail(file.Name, PartlyWritten(Loc.T("przerwano")), $"cancelled, existing file partly overwritten: {target}");
            }
            else
            {
                audit?.Invoke("provide.cancel", $"{file.Name}: {target}", AuditOutcome.Info);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only a file this job created is removed; an existing one that was overwritten keeps what was written.
            await DiscardAsync(createdTarget ? target : null).ConfigureAwait(false);
            if (overwriting)
            {
                Fail(file.Name, PartlyWritten(ex.Message), $"failed, existing file partly overwritten: {target}: {ex.Message}");
            }
            else
            {
                Fail(file.Name, ex.Message, $"failed: {target}: {ex.Message}");
            }

            return 0;
        }
    }

    private static string TooLarge => Loc.T("zawartość większa niż {0} MB", PanelTransferLimits.MaxInlineMegabytes);

    private static string PartlyWritten(string reason) =>
        Loc.T("{0} — istniejący plik został nadpisany tylko częściowo i może być niekompletny", reason);

    private static async Task<long> CopyAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        return total;
    }

    /// <summary>Removes a half-written file; it must not mask the real failure.</summary>
    private async Task DiscardAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (await router.GetEntryAsync(path, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                await router.DeleteAsync(path, recursive: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort with any provider (SFTP throws its own exceptions): the file's real error is what gets reported.
            audit?.Invoke("provide.discard", $"{path}: {ex.Message}", AuditOutcome.Failed);
        }
    }

    /// <param name="reason">For the user (localized).</param>
    /// <param name="auditDetail">For the audit log (English).</param>
    private void Fail(string name, string reason, string auditDetail, string operation = "provide.fail", AuditOutcome outcome = AuditOutcome.Failed)
    {
        _errors.Add(Loc.T("{0}: {1}", name, reason));
        audit?.Invoke(operation, $"{name}: {auditDetail}", outcome);
    }
}
