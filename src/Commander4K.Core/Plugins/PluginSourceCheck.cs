using System.Text.RegularExpressions;

namespace Commander4K.Core.Plugins;

/// <param name="Api">What the code uses, e.g. <c>open()</c> or <c>Get-Content</c>.</param>
/// <param name="Instead">The SDK call to use instead.</param>
public sealed record SourceFinding(string File, int Line, string Api, string Instead)
{
    public override string ToString() => $"{File}:{Line} — {Api} → {Instead}";
}

/// <summary>
/// Finds plugin code that talks to the system without the SDK (files, processes, network, registry, or writes to stdout,
/// which is the protocol channel). A plugin runs as the user's process, so this cannot enforce anything; it is a lint
/// that the AI installer, the AI fix request and the plugin manager use to point at what to change. The SDK client
/// files themselves are skipped; comment lines are ignored.
/// </summary>
public static partial class PluginSourceCheck
{
    private sealed record Rule(Regex Pattern, string Api, string Instead);

    private static readonly Rule[] Python =
    [
        new(Rx(@"(?<![\w.])open\s*\("), "open()", "c4k.read_text / read_bytes / write_text / write_bytes"),
        new(Rx(@"\bio\.open\s*\("), "io.open()", "c4k.read_text / write_text"),
        new(Rx(@"\bos\.(remove|unlink|rename|replace|mkdir|makedirs|rmdir|removedirs|listdir|scandir|walk|stat|system|popen|startfile|kill|chmod)\b"), "os.*", "c4k.list_dir / stat / make_dir / remove / move / run / kill_process"),
        new(Rx(@"\bshutil\."), "shutil", "c4k.move / remove / write_bytes"),
        new(Rx(@"\b(import|from)\s+(subprocess|socket|urllib|requests|httpx|aiohttp|http\.client|ftplib|smtplib|winreg|ctypes|psutil|webbrowser)\b"), "import of a system module", "c4k.run / http_request / list_processes / system_info / open_url"),
        new(Rx(@"\.(read_text|write_text|read_bytes|write_bytes|iterdir|unlink|rmdir|mkdir|touch|rglob|glob)\s*\("), "pathlib I/O", "c4k.read_text / write_text / list_dir"),
        new(Rx(@"(?<![\w.])print\s*\("), "print()", "c4k.log / notify / show_text (stdout is the protocol channel)"),
        new(Rx(@"\bsys\.stdout\.write\b"), "sys.stdout.write", "c4k.log (stdout is the protocol channel)"),
    ];

    private static readonly Rule[] Node =
    [
        new(Rx(@"(require\s*\(\s*|from\s+|import\s*\(\s*)['""](node:)?(fs|fs/promises|child_process|net|http|https|http2|dgram|tls|dns|worker_threads|cluster)['""]"), "Node system module", "c4k.readText / writeText / listDir / run / httpRequest"),
        new(Rx(@"(?<![\w.])fetch\s*\("), "fetch()", "c4k.httpGet / httpRequest"),
        new(Rx(@"\bXMLHttpRequest\b|\bWebSocket\b"), "XMLHttpRequest/WebSocket", "c4k.httpRequest"),
        new(Rx(@"\bconsole\.(log|info|debug)\s*\("), "console.log()", "c4k.log / notify (stdout is the protocol channel)"),
        new(Rx(@"\bprocess\.stdout\.write\b"), "process.stdout.write", "c4k.log (stdout is the protocol channel)"),
        new(Rx(@"\bprocess\.kill\s*\("), "process.kill()", "c4k.killProcess"),
    ];

