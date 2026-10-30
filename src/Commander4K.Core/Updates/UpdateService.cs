using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Commander4K.Core.Updates;

public sealed record UpdateInfo(
    SemanticVersion Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    bool IsPrerelease,
    string? AssetName,
    string? AssetUrl,
    long AssetSize,
    string? ChecksumsUrl);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How the running copy was installed, which decides how it updates itself.</summary>
public enum InstallKind : byte
{
    /// <summary><c>dotnet run</c> or a framework-dependent folder: no self-update, link to the release page.</summary>
    Development,

    /// <summary>Unzipped single-file exe: the new exe is extracted from the release ZIP and swapped in.</summary>
    Portable,

    /// <summary>Installed by the MSI (marker file next to the exe): the new MSI is downloaded and run.</summary>
    Msi,
}

/// <summary>
/// Self-update from GitHub Releases. A release carries a portable ZIP and an MSI per architecture
/// (<c>Commander4K-win-x64.zip</c>, <c>Commander4K-win-arm64.msi</c>, ...) and a <c>SHA256SUMS.txt</c>; downloads are rejected
/// unless they match the published checksum. The running exe is replaced by renaming it to <c>.old</c> (Windows allows
/// renaming a running image) and moving the new file into place; the old file is removed on the next start.
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>Written next to the exe by the MSI.</summary>
    public const string MsiMarkerFileName = "Commander4K.installed";

    private const string ApiBase = "https://api.github.com";

    /// <summary>Release asset for this architecture and install kind, e.g. <c>Commander4K-win-arm64.zip</c>.</summary>
    public static string AssetNameFor(Architecture architecture, InstallKind kind = InstallKind.Portable) =>
        $"Commander4K-{(architecture == Architecture.Arm64 ? "win-arm64" : "win-x64")}{(kind == InstallKind.Msi ? ".msi" : ".zip")}";

    public static InstallKind DetectInstallKind(string? processPath)
    {
        if (processPath is null
            || !string.Equals(Path.GetFileName(processPath), "Commander4K.exe", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(Path.GetDirectoryName(processPath)!, "Commander4K.dll")))
        {
            return InstallKind.Development;
        }

        return File.Exists(Path.Combine(Path.GetDirectoryName(processPath)!, MsiMarkerFileName)) ? InstallKind.Msi : InstallKind.Portable;
    }

    public static bool CanSelfUpdate(string? processPath) => DetectInstallKind(processPath) != InstallKind.Development;

    /// <summary>Extracts <c>Commander4K.exe</c> from a downloaded portable ZIP into <paramref name="stagingDirectory"/>.</summary>
    public static string ExtractExecutable(string zipPath, string stagingDirectory)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "Commander4K.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new UpdateException("Archiwum aktualizacji nie zawiera Commander4K.exe.");
        var target = Path.Combine(stagingDirectory, "Commander4K.new.exe");
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    /// <summary>Starts the downloaded MSI (it upgrades the installed version in place; UAC prompts as needed).</summary>
    public static void StartMsiInstall(string msiPath)
    {
        var info = new System.Diagnostics.ProcessStartInfo("msiexec.exe") { UseShellExecute = true };
        info.ArgumentList.Add("/i");
        info.ArgumentList.Add(msiPath);
        info.ArgumentList.Add("/passive");
        using var _ = System.Diagnostics.Process.Start(info);
    }

    /// <summary>Returns the newest release above <paramref name="current"/>, or <c>null</c> when up to date.</summary>
    /// <exception cref="UpdateException">The repository is unreachable, missing, or rate-limited.</exception>
    public async Task<UpdateInfo?> CheckAsync(string repository, SemanticVersion current, bool includePrereleases, CancellationToken cancellationToken, InstallKind kind = InstallKind.Portable)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{repository}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Commander4K", current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Brak połączenia z GitHubem: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException($"Repozytorium „{repository}” nie istnieje albo nie jest publiczne.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateException("GitHub ograniczył liczbę zapytań. Spróbuj ponownie później.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub zwrócił błąd {(int)response.StatusCode}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return SelectNewest(document.RootElement, current, includePrereleases, kind);
        }
    }

    /// <summary>Downloads the release asset into <paramref name="stagingDirectory"/> and verifies its SHA-256.</summary>
    /// <param name="progress">Receives (bytes downloaded, total bytes).</param>
    public async Task<string> DownloadAsync(UpdateInfo update, string stagingDirectory, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        if (update.AssetUrl is null || update.AssetName is null)
        {
            throw new UpdateException($"Wydanie {update.Tag} nie zawiera pliku dla tej architektury.");
        }

        if (update.ChecksumsUrl is null)
        {
            throw new UpdateException($"Wydanie {update.Tag} nie zawiera {ChecksumsAssetName}; aktualizacja bez weryfikacji jest zablokowana.");
        }

        var expected = await FetchExpectedHashAsync(update.ChecksumsUrl, update.AssetName, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(stagingDirectory);
        var target = Path.Combine(stagingDirectory, update.AssetName);
        var partial = target + ".download";
        try
        {
            using var response = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.AssetSize;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"Suma kontrolna pobranego pliku się nie zgadza (oczekiwano {expected}, jest {actual}). Plik odrzucono.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Pobieranie nie powiodło się: {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>Swaps <paramref name="stagedExecutable"/> in for <paramref name="currentExecutable"/>; rolls back on failure.</summary>
    public static void Install(string stagedExecutable, string currentExecutable)
    {
        var old = currentExecutable + ".old";
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        File.Move(currentExecutable, old);
        try
        {
            File.Move(stagedExecutable, currentExecutable);
        }
        catch
        {
            File.Move(old, currentExecutable);
            throw;
        }
    }

    /// <summary>Removes the previous version left behind by <see cref="Install"/>; call on start-up.</summary>
    public static void CleanupPreviousVersion(string? currentExecutable)
    {
        if (currentExecutable is null)
        {
            return;
        }

        try
        {
            File.Delete(currentExecutable + ".old");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked (the old process is exiting); the next start will retry.
        }
    }

    private static UpdateInfo? SelectNewest(JsonElement releases, SemanticVersion current, bool includePrereleases, InstallKind kind)
    {
        UpdateInfo? best = null;
        var assetName = AssetNameFor(RuntimeInformation.ProcessArchitecture, kind);
        foreach (var release in releases.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString();
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (release.GetProperty("draft").GetBoolean()
                || (prerelease && !includePrereleases)
                || !SemanticVersion.TryParse(tag, out var version)
                || version <= current
                || (best is not null && version <= best.Version))
            {
                continue;
            }

            string? assetUrl = null, checksumsUrl = null;
            long assetSize = 0;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = url;
                    assetSize = asset.GetProperty("size").GetInt64();
                }
                else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = url;
                }
            }

            best = new UpdateInfo(
                version,
                tag!,
                release.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : tag!,
                release.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty,
                release.GetProperty("html_url").GetString()!,
                prerelease,
                assetUrl is null ? null : assetName,
                assetUrl,
                assetSize,
                checksumsUrl);
        }

        return best;
    }

    private async Task<string> FetchExpectedHashAsync(string checksumsUrl, string assetName, CancellationToken cancellationToken)
    {
        var text = await http.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // sha256sum format: "<hex>  <name>" (a '*' marks binary mode).
            var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*').Trim(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        throw new UpdateException($"{ChecksumsAssetName} nie zawiera sumy dla {assetName}.");
    }
}
