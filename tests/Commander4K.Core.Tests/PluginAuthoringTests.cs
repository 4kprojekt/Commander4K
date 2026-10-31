using Commander4K.Core.Ai;
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

    [Fact]
    public void Files_are_found_after_file_lines_in_several_styles()
    {
        var answer = $"""
            Here is the plugin.

            File: plugin.json
            ```json
            {Manifest}
            ```

            **File:** `main.py`
            ```python
            import c4k
            c4k.serve()
            ```

            ### Plik: data/words.txt

            ```
            hello
            ```

            Some explanation that mentions File: nothing.
            ```python
            print("not a file of the plugin")
            ```
            """;

        var files = PluginAuthoring.ParseFiles(answer);

        Assert.Equal(["plugin.json", "main.py", "data/words.txt"], files.Select(f => f.Path));
        Assert.Contains("c4k.serve()", files[1].Content, StringComparison.Ordinal);
        Assert.Equal("hello\n", files[2].Content);
    }

    [Fact]
    public void Fence_info_paths_and_repeated_files_keep_the_last_version()
    {
        var answer = """
            ```python title="main.py"
            print(1)
            ```
            Corrected:
            ```python file=main.py
            print(2)
            ```
            """;

        var file = Assert.Single(PluginAuthoring.ParseFiles(answer));

        Assert.Equal("main.py", file.Path);
        Assert.Equal("print(2)\n", file.Content);
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
    public void Prompt_carries_guide_context_format_and_wish()
    {
        var prompt = PluginAuthoring.BuildPrompt("GUIDE", "CONTEXT", "EXAMPLE", "list big files");

        Assert.Contains("<plugin-guide>\nGUIDE", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("CONTEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("File: <relative path>", prompt, StringComparison.Ordinal);
        Assert.EndsWith("list big files\n</user-wish>", prompt.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_conversation_is_flattened_for_models_without_tools()
    {
        var call = new AiToolCall("c1", "get_context", "{}");
        var flat = AiMessage.WithoutTools([
            AiMessage.User("where am I?"),
            AiMessage.Assistant(string.Empty) with { ToolCalls = [call] },
            AiMessage.Tool(call, "Path: C:\\Repos"),
        ]);

        Assert.All(flat, m => Assert.Empty(m.ToolCalls));
        Assert.Equal(["user", "assistant", "user"], flat.Select(m => m.Role));
        Assert.Contains("[result of get_context]", flat[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Assistants_have_a_chat_page_for_long_prompts()
    {
        Assert.Equal("https://gemini.google.com/app", SearchEngines.Get("gemini").ChatUrl);
        Assert.Equal("https://chatgpt.com/", SearchEngines.Get("chatgpt").ChatUrl);
        Assert.Equal("https://claude.ai/", SearchEngines.Get("claude").ChatUrl);
    }
}
