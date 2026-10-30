using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Columns;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

public sealed class ColumnTests : IAsyncDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-cols-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // Git object files are read-only.
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Jpeg_exif_gives_date_camera_and_frame_size(bool littleEndian)
    {
        var info = ImageInfoColumnPlugin.Parse(Jpeg(littleEndian, "Canon", "Canon EOS R6", "2024:05:01 12:30:45", 6000, 4000));

        Assert.NotNull(info);
        Assert.Equal((6000, 4000), (info.Width, info.Height));
        Assert.Equal(new DateTime(2024, 5, 1, 12, 30, 45), info.Taken);
        Assert.Equal("Canon EOS R6", info.Camera); // Model already contains the make.
    }

    [Fact]
    public void Png_gif_and_bmp_headers_give_dimensions()
    {
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 1920);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 1080);
        Assert.Equal((1920, 1080), Size(ImageInfoColumnPlugin.Parse(png)));

        var gif = new byte[16];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 200);
        Assert.Equal((320, 200), Size(ImageInfoColumnPlugin.Parse(gif)));

        var bmp = new byte[32];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 640);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -480); // Top-down bitmaps store a negative height.
        Assert.Equal((640, 480), Size(ImageInfoColumnPlugin.Parse(bmp)));

        Assert.Null(ImageInfoColumnPlugin.Parse("not an image"u8));
        Assert.Equal((0, 0), Size(ImageInfoColumnPlugin.Parse([0xFF, 0xD8, 0xFF, 0xE1, 0x00]))); // Truncated JPEG: no crash.
    }

    [Fact]
    public async Task Image_columns_read_through_the_host()
    {
        var path = Path.Combine(_folder, "zdjęcie.jpg");
        await File.WriteAllBytesAsync(path, Jpeg(true, "NIKON CORPORATION", "NIKON Z 6", "2023:12:24 18:00:00", 4000, 3000), Token);
        var plugin = new ImageInfoColumnPlugin();
        var entry = (await _router.GetEntryAsync(path, Token))!.Value;
        var host = HostFor(plugin.Manifest);

        Assert.True(plugin.Supports(entry));
        Assert.Equal("4000×3000", (await plugin.GetValueAsync(entry, ImageInfoColumnPlugin.SizeColumn, host, Token)).Text);
        Assert.Equal("2023-12-24 18:00", (await plugin.GetValueAsync(entry, ImageInfoColumnPlugin.TakenColumn, host, Token)).Text);
        Assert.Equal("NIKON CORPORATION NIKON Z 6", (await plugin.GetValueAsync(entry, ImageInfoColumnPlugin.CameraColumn, host, Token)).Text);
        Assert.Single(_audit.Recent(plugin.Id), e => e.Operation == "fs.read"); // Parsed once for all three columns.
    }

    [Fact]
    public async Task Hash_column_matches_sha256()
    {
        var path = Path.Combine(_folder, "dane.bin");
        var data = Encoding.UTF8.GetBytes("Commander4K");
        await File.WriteAllBytesAsync(path, data, Token);
        var plugin = new HashColumnPlugin();

        var value = await plugin.GetValueAsync((await _router.GetEntryAsync(path, Token))!.Value, HashColumnPlugin.Sha256Column, HostFor(plugin.Manifest), Token);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data))[..16], value.Text);
    }

    [Fact]
    public void Porcelain_output_handles_renames_and_spaces()
    {
        var codes = GitStatusColumnPlugin.ParsePorcelain(" M src/a b.cs\0R  new.cs\0old.cs\0?? notes/\0!! bin/\0");

        Assert.Equal(" M", codes["src/a b.cs"]);
        Assert.Equal("R ", codes["new.cs"]);
        Assert.False(codes.ContainsKey("old.cs"));
        Assert.Equal("??", codes["notes/"]);
        Assert.Equal("!!", codes["bin/"]);
    }

    [Fact]
    public async Task Git_column_reports_modified_new_ignored_and_folder_rollup()
    {
        Assert.SkipWhen(!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d => File.Exists(Path.Combine(d.Trim(), "git.exe"))), "git is not installed");
        var repo = Path.Combine(_folder, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Git(repo, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "app.cs"), "v1", Token);
        await File.WriteAllTextAsync(Path.Combine(repo, "clean.txt"), "same", Token);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n", Token);
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "app.cs"), "v2", Token);
        await File.WriteAllTextAsync(Path.Combine(repo, "nowy plik.txt"), "?", Token);
        await File.WriteAllTextAsync(Path.Combine(repo, "debug.log"), "x", Token);
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repo, "docs", "readme.md"), "doc", Token);
        await File.WriteAllTextAsync(Path.Combine(repo, "docs", "build.log"), "ignored inside a tracked folder", Token);
        Git(repo, "add", "docs/readme.md");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "docs");

        var plugin = new GitStatusColumnPlugin();
        var host = HostFor(plugin.Manifest);
        async Task<string> StatusOf(string relative) =>
            (await plugin.GetValueAsync((await _router.GetEntryAsync(Path.Combine(repo, relative), Token))!.Value, GitStatusColumnPlugin.StatusColumn, host, Token)).Text;

        Assert.Equal("zmieniony", await StatusOf(Path.Combine("src", "app.cs")));
        Assert.Equal("● zmieniony", await StatusOf("src"));
        Assert.Equal("nowy", await StatusOf("nowy plik.txt"));
        Assert.Equal("ignorowany", await StatusOf("debug.log"));
        Assert.Equal(string.Empty, await StatusOf("clean.txt"));
        Assert.Equal(string.Empty, await StatusOf("docs")); // Only an ignored file inside: the folder itself is clean.
        Assert.Contains(_audit.Recent(plugin.Id), e => e.Operation == "shell.run" && e.Detail.StartsWith("git ", StringComparison.Ordinal));

        // Outside a repository the column stays empty instead of failing.
        var outside = Path.Combine(_folder, "plain.txt");
        await File.WriteAllTextAsync(outside, "x", Token);
        Assert.Equal(string.Empty, (await plugin.GetValueAsync((await _router.GetEntryAsync(outside, Token))!.Value, GitStatusColumnPlugin.StatusColumn, host, Token)).Text);
    }

    private ScopedPluginHost HostFor(PluginManifest manifest) => new(manifest, new NullUi(), _router, _http, _audit);

    private static (int, int) Size(ImageInfo? info) => (info?.Width ?? -1, info?.Height ?? -1);

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    /// <summary>Minimal JPEG: SOI, APP1 with a TIFF (IFD0: Make, Model, Exif pointer; Exif IFD: DateTimeOriginal), SOF0, EOI.</summary>
    private static byte[] Jpeg(bool little, string make, string model, string taken, ushort width, ushort height)
    {
        var tiff = new List<byte>();
        void U16(ushort v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v); tiff.AddRange(b); }
        void U32(uint v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); tiff.AddRange(b); }

        var makeBytes = Encoding.ASCII.GetBytes(make + "\0");
        var modelBytes = Encoding.ASCII.GetBytes(model + "\0");
        var takenBytes = Encoding.ASCII.GetBytes(taken + "\0");
        const uint ifd0 = 8;
        const uint ifd0Size = 2 + (3 * 12) + 4;
        var makeOffset = ifd0 + ifd0Size;
        var modelOffset = makeOffset + (uint)makeBytes.Length;
        var exifIfd = modelOffset + (uint)modelBytes.Length;
        const uint exifSize = 2 + 12 + 4;
        var takenOffset = exifIfd + exifSize;

        tiff.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(ifd0);
        U16(3);
        U16(0x010F); U16(2); U32((uint)makeBytes.Length); U32(makeOffset);
        U16(0x0110); U16(2); U32((uint)modelBytes.Length); U32(modelOffset);
        U16(0x8769); U16(4); U32(1); U32(exifIfd);
        U32(0);
        tiff.AddRange(makeBytes);
        tiff.AddRange(modelBytes);
        U16(1);
        U16(0x9003); U16(2); U32((uint)takenBytes.Length); U32(takenOffset);
        U32(0);
        tiff.AddRange(takenBytes);

        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        jpeg.AddRange([(byte)((app1.Length + 2) >> 8), (byte)(app1.Length + 2)]);
        jpeg.AddRange(app1);
        jpeg.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        jpeg.AddRange(new byte[9]);
        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }

    private sealed class NullUi : IPluginUi
    {
        public string? ActivePath => null;

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError)
        {
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;
    }
}
