using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Execution;
using Commander4K.Core.Mcp;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class McpFileToolsTests
{
    private readonly MemFs _fs = new();
    private readonly IReadOnlyList<McpTool> _tools;

    public McpFileToolsTests() => _tools = McpFileTools.Create(new VfsRouter(new ResilientAsyncExecutor(), [_fs]));

    private McpTool Tool(string name) => _tools.Single(t => t.Definition.Name == name);

    private Task<McpToolResult> Call(string name, params (string Key, JsonNode? Value)[] args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args)
        {
            json[key] = value;
        }

        return Tool(name).Handler(new McpCall("test", json), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task List_directory_lists_names_kinds_sizes()
    {
        _fs.Dirs.Add("mfs://d/sub");
        _fs.Files["mfs://d/a.txt"] = new byte[1234];
        var result = await Call("list_directory", ("path", "mfs://d"));
        Assert.False(result.IsError);
        Assert.Contains("a.txt", result.Text);
        Assert.Contains("1234 B", result.Text);
        Assert.Contains("sub/", result.Text);
        Assert.Contains("Directory", result.Text);
    }

    [Fact]
    public async Task Read_text_truncates_at_60000_chars()
    {
        _fs.Files["mfs://d/big.txt"] = Encoding.UTF8.GetBytes(new string('x', 70_000));
        var result = await Call("read_file", ("path", "mfs://d/big.txt"));
        Assert.EndsWith("… (file truncated)", result.Text);
        Assert.Equal(60_000, result.Text.IndexOf('\n'));
    }

    [Fact]
    public async Task Read_binary_suggests_base64()
    {
        _fs.Files["mfs://d/b.bin"] = [1, 2, 0, 3];
        var result = await Call("read_file", ("path", "mfs://d/b.bin"));
        Assert.True(result.IsError);
        Assert.Contains("base64", result.Text);
    }

    [Fact]
    public async Task Read_base64_pages_with_offset()
    {
        var data = new byte[600 * 1024];
        new Random(1).NextBytes(data);
        _fs.Files["mfs://d/b.bin"] = data;
        var decoded = new List<byte>();
        long offset = 0;
        var texts = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var result = await Call("read_file", ("path", "mfs://d/b.bin"), ("encoding", "base64"), ("offset", offset));
            texts.Add(result.Text);
            var parts = result.Text.Split("\nnext offset: ");
            decoded.AddRange(Convert.FromBase64String(parts[0]));
            if (parts.Length == 1)
            {
                break;
            }

            offset = long.Parse(parts[1]);
        }

        Assert.Equal(3, texts.Count);
        Assert.Contains("next offset: 262144", texts[0]);
        Assert.Contains("next offset: 524288", texts[1]);
        Assert.DoesNotContain("next", texts[2]);
        Assert.Equal(data, decoded.ToArray());
    }

    [Fact]
    public async Task Read_of_huge_file_reads_only_what_it_returns()
    {
        _fs.Files["mfs://d/huge.bin"] = new byte[50_000_000];
        _fs.Counter = new long[1];
        await Call("read_file", ("path", "mfs://d/huge.bin"), ("encoding", "base64"));
        Assert.InRange(_fs.Counter[0], 1, McpFileTools.MaxBase64Bytes + 1);

        _fs.Counter[0] = 0;
        await Call("read_file", ("path", "mfs://d/huge.bin"));
        Assert.InRange(_fs.Counter[0], 1, McpFileTools.MaxTextChars + 16_384);
    }

    [Fact]
    public async Task Write_then_stat_roundtrip()
    {
        var write = await Call("write_file", ("path", "mfs://d/n.txt"), ("content", "hello world"));
        Assert.False(write.IsError);
        var stat = await Call("stat", ("path", "mfs://d/n.txt"));
        Assert.Contains("n.txt", stat.Text);
        Assert.Contains("11 B", stat.Text);
        Assert.Equal("hello world", (await Call("read_file", ("path", "mfs://d/n.txt"))).Text);
        Assert.True((await Call("stat", ("path", "mfs://d/missing"))).IsError);
    }

    [Fact]
    public async Task Move_and_delete_work_on_temp_folder()
    {
        _fs.Files["mfs://d/a.txt"] = [1];
        _fs.Files["mfs://d/b.txt"] = [2];
        Assert.False((await Call("make_directory", ("path", "mfs://d/new"))).IsError);
        Assert.False((await Call("move", ("source", "mfs://d/a.txt"), ("destination", "mfs://d/new/a.txt"))).IsError);
        Assert.True(_fs.Files.ContainsKey("mfs://d/new/a.txt"));
        var clash = await Call("move", ("source", "mfs://d/b.txt"), ("destination", "mfs://d/new/a.txt"));
        Assert.True(clash.IsError);
        Assert.False((await Call("delete", ("path", "mfs://d/new"))).IsError);
        Assert.False(_fs.Files.ContainsKey("mfs://d/new/a.txt"));
    }

    [Fact]
    public void Summaries_name_the_paths()
    {
        var move = Tool("move").Summary!(new JsonObject { ["source"] = "mfs://d/a", ["destination"] = "mfs://d/b" });
        Assert.Contains("mfs://d/a", move);
        Assert.Contains("mfs://d/b", move);
        var write = Tool("write_file").Summary!(new JsonObject { ["path"] = "mfs://d/w", ["content"] = "hello" });
        Assert.Contains("mfs://d/w", write);
        Assert.Contains("5 B", write);
        Assert.True(Tool("move").Definition.AlwaysAsk);
        Assert.True(Tool("delete").Definition.AlwaysAsk);
    }

    private sealed class MemFs : IVfsProviderPlugin
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public HashSet<string> Dirs { get; } = [];

        public long[]? Counter { get; set; }

        public string Id => "test.memfs";

        public string DisplayName => "MemFs";

        public PluginManifest Manifest { get; } = new("test.memfs", "MemFs", "1.0.0", "tests", "In-memory", [PluginPermissions.FileRead]);

        public IReadOnlyList<string> Schemes { get; } = ["mfs"];

        public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.Delete | VfsCapabilities.Move | VfsCapabilities.CreateDirectory;

        public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var prefix = path.TrimEnd('/') + "/";
            foreach (var dir in Dirs.Where(d => IsChild(d, prefix)).ToList())
            {
                yield return new VfsEntry(dir[prefix.Length..], dir, VfsEntryKind.Directory);
            }

            foreach (var file in Files.Where(f => IsChild(f.Key, prefix)).ToList())
            {
                yield return new VfsEntry(file.Key[prefix.Length..], file.Key, VfsEntryKind.File, file.Value.Length);
            }
        }

        public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            VfsEntry? entry = Files.TryGetValue(path, out var data)
                ? new VfsEntry(name, path, VfsEntryKind.File, data.Length)
                : Dirs.Contains(path) ? new VfsEntry(name, path, VfsEntryKind.Directory) : null;
            return ValueTask.FromResult(entry);
        }

        public string? GetParentPath(string path) => path[..path.LastIndexOf('/')];

        public string Combine(string directoryPath, string name) => directoryPath.TrimEnd('/') + "/" + name;

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            if (!Files.TryGetValue(path, out var data))
            {
                throw new FileNotFoundException(path);
            }

            return ValueTask.FromResult<Stream>(new CountingStream(new MemoryStream(data), Counter));
        }

        public ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken)
        {
            if (!overwrite && Files.ContainsKey(path))
            {
                throw new IOException("exists");
            }

            return ValueTask.FromResult<Stream>(new SinkStream(bytes => Files[path] = bytes));
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Dirs.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            var prefix = path + "/";
            Dirs.RemoveWhere(d => d == path || d.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var key in Files.Keys.Where(k => k == path || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                Files.Remove(key);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            if (!overwrite && Files.ContainsKey(destinationPath))
            {
                throw new IOException("destination exists");
            }

            Files[destinationPath] = Files[sourcePath];
            Files.Remove(sourcePath);
            return ValueTask.CompletedTask;
        }

        private static bool IsChild(string path, string prefix) =>
            path.StartsWith(prefix, StringComparison.Ordinal) && !path.AsSpan(prefix.Length).Contains('/');
    }

    private sealed class SinkStream(Action<byte[]> commit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(Stream inner, long[]? counter) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            if (counter is not null)
            {
                counter[0] += read;
            }

            return read;
        }
    }
}
