using Commander4K.Core.Plugins;
using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class PluginAuthoringTests : IDisposable
{
    private const string Manifest = """
        {
          "id": "ai.hello",
          "name": "Hello",
          "version": "1.0.0",
          "author": "AI",
          "description": "Says hello.",
          "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
          "permissions": [],
          "contributes": { "commands": [ { "id": "hi", "title": "Hi", "category": "AI" } ] }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "c4k-authoring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("main.py", true)]
    [InlineData("lib/util.py", true)]
    [InlineData("./main.py", true)]
    [InlineData("../evil.py", false)]
    [InlineData("lib/../../evil.py", false)]
    [InlineData("C:\\Windows\\evil.dll", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("\\\\server\\share\\x", false)]
    public void Only_paths_inside_the_plugin_folder_are_accepted(string path, bool safe) =>
        Assert.Equal(safe, PluginAuthoring.IsSafeRelativePath(path));

    [Fact]
    public void Prepare_validates_the_manifest_and_adds_the_sdk_client()
    {
        var draft = PluginAuthoring.Prepare(
            [new GeneratedFile("plugin.json", Manifest), new GeneratedFile("main.py", "import c4k\nc4k.serve()\n")],
            _root,
            runtime => runtime == "python" ? "# sdk client\n" : null);

        Assert.Equal("ai.hello", draft.Manifest.Id);
        Assert.Equal(Path.Combine(_root, "ai.hello"), draft.TargetFolder);
        Assert.False(draft.TargetExists);
        Assert.Contains(draft.Files, f => f.Path == "c4k.py" && f.Content == "# sdk client\n");

        PluginAuthoring.Install(draft, overwrite: false);

        Assert.True(File.Exists(Path.Combine(_root, "ai.hello", "main.py")));
        Assert.True(File.Exists(Path.Combine(_root, "ai.hello", "c4k.py")));
        var again = PluginAuthoring.Prepare(draft.Files, _root, _ => null);
        Assert.True(again.TargetExists);
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.Install(again, overwrite: false));
    }

    [Fact]
    public void Prepare_refuses_unsafe_paths_and_invalid_manifests()
    {
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.Prepare(
            [new GeneratedFile("plugin.json", Manifest), new GeneratedFile("../outside.py", "x")], _root, _ => null));
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.Prepare(
            [new GeneratedFile("plugin.json", Manifest.Replace("ai.hello", "builtin.hello", StringComparison.Ordinal)), new GeneratedFile("main.py", "x")], _root, _ => null));
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.Prepare([new GeneratedFile("main.py", "x")], _root, _ => null));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Binary_files_are_written_byte_exact()
    {
        byte[] data = [0, 255, 10];
        var draft = PluginAuthoring.Prepare(
            [new GeneratedFile("plugin.json", Manifest), new GeneratedFile("main.py", "x"), new GeneratedFile("assets/a.bin", string.Empty, data)], _root, _ => null);
        PluginAuthoring.Install(draft, overwrite: false);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(draft.TargetFolder, "assets", "a.bin")));
    }

    [Fact]
    public void Folder_limits_allow_big_assets()
    {
        var big = new GeneratedFile("big.mjs", new string('a', 2 * 1024 * 1024));
        GeneratedFile[] files = [new("plugin.json", Manifest), new("main.py", "x"), big];
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.Prepare(files, _root, _ => null));
        Assert.Equal("ai.hello", PluginAuthoring.Prepare(files, _root, _ => null, PluginLimits.Folder).Manifest.Id);
    }

    [Fact]
    public void FromFolder_reads_text_and_binary_and_skips_git()
    {
        var dir = Path.Combine(_root, "src-plugin");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", ".cache"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), Manifest);
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref");
        File.WriteAllText(Path.Combine(dir, "node_modules", ".cache", "x.js"), "x");
        File.WriteAllBytes(Path.Combine(dir, "sub", "img.png"), [1, 0, 2]);
        File.WriteAllBytes(Path.Combine(dir, "sub", "weird.txt"), [65, 0, 66]);
        var files = PluginAuthoring.FromFolder(dir, PluginLimits.Folder, TestContext.Current.CancellationToken);
        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.Path == "plugin.json" && f.Content == Manifest && f.Bytes is null);
        Assert.Contains(files, f => f.Path.Replace(Path.DirectorySeparatorChar, '/') == "sub/img.png" && f.Bytes!.SequenceEqual(new byte[] { 1, 0, 2 }));
        Assert.Contains(files, f => f.Path.EndsWith("weird.txt", StringComparison.Ordinal) && f.Bytes is not null);
        Assert.Throws<InvalidOperationException>(() => PluginAuthoring.FromFolder(dir, new PluginLimits(2, 1024, 1024), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Source_check_ignores_binary_files()
    {
        Assert.Empty(PluginSourceCheck.Scan([new GeneratedFile("x.py", string.Empty, [1, 2, 3])]));
    }

    [Fact]
    public void FromFolder_keeps_odd_encoded_code_as_text_so_the_lint_sees_it()
    {
        var dir = Path.Combine(_root, "enc-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.ps1"), [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("Get-Content x\n")]);
        File.WriteAllBytes(Path.Combine(dir, "b.py"), [.. System.Text.Encoding.Latin1.GetBytes("# café\nx = open('a')\n")]);
        var files = PluginAuthoring.FromFolder(dir, PluginLimits.Folder, TestContext.Current.CancellationToken);
        Assert.All(files, f => Assert.Null(f.Bytes));
        var findings = PluginSourceCheck.Scan(files);
        Assert.Contains(findings, f => f.File == "a.ps1");
        Assert.Contains(findings, f => f.File == "b.py");
    }
}
