using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class PluginSourceCheckTests
{
    [Fact]
    public void Python_direct_io_is_found_but_sdk_calls_are_not()
    {
        var findings = PluginSourceCheck.Scan([new GeneratedFile("main.py", """
            import c4k
            import c4k as host
            import subprocess
            # open("comment.txt") is ignored
            text = c4k.read_text(path)
            data = host.read_bytes(path, 0, 512)
            with open(path) as f:
                pass
            pathlib.Path(path).write_text("x")
            print("hello")
            c4k.open_url("https://example.com")
            """)]);

        Assert.Equal([3, 7, 9, 10], findings.Select(f => f.Line));
        Assert.Contains(findings, f => f.Api == "open()" && f.Instead.Contains("read_text", StringComparison.Ordinal));
    }

    [Fact]
    public void Node_and_powershell_bypasses_are_found()
    {
        var findings = PluginSourceCheck.Scan([
            new GeneratedFile("main.js", """
                const c4k = require('./c4k');
                const fs = require('node:fs');
                const text = await c4k.readText(p);
                const r = await fetch(url);
                console.log('x');
                """),
            new GeneratedFile("main.ps1", """
                . "$PSScriptRoot/c4k.ps1"
                $t = Get-C4KText $path
                $u = Get-Content $path
                & git.exe status
                Write-Host 'x'
                """),
            new GeneratedFile("c4k.js", "const fs = require('fs');"),
            new GeneratedFile("plugin.json", "{ \"open(\": 1 }"),
        ]);

        Assert.Equal(["main.js:2", "main.js:4", "main.js:5", "main.ps1:3", "main.ps1:4", "main.ps1:5"], findings.Select(f => $"{f.File}:{f.Line}"));
    }

    [Theory]
    [InlineData("folder-stats-python")]
    [InlineData("site-check-node")]
    [InlineData("git-powershell")]
    [InlineData("project-modules-python")]
    [InlineData("upload-client-python")]
    public void Bundled_examples_use_only_the_sdk(string example)
    {
        var findings = PluginSourceCheck.ScanFolder(Path.Combine(RepositoryRoot(), "examples", "plugins", example));

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    [Fact]
    public void Fix_request_lists_every_place()
    {
        var request = PluginSourceCheck.FixRequest([new SourceFinding("main.py", 7, "open()", "c4k.read_text")]);

        Assert.Contains("main.py:7 — open() → c4k.read_text", request, StringComparison.Ordinal);
        Assert.Contains("File: <path>", request, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }
}
