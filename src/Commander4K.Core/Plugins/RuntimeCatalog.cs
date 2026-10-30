using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Commander4K.Core.Execution;
using Commander4K.Core.Localization;
using Commander4K.Core.Tools;
using Commander4K.Core.Updates;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <param name="Executable">Full path of the interpreter, or <c>null</c> when not installed.</param>
public sealed record RuntimeInfo(string Type, string DisplayName, string? Executable, SemanticVersion? Version)
{
    public bool IsInstalled => Executable is not null;
}

/// <summary>
/// Which plugin runtimes are installed and in which version, whether a manifest's requirement is met, how to start a
/// plugin, and what to tell the user when something is missing.
/// </summary>
public static partial class RuntimeCatalog
{
    public static async Task<IReadOnlyList<RuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var detections = new[]
        {
            DetectAsync("node", "Node.js", ["node"], ["--version"], cancellationToken),
            DetectAsync("python", "Python", ["python", "py"], ["--version"], cancellationToken),
            DetectPowerShellAsync(cancellationToken),
            DetectDotnetAsync(cancellationToken),
        };
        var results = await Task.WhenAll(detections).ConfigureAwait(false);
        return [.. results, new RuntimeInfo("exe", Loc.T("Program Windows (exe)"), Loc.T("(system)"), null)];
    }

    /// <summary>Supports space-separated <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c>, <c>^X</c> and a bare <c>X.Y</c> (meaning &gt;=).</summary>
    public static bool Satisfies(SemanticVersion version, string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        foreach (var token in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = RangeRegex().Match(token);
            // A bare major ("18", "^7") means major.0.
            var text = match.Groups["version"].Value;
            if (!match.Success || !SemanticVersion.TryParse(text.Contains('.', StringComparison.Ordinal) ? text : text + ".0", out var bound))
            {
                return false;
            }

            var comparison = version.CompareTo(bound with { Prerelease = null, Build = null });
            var ok = match.Groups["op"].Value switch
            {
                ">=" or "" => comparison >= 0,
                ">" => comparison > 0,
                "<=" => comparison <= 0,
                "<" => comparison < 0,
                "=" => comparison == 0,
                "^" => comparison >= 0 && version.Major == bound.Major,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Why a plugin cannot start, or <c>null</c> when its runtime is ready.</summary>
    public static string? CheckRequirement(PluginRuntime requirement, IReadOnlyList<RuntimeInfo> runtimes)
    {
        var runtime = runtimes.FirstOrDefault(r => string.Equals(r.Type, requirement.Type, StringComparison.OrdinalIgnoreCase));
        if (runtime is null || !runtime.IsInstalled)
        {
            return Loc.T("Brak środowiska {0}. {1}", runtime?.DisplayName ?? requirement.Type, InstallHint(requirement.Type));
        }

        if (runtime.Version is { } version && !Satisfies(version, requirement.Version))
        {
            return Loc.T("{0} {1} nie spełnia wymagania „{2}”. {3}", runtime.DisplayName, version, requirement.Version, InstallHint(requirement.Type));
        }

        return null;
    }

    public static string InstallHint(string type) => type.ToLowerInvariant() switch
    {
        "node" => "Zainstaluj: winget install OpenJS.NodeJS.LTS",
        "python" => "Zainstaluj: winget install Python.Python.3.13",
        "powershell" => "Zainstaluj: winget install Microsoft.PowerShell",
        "dotnet" => "Zainstaluj: winget install Microsoft.DotNet.Runtime.10",
        _ => string.Empty,
    };

    /// <summary>Command line that starts the plugin with stdio redirected for the JSON-RPC connection.</summary>
    public static ProcessStartInfo BuildStartInfo(RuntimeInfo runtime, string entry, string folder)
    {
        var (file, arguments) = runtime.Type switch
        {
            "node" => (runtime.Executable!, new[] { entry }),
            "python" => (runtime.Executable!, Path.GetFileNameWithoutExtension(runtime.Executable!).Equals("py", StringComparison.OrdinalIgnoreCase)
                ? new[] { "-3", "-u", entry }
                : ["-u", entry]),
            "powershell" => (runtime.Executable!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry]),
            "dotnet" => entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? (runtime.Executable!, [entry]) : (entry, Array.Empty<string>()),
            _ => (entry, Array.Empty<string>()),
        };

        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = folder,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["C4K_PROTOCOL"] = ManifestReader.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        return info;
    }

    private static async Task<RuntimeInfo> DetectAsync(string type, string name, string[] candidates, string[] versionArgs, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            if (ExternalToolsService.ResolveExecutable(candidate + ".exe") is not { } executable)
            {
                continue;
            }

            // The Microsoft Store "python.exe" alias in WindowsApps only opens the Store; skip it.
            if (executable.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = await ReadVersionAsync(executable, versionArgs, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                return new RuntimeInfo(type, name, executable, version);
            }
        }

        return new RuntimeInfo(type, name, null, null);
    }

    private static async Task<RuntimeInfo> DetectPowerShellAsync(CancellationToken cancellationToken)
    {
        var pwsh = await DetectAsync("powershell", "PowerShell", ["pwsh"], ["--version"], cancellationToken).ConfigureAwait(false);
        if (pwsh.IsInstalled)
        {
            return pwsh;
        }

        // Windows PowerShell 5.1 ships with every Windows 10/11.
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(windowsPowerShell)
            ? new RuntimeInfo("powershell", "Windows PowerShell", windowsPowerShell, new SemanticVersion(5, 1, 0))
            : pwsh;
    }

    private static async Task<RuntimeInfo> DetectDotnetAsync(CancellationToken cancellationToken)
    {
        if (ExternalToolsService.ResolveExecutable("dotnet.exe") is not { } dotnet)
        {
            return new RuntimeInfo("dotnet", ".NET", null, null);
        }

        try
        {
            var result = await ProcessRunner.RunAsync(new ProcessRunRequest(dotnet, ["--list-runtimes"], Timeout: TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
            var newest = DotnetRuntimeRegex().Matches(result.Output)
                .Select(m => SemanticVersion.TryParse(m.Groups["version"].Value, out var v) ? v : (SemanticVersion?)null)
                .OfType<SemanticVersion>()
                .DefaultIfEmpty()
                .Max();
            return new RuntimeInfo("dotnet", ".NET", dotnet, newest == default ? null : newest);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new RuntimeInfo("dotnet", ".NET", null, null);
        }
    }

    private static async Task<SemanticVersion?> ReadVersionAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(new ProcessRunRequest(executable, arguments, Timeout: TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
            var match = VersionRegex().Match(result.Output + " " + result.Error);
            return match.Success && SemanticVersion.TryParse(match.Value, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<op>>=|<=|>|<|=|\^)?v?(?<version>\d+(\.\d+){0,2})$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"Microsoft\.NETCore\.App (?<version>\d+\.\d+\.\d+)")]
    private static partial Regex DotnetRuntimeRegex();
}