    private static readonly Rule[] PowerShell =
    [
        new(Rx(@"\b(Get-Content|Set-Content|Add-Content|Out-File|Get-ChildItem|Get-Item|Remove-Item|Copy-Item|Move-Item|Rename-Item|New-Item|Test-Path|Import-Csv|Export-Csv)\b"), "file cmdlet", "Get-C4KText / Set-C4KText / Get-C4KChildItem / Get-C4KItem / Remove-C4KItem / Move-C4KItem / New-C4KDirectory"),
        new(Rx(@"\b(Start-Process|Get-Process|Stop-Process|Invoke-Expression|Invoke-Command)\b"), "process cmdlet", "Invoke-C4KProcess / Get-C4KProcess / Stop-C4KProcess"),
        new(Rx(@"\b(Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer)\b"), "web cmdlet", "Invoke-C4KHttpGet / Invoke-C4KHttpRequest"),
        new(Rx(@"\b(Get-CimInstance|Get-WmiObject|Get-ItemProperty|Set-ItemProperty)\b"), "system cmdlet", "Get-C4KSystemInfo / Invoke-C4KProcess"),
        new(Rx(@"\[(System\.)?(IO\.(File|Directory|Path|FileInfo|DirectoryInfo|StreamReader|StreamWriter)|Net\.(WebClient|Http\.HttpClient|Sockets\.\w+)|Diagnostics\.Process)\]"), ".NET I/O class", "the Invoke-C4K* / Get-C4K* functions"),
        new(Rx(@"(^|[;|{(]\s*)&\s*['""]?[\w:\\./ -]+\.(exe|cmd|bat)\b"), "& program.exe", "Invoke-C4KProcess"),
        new(Rx(@"\bWrite-Host\b|\[Console\]::(Write|WriteLine|Out)\b"), "Write-Host / Console", "Write-C4KLog / Show-C4KNotification (stdout is the protocol channel)"),
    ];

    /// <summary>Findings in a set of plugin files (AI answers before installing).</summary>
    public static IReadOnlyList<SourceFinding> Scan(IEnumerable<GeneratedFile> files)
    {
        var findings = new List<SourceFinding>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.Path);
            if (PluginAuthoring.SdkClients.Values.Contains(name, StringComparer.OrdinalIgnoreCase) || RulesFor(name) is not { } rules)
            {
                continue;
            }

            var comment = Path.GetExtension(name).ToLowerInvariant() is ".py" or ".ps1" or ".psm1" ? "#" : "//";
            // Calls through the SDK module (c4k.read_text, host.readText after "import c4k as host") are what we want.
            var aliases = SdkAliasRegex().Matches(file.Content).Select(m => m.Groups["alias"].Success ? m.Groups["alias"].Value : "c4k").Append("c4k").Distinct().ToList();
            var sdkCall = new Regex(@"\b(" + string.Join('|', aliases.Select(Regex.Escape)) + @")\.\w+", RegexOptions.CultureInvariant);
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = sdkCall.Replace(lines[i].TrimStart(), "SDK");
                if (line.StartsWith(comment, StringComparison.Ordinal) || line.StartsWith('*'))
                {
                    continue;
                }

                foreach (var rule in rules.Where(r => r.Pattern.IsMatch(line)))
                {
                    findings.Add(new SourceFinding(file.Path.Replace('\\', '/'), i + 1, rule.Api, rule.Instead));
                }
            }
        }

        return findings;
    }

    /// <summary>Findings in an installed plugin folder (code files up to 1 MB each, at most 200 files). Blocking.</summary>
    public static IReadOnlyList<SourceFinding> ScanFolder(string folder)
    {
        var files = new List<GeneratedFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(200))
        {
            if (RulesFor(path) is not null && new FileInfo(path).Length <= PluginAuthoring.MaxFileBytes)
            {
                files.Add(new GeneratedFile(Path.GetRelativePath(folder, path), File.ReadAllText(path)));
            }
        }

        return Scan(files);
    }

    /// <summary>A follow-up request for the assistant that wrote the plugin.</summary>
    public static string FixRequest(IReadOnlyList<SourceFinding> findings) =>
        "The plugin bypasses the Commander4K SDK. All file, process, network and system work must go through the SDK client " +
        "(c4k), and nothing may be written to stdout. Fix these places and send ALL files again in the same format " +
        "(`File: <path>` + one code block each):\n" + string.Join("\n", findings.Select(f => "- " + f));

    private static Rule[]? RulesFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" => Python,
        ".js" or ".mjs" or ".cjs" or ".ts" => Node,
        ".ps1" or ".psm1" => PowerShell,
        _ => null,
    };

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "import c4k", "import c4k as host", "const host = require('./c4k')", "import * as host from './c4k.js'".
    [GeneratedRegex(@"import\s+c4k(\s+as\s+(?<alias>\w+))?|(const|let|var)\s+(?<alias>\w+)\s*=\s*require\(\s*['""]\./c4k(\.js)?['""]\s*\)|import\s+\*\s+as\s+(?<alias>\w+)\s+from\s+['""]\./c4k(\.js)?['""]")]
    private static partial Regex SdkAliasRegex();
}
