using System.ComponentModel;
using System.Diagnostics;

namespace Commander4K.Core.Tools;

/// <param name="Executable">Name on <c>PATH</c> or a path; <c>%VARIABLES%</c> are expanded.</param>
/// <param name="Arguments">Argument tokens; <c>{file}</c>, <c>{file2}</c> and <c>{dir}</c> are substituted. Each token
/// is passed as one argument, so no manual quoting is needed.</param>
public sealed record ToolCandidate(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Content of <c>config/tools.json</c>: tool id to candidates tried in order until one exists.</summary>
public sealed class ExternalToolsConfig
{
    public Dictionary<string, List<ToolCandidate>> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct ToolArguments(string? File = null, string? SecondFile = null, string? Directory = null);

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
        },
    };

    public Task<ToolLaunchResult> EditAsync(string file) =>
        LaunchAsync(EditTool, new ToolArguments(File: file, Directory: Path.GetDirectoryName(file)));

    public Task<ToolLaunchResult> DiffAsync(string left, string right) =>
        LaunchAsync(DiffTool, new ToolArguments(File: left, SecondFile: right, Directory: Path.GetDirectoryName(left)));

    /// <param name="directory">Local or UNC directory the terminal starts in.</param>
    public Task<ToolLaunchResult> OpenTerminalAsync(string directory) =>
        LaunchAsync(TerminalTool, new ToolArguments(Directory: directory));

    public Task<ToolLaunchResult> LaunchAsync(string toolId, ToolArguments arguments) =>
        Task.Run(() => Launch(toolId, arguments));

    private ToolLaunchResult Launch(string toolId, ToolArguments arguments)
    {
        if (!_config.Tools.TryGetValue(toolId, out var candidates))
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
                startInfo.ArgumentList.Add(Substitute(token, arguments));
            }

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
