using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Commander4K.App.Browser;
using Commander4K.App.Theming;
using Microsoft.Web.WebView2.Core;

namespace Commander4K.App.Preview;

public sealed record MermaidImage(Bitmap Bitmap, double Width, double Height);

public sealed class MermaidRenderException(string message) : Exception(message);

/// <summary>
/// Renders Mermaid diagrams to images, offline. A hidden WebView2 (the browser's shared environment, started on the
/// first diagram) loads <c>render.html</c> with the bundled Mermaid 12.2.0 from a virtual host; each diagram comes
/// back as a PNG in the active skin's colours. <c>securityLevel: strict</c> and a navigation lock keep diagrams from
/// running scripts or loading anything. All members are called on the UI thread (WebView2 is single-threaded).
/// </summary>
public static class MermaidRenderer
{
    public const string MermaidVersion = "12.2.0";
    private const string VirtualHost = "c4k-mermaid.local";
    private const int CacheSize = 64;

    private static readonly Dictionary<string, TaskCompletionSource<JsonElement>> Pending = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MermaidImage> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static Func<nint>? _windowHandle;
    private static string? _dataDirectory;
    private static Task<CoreWebView2Controller>? _starting;
    private static TaskCompletionSource? _ready;

    /// <summary>Diagrams render only once the main window has handed over its HWND and WebView2 is installed.</summary>
    public static bool IsAvailable => _windowHandle is not null && WebView2Host.RuntimeVersion() is not null;

    /// <param name="windowHandle">The main window's HWND (the hidden controller's parent).</param>
    /// <param name="dataDirectory">The app's data folder (WebView2 profile and the extracted renderer page).</param>
    public static void Configure(Func<nint> windowHandle, string dataDirectory)
    {
        _windowHandle = windowHandle;
        _dataDirectory = dataDirectory;
    }

    public static async Task<MermaidImage> RenderAsync(string code, SkinBrushes brushes)
    {
        var theme = ThemeJson(brushes);
        var key = theme + "\n" + code;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await OneAtATime.WaitAsync();
        try
        {
            var controller = await StartAsync();
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[id] = completion;
            try
            {
                await controller.CoreWebView2.ExecuteScriptAsync(
                    $"window.c4kRender({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(code)}, {theme})");
                var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (result.TryGetProperty("error", out var error))
                {
                    throw new MermaidRenderException(error.GetString() ?? "Mermaid error");
                }

                var png = Convert.FromBase64String(result.GetProperty("png").GetString()!);
                var image = new MermaidImage(new Bitmap(new MemoryStream(png)), result.GetProperty("width").GetDouble(), result.GetProperty("height").GetDouble());
                if (Cache.Count >= CacheSize)
                {
                    Cache.Clear();
                }

                Cache[key] = image;
                return image;
            }
            finally
            {
                Pending.Remove(id);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static Task<CoreWebView2Controller> StartAsync()
    {
        if (_starting is { IsFaulted: true } or { IsCanceled: true })
        {
            _starting = null; // Try again on the next diagram (e.g. WebView2 installed meanwhile).
        }

        return _starting ??= CreateAsync();
    }

    private static async Task<CoreWebView2Controller> CreateAsync()
    {
        if (_windowHandle is null || _dataDirectory is null)
        {
            throw new InvalidOperationException("The Mermaid renderer is not configured.");
        }

        // WebView2 rejects a null parent with ArgumentException; without a window yet (or headless) the code is shown instead.
        if (_windowHandle() is var parent && parent == 0)
        {
            throw new InvalidOperationException("The window has no native handle yet.");
        }

        var folder = await Task.Run(() => ExtractAssets(_dataDirectory));
        var environment = await WebView2Host.GetEnvironmentAsync(Path.Combine(_dataDirectory, "data", "webview2"));
        var controller = await environment.CreateCoreWebView2ControllerAsync(parent);
        controller.IsVisible = false;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1200, 900);
        var core = controller.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true; // Only the renderer page; diagrams cannot navigate anywhere.
            }
        };
        core.WebMessageReceived += OnMessage;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Navigate($"https://{VirtualHost}/render.html");
        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch
        {
            controller.Close();
            throw;
        }

        return controller;
    }

    private static void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var id = document.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
        if (id == "ready")
        {
            _ready?.TrySetResult();
        }
        else if (id is not null && Pending.TryGetValue(id, out var completion))
        {
            completion.TrySetResult(document.RootElement.Clone());
        }
    }

    /// <summary>Copies render.html and mermaid.min.js out of the app into data/mermaid/&lt;version&gt; (once per version).</summary>
    private static string ExtractAssets(string dataDirectory) =>
        Browser.WebView2Host.ExtractAssets(Path.Combine(dataDirectory, "data", "mermaid", MermaidVersion), "mermaid/", "render.html", "mermaid.min.js");

    /// <summary>Mermaid's "base" theme driven by the skin: background, boxes, text, lines and the accent for borders.</summary>
    private static string ThemeJson(SkinBrushes b)
    {
        static string Hex(IBrush brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.R:x2}{solid.Color.G:x2}{solid.Color.B:x2}" : "#808080";
        var background = ((ISolidColorBrush)b.PanelBackground).Color;
        var dark = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255 < 0.5;
        var theme = new
        {
            scale = 2,
            font = b.FontFamily.Name,
            variables = new Dictionary<string, object>
            {
                ["darkMode"] = dark,
                ["background"] = Hex(b.PanelBackground),
                ["primaryColor"] = Hex(b.Surface2),
                ["primaryTextColor"] = Hex(b.FileForeground),
                ["primaryBorderColor"] = Hex(b.Accent),
                ["secondaryColor"] = Hex(b.HeaderBackground),
                ["tertiaryColor"] = Hex(b.WindowBackground),
                ["lineColor"] = Hex(b.MutedForeground),
                ["textColor"] = Hex(b.FileForeground),
                ["noteBkgColor"] = Hex(b.HighlightBackground),
                ["noteTextColor"] = Hex(b.FileForeground),
                ["edgeLabelBackground"] = Hex(b.PanelBackground),
                ["fontFamily"] = b.FontFamily.Name,
            },
        };
        return JsonSerializer.Serialize(theme);
    }
}
