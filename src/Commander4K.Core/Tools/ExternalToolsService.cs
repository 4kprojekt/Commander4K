using System.ComponentModel;
using System.Diagnostics;

namespace Commander4K.Core.Tools;

/// <param name="Executable">Name on <c>PATH</c> or a path; <c>%VARIABLES%</c> are expanded.</param>
/// <param name="Arguments">Argument tokens; <c>{file}</c>, <c>{file2}</c> and <c>{dir}</c> are substituted, a token that is
/// exactly <c>{files}</c> becomes one argument per file. Each token is passed as one argument, so no manual quoting is needed.</param>
public sealed record ToolCandidate(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Content of <c>config/tools.json</c>: tool id to candidates tried in order until one exists.</summary>
public sealed class ExternalToolsConfig
{
    public Dictionary<string, List<ToolCandidate>> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Files">Substituted for a <c>{files}</c> token: one argument per file (may be empty).</param>
public readonly record struct ToolArguments(string? File = null, string? SecondFile = null, string? Directory = null, IReadOnlyList<string>? Files = null);

public sealed record ToolLaunchResult(bool Launched, string? Executable, int? ProcessId, string? Error)
{
    public static ToolLaunchResult NotFound(string toolId) =>
        new(false, null, null, $"No installed program found for tool '{toolId}'.");
}

/// <summary>
/// Launches external programs (editor, diff, terminal) without ever blocking or waiting on them. Executable lookup
/// touches the disk, so it runs on the thread pool.
/// </summary>
public sealed class ExternalToolsService(ExternalToolsConfig config)
{
    public const string EditTool = "edit";
    public const string DiffTool = "diff";
    public const string TerminalTool = "terminal";
    public const string VsCodeTool = "vscode";
    public const string SshTool = "ssh";

    private readonly ExternalToolsConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    public static ExternalToolsConfig CreateDefaultConfig() => new()
    {
        Tools = new(StringComparer.OrdinalIgnoreCase)
        {
            [EditTool] =
            [
                new(@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe", ["{file}"]),
                new(@"%ProgramFiles%\Microsoft VS Code\Code.exe", ["{file}"]),
                new(@"%ProgramFiles%\Notepad++\notepad++.exe", ["{file}"]),
                new("notepad.exe", ["{file}"]),
            ],
            [DiffTool] =
            [
                new(@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe", ["--diff", "{file}", "{file2}"]),
                new(@"%ProgramFiles%\Microsoft VS Code\Code.exe", ["--diff", "{file}", "{file2}"]),
                new(@"%ProgramFiles%\WinMerge\WinMergeU.exe", ["{file}", "{file2}"]),
            ],
            [TerminalTool] =
            [
                new("wt.exe", ["-d", "{dir}"]),
                new("pwsh.exe", ["-NoExit", "-WorkingDirectory", "{dir}"]),
                new("powershell.exe", ["-NoExit"]),
            ],
            // {files} = the ssh arguments (port, key, user, host, remote command).
            [SshTool] =
            [
                new("wt.exe", ["ssh.exe", "{files}"]),
                new("ssh.exe", ["{files}"]),
            ],
            // The folder as the workspace, plus the chosen files opened in it.
            [VsCodeTool] =
            [
                new(@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe", ["{dir}", "{files}"]),
                new(@"%ProgramFiles%\Microsoft VS Code\Code.exe", ["{dir}", "{files}"]),
                new(@"%LocalAppData%\Programs\Microsoft VS Code Insiders\Code - Insiders.exe", ["{dir}", "{files}"]),
                new("code", ["{dir}", "{files}"]),
                new("codium", ["{dir}", "{files}"]),
            ],
        },
    };

    public Task<ToolLaunchResult> EditAsync(string file) =>
        LaunchAsync(EditTool, new ToolArguments(File: file, Directory: Path.GetDirectoryName(file)));

    public Task<ToolLaunchResult> DiffAsync(string left, string right) =>
        LaunchAsync(DiffTool, new ToolArguments(File: left, SecondFile: right, Directory: Path.GetDirectoryName(left)));

    /// <param name="directory">Local or UNC directory the terminal starts in.</param>
    public Task<ToolLaunchResult> OpenTerminalAsync(string directory) =>
        LaunchAsync(TerminalTool, new ToolArguments(Directory: directory));

    /// <summary>Opens a terminal running OpenSSH's <c>ssh</c> with <paramref name="sshArguments"/> (colours are the terminal's).</summary>
    public Task<ToolLaunchResult> OpenSshTerminalAsync(IReadOnlyList<string> sshArguments) =>
        LaunchAsync(SshTool, new ToolArguments(Files: sshArguments));

    /// <summary>Opens <paramref name="directory"/> in VS Code (or VSCodium) with <paramref name="files"/> as editors.</summary>
    public Task<ToolLaunchResult> OpenInVsCodeAsync(string directory, IReadOnlyList<string> files) =>
        LaunchAsync(VsCodeTool, new ToolArguments(Directory: directory, Files: files));

    public Task<ToolLaunchResult> LaunchAsync(string toolId, ToolArguments arguments) =>
        Task.Run(() => Launch(toolId, arguments));

    /// <summary>
    /// The configured candidates; tools.json is written once, so tools added in later versions fall back to their
    /// defaults until the user adds them.
    /// </summary>
    internal IReadOnlyList<ToolCandidate>? CandidatesFor(string toolId) =>
        _config.Tools.TryGetValue(toolId, out var configured) ? configured
        : CreateDefaultConfig().Tools.TryGetValue(toolId, out var defaults) ? defaults
        : null;

    private ToolLaunchResult Launch(string toolId, ToolArguments arguments)
    {
        if (CandidatesFor(toolId) is not { } candidates)
        {
            return new ToolLaunchResult(false, null, null, $"Tool '{toolId}' is not configured.");
        }

        foreach (var candidate in candidates)
        {
            var executable = ResolveExecutable(candidate.Executable);
            if (executable is null)
            {
                continue;
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = arguments.Directory is { Length: > 0 } dir && Directory.Exists(dir) ? dir : string.Empty,
            };
            foreach (var token in candidate.Arguments)
            {
                if (string.Equals(token, "{files}", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in arguments.Files ?? [])
                    {
                        startInfo.ArgumentList.Add(file);
                    }

                    continue;
                }

                startInfo.ArgumentList.Add(Substitute(token, arguments));
            }

            // Inherited when Commander4K itself was started from VS Code's terminal: it would make Code.exe run the
            // file as a Node script instead of opening it.
            startInfo.Environment.Remove("ELECTRON_RUN_AS_NODE");

            try
            {
                using var process = Process.Start(startInfo);
                return new ToolLaunchResult(true, executable, process?.Id, null);
            }
            catch (Win32Exception ex)
            {
                return new ToolLaunchResult(false, executable, null, ex.Message);
            }
        }

        return ToolLaunchResult.NotFound(toolId);
    }

    private static string Substitute(string token, ToolArguments arguments) => token
        .Replace("{file}", arguments.File ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("{file2}", arguments.SecondFile ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("{dir}", arguments.Directory ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a configured executable to a full path via <c>PATH</c> and <c>PATHEXT</c>.</summary>
    internal static string? ResolveExecutable(string configured)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (Path.IsPathFullyQualified(expanded))
        {
            return File.Exists(expanded) ? expanded : null;
        }

        var extensions = Path.HasExtension(expanded)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, expanded + extension);
                }
                catch (ArgumentException)
                {
                    break; // Malformed PATH entry.
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
