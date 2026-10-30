using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Execution;

/// <summary>Runs programs on behalf of the host and plugins: captured UTF-8 output, timeout, tree kill on cancel.</summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directory = request.WorkingDirectory is { } dir && Directory.Exists(dir) ? dir : string.Empty;

        if (request.Elevated)
        {
            // Elevation needs ShellExecute ("runas"), which cannot redirect output: the program gets its own window.
            var elevated = new ProcessStartInfo(request.FileName) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory };
            foreach (var argument in request.Arguments)
            {
                elevated.ArgumentList.Add(argument);
            }

            try
            {
                using var _ = Process.Start(elevated);
                return new ProcessRunResult(0, "(uruchomiono jako administrator w osobnym oknie)", string.Empty);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Anulowano podniesienie uprawnień.", ex);
            }
        }

        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = directory,
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Nie udało się uruchomić {request.FileName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout ?? DefaultTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Path.GetFileName(request.FileName)} nie zakończył się w czasie {request.Timeout ?? DefaultTimeout}.");
            }

            throw;
        }
    }
}
