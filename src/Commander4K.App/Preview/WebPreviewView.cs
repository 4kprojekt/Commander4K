using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Commander4K.App.Browser;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.Core.Web;
using Commander4K.PluginSdk;
using Microsoft.Web.WebView2.Core;
using CoreModifiers = Commander4K.Core.Input.KeyModifiers;

namespace Commander4K.App.Preview;

/// <summary>
/// A preview plugin's HTML page (<see cref="WebPreview"/>) in WebView2, sandboxed:
/// <list type="bullet">
/// <item>its own profile (<c>data/webview2-previews</c>, no cookies from the browser panel) whose network goes to a dead proxy, so nothing the page starts can leave the machine;</item>
/// <item>every request is answered here: <c>https://&lt;plugin&gt;.plugin.c4k/</c> (<see cref="WebPreviewOrigin"/>, one origin per exact plugin id)
/// serves files under the preview's <c>webRoot</c> (with a CSP that only allows that origin), <c>/__entry/&lt;token&gt;/…</c> the previewed
/// file through the plugin's own <see cref="IPluginHost"/> (fs.read, audited, byte ranges); service worker scripts and anything else are 403;</item>
/// <item>navigation away is cancelled, new windows and downloads too, permission prompts are denied; only an http(s) link the user
/// clicks opens in the browser panel (a script's own <c>location.href</c> or <c>window.open</c> opens nothing).</item>
/// </list>
/// The page talks to the app only through <c>window.c4k</c> (<c>WebBridge.js</c>). Moving to another file the same page
/// shows posts an <c>entry</c> event instead of reloading. All members run on the UI thread.
/// </summary>
public sealed class WebPreviewView : UserControl, IDisposable
{
    private const string EntryPrefix = "/__entry/";
    private const long MaxBufferedBytes = 4 * 1024 * 1024;
    private const int MaxNotifyChars = 500;
    private const int MaxCopyChars = 1024 * 1024;
    private const int MaxMessagesPerSecond = 5;
    private const int MaxRequestsInFlight = 16;
    private static readonly string LineSeparator = ((char)0x2028).ToString();
    private static readonly string ParagraphSeparator = ((char)0x2029).ToString();

    /// <summary>
    /// No proxy answers on port 9, so WebSockets, WebRTC relays and any other request the resource filter does not see
    /// fail; requests the filter answers never reach the network stack.
    /// </summary>
    private const string SandboxArguments = "--proxy-server=http://127.0.0.1:9 --proxy-bypass-list=<-loopback> --force-webrtc-ip-handling-policy=disable_non_proxied_udp";

    private const string PageSecurityPolicy =
        "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline' 'unsafe-eval' 'wasm-unsafe-eval' blob:; style-src 'self' 'unsafe-inline' data: blob:; " +
        "connect-src 'self' data: blob:; frame-src 'self' blob:; worker-src 'self' blob:; object-src 'none'; form-action 'none'; base-uri 'self'";

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkTab = 0x09, VkEscape = 0x1B, VkF1 = 0x70, VkF12 = 0x7B;

    private static readonly Lazy<string> BridgeScript = new(() =>
    {
        using var stream = typeof(WebPreviewView).Assembly.GetManifestResourceStream("preview/web-bridge.js")
            ?? throw new InvalidOperationException("Embedded resource preview/web-bridge.js is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().TrimEnd().TrimEnd(';');
    });

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    private static bool _staleSpoolsCleaned;
    private static bool _supported;

    private readonly Func<KeyStroke, bool> _runShortcut;
    private readonly WebView2Host _web;
    private readonly TextBlock _fallback;
    private readonly StreamTracker _pageStreams = new();
    private SkinBrushes _brushes;
    private CoreWebView2? _core;
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _environment;
    private EntryState? _current;
    private CancellationTokenSource? _showing;
    private string? _scriptId;
    private string? _hostName;
    private IPluginHost? _pageHost;
    private string? _root;
    private long _lastExternalOpen;
    private long _lastAcceleratorEscape = long.MinValue / 2;
    private string? _pageUrl;
    private bool _disposed;

    public WebPreviewView(AppHost app, SkinBrushes brushes, Func<KeyStroke, bool> runShortcut)
    {
        _brushes = brushes;
        _runShortcut = runShortcut;
        _fallback = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _web = new WebView2Host(Path.Combine(app.DataDirectory, "data", "webview2-previews"), SandboxArguments);
        _web.Ready += OnReady;
        _web.Failed += OnFailed;
        Content = new Grid { Children = { _web, _fallback } };
        CleanStaleSpoolsOnce();
        ApplySkin(brushes);
    }

    /// <summary>WebView2 is installed and this is an STA thread (headless harnesses run on MTA and get the message instead).</summary>
    public static bool IsSupported
    {
        get
        {
            // Only a positive answer is cached: the runtime may be installed while the app runs.
            if (!_supported)
            {
                _supported = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && WebView2Host.RuntimeVersion() is not null;
            }

            return _supported;
        }
    }

    public static string UnavailableMessage => Loc.T("Ten podgląd potrzebuje WebView2 (Microsoft Edge WebView2 Runtime).");

    /// <summary>
    /// Shows <paramref name="preview"/> for <paramref name="entry"/>: the file is opened through <paramref name="host"/>
    /// (the preview plugin's scoped host) off the UI thread; the same page of the same plugin gets an <c>entry</c> event,
    /// anything else is navigated to.
    /// </summary>
    public async Task ShowAsync(WebPreview preview, VfsEntry entry, ExternalPreviewPlugin plugin, IPluginHost host)
    {
        if (_disposed)
        {
            return;
        }

        _showing?.Cancel();
        var showing = new CancellationTokenSource();
        _showing = showing;
        var token = showing.Token;
        var state = await Task.Run(() => OpenEntryAsync(preview, entry, plugin, host, token));
        var stale = _showing != showing || _disposed;
        if (_showing == showing)
        {
            _showing = null;
        }

        showing.Dispose(); // Only OpenEntryAsync used its token; _showing never holds a disposed source.
        if (stale)
        {
            state.Close();
            return;
        }

        var previous = _current;
        _current = state;
        previous?.Close();
        if (_core is not null)
        {
            await ApplyAsync(state);
        }
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        var changed = !ReferenceEquals(_brushes, brushes);
        _brushes = brushes;
        Background = brushes.PanelBackground;
        _fallback.Foreground = brushes.HiddenForeground;
        if (_web.Controller is { } controller)
        {
            controller.DefaultBackgroundColor = DrawingColor(brushes.PanelBackground);
        }

        if (changed && _core is { } core && _current is { } state)
        {
            _ = RefreshSkinAsync(core, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _showing?.Cancel(); // ShowAsync disposes it when its open finishes.
        _showing = null;
        _current?.Close();
        _current = null;
        _pageStreams.CloseAll();
        _web.Ready -= OnReady;
        _web.Failed -= OnFailed;
        Unsubscribe();
        _core = null;
        _controller = null;
        Content = null; // Leaving the visual tree closes the WebView2 controller.
    }

    private static async Task<EntryState> OpenEntryAsync(WebPreview preview, VfsEntry entry, ExternalPreviewPlugin plugin, IPluginHost host, CancellationToken cancellationToken)
    {
        try
        {
            var source = await EntryFileSource.OpenAsync(ct => host.OpenReadAsync(entry.Path, ct), entry.Size >= 0 ? entry.Size : null, Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
            return new EntryState(preview, entry, plugin, host, source, source.Token, 0);
        }
        catch (Exception ex)
        {
            // The page still loads; its file requests get the status (fs.read denied = 403, the host audited it).
            var status = ex switch
            {
                PluginPermissionException => 403,
                FileNotFoundException or DirectoryNotFoundException or VfsProviderNotFoundException => 404,
                _ => 500,
            };
            return new EntryState(preview, entry, plugin, host, null, Guid.NewGuid().ToString("N"), status);
        }
    }

    private static void CleanStaleSpoolsOnce()
    {
        if (_staleSpoolsCleaned)
        {
            return;
        }

        _staleSpoolsCleaned = true;
        var folder = Path.GetTempPath();
        _ = Task.Run(() =>
        {
            try
            {
                var limit = DateTime.UtcNow.AddDays(-1);
                foreach (var file in Directory.EnumerateFiles(folder, "c4k-web-*.bin"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < limit)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // In use by another instance, or not ours to delete.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temp folder cannot be listed; the OS cleans it eventually.
            }
        });
    }

    private void OnReady(CoreWebView2Controller controller)
    {
        if (_disposed)
        {
            return;
        }

        var core = controller.CoreWebView2;
        _core = core;
        _environment = core.Environment;
        controller.DefaultBackgroundColor = DrawingColor(_brushes.PanelBackground);
        var settings = core.Settings;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsZoomControlEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All); // Workers too.
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.WebMessageReceived += OnWebMessage;
        controller.AcceleratorKeyPressed += OnAcceleratorKey;
        _controller = controller;

        if (_current is { } state)
        {
            _ = ApplyAsync(state);
        }
    }

    /// <summary>
    /// Drops the handlers that call back into this view. <see cref="OnResourceRequested"/> stays until the controller
    /// closes, so a request the page still makes gets its 403 instead of going to the network stack.
    /// </summary>
    private void Unsubscribe()
    {
        try
        {
            if (_core is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.FrameNavigationStarting -= OnFrameNavigationStarting;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.PermissionRequested -= OnPermissionRequested;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                core.WebMessageReceived -= OnWebMessage;
            }

            if (_controller is { } controller)
            {
                controller.AcceleratorKeyPressed -= OnAcceleratorKey;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The controller already closed and took its handlers with it.
        }
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) => e.State = CoreWebView2PermissionState.Deny;

    private static void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) => e.Cancel = true;

    private void OnFailed(Exception error)
    {
        _fallback.Text = UnavailableMessage + "\n\n" + error.Message;
        _fallback.IsVisible = true;
        _web.IsVisible = false; // The native window would cover the message otherwise.
    }

    /// <summary>Registers the bridge with the entry's data, then either tells the open page about the entry or navigates.</summary>
    private async Task ApplyAsync(EntryState state)
    {
        if (_core is not { } core)
        {
            return;
        }

        var hostName = WebPreviewOrigin.HostName(state.Preview.PluginId);
        if (!await RegisterBridgeAsync(core, state, hostName) || _current != state)
        {
            return;
        }

        var pageUrl = $"https://{hostName}/{string.Join('/', state.Preview.Page.Split('/').Select(Uri.EscapeDataString))}";
        try
        {
            // The page's own #hash or replaceState changes (PDF.js: #page=3) keep it the same page; only the path counts.
            var samePage = hostName == _hostName
                && string.Equals(_root, state.Preview.RootFolder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_pageUrl, pageUrl, StringComparison.Ordinal)
                && Uri.TryCreate(core.Source, UriKind.Absolute, out var shown)
                && string.Equals(shown.GetLeftPart(UriPartial.Path), pageUrl, StringComparison.OrdinalIgnoreCase);
            if (samePage)
            {
                Post(core, writer =>
                {
                    writer.WriteString("type", "entry");
                    WriteEntryData(writer, state, hostName);
                });
                return;
            }

            _hostName = hostName;
            _pageHost = state.Host;
            _root = state.Preview.RootFolder;
            _pageUrl = pageUrl;
            core.Navigate(pageUrl);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The controller closed (the view left the window) meanwhile.
        }
    }

    /// <summary>Replaces the injected bridge so documents created from now on start with this entry and skin.</summary>
    private async Task<bool> RegisterBridgeAsync(CoreWebView2 core, EntryState state, string hostName)
    {
        var script = BridgeScript.Value + "(" + Json(writer =>
        {
            writer.WriteStartObject();
            WriteEntryData(writer, state, hostName);
            writer.WritePropertyName("skin");
            WriteSkin(writer, _brushes);
            writer.WriteEndObject();
        }) + ");";

        string id;
        try
        {
            id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            return false; // The controller closed meanwhile.
        }

        if (_core != core || _current != state)
        {
            TryRemoveScript(core, id);
            return false;
        }

        if (_scriptId is { } previous)
        {
            TryRemoveScript(core, previous);
        }

        _scriptId = id;
        return true;
    }

    private async Task RefreshSkinAsync(CoreWebView2 core, EntryState state)
    {
        if (await RegisterBridgeAsync(core, state, WebPreviewOrigin.HostName(state.Preview.PluginId)))
        {
            Post(core, writer =>
            {
                writer.WriteString("type", "skin");
                writer.WritePropertyName("skin");
                WriteSkin(writer, _brushes);
            });
        }
    }

    private static void TryRemoveScript(CoreWebView2 core, string id)
    {
        try
        {
            core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // Closed: nothing left to remove from.
        }
    }

    // ---- requests ----

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Set in OnReady, before any request can arrive, and kept after Dispose: a late request still gets its 403 below.
        if (_environment is not { } environment)
        {
            return;
        }

        var request = e.Request;
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri))
        {
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            return;
        }

        if (uri.Scheme is "data" or "blob")
        {
            return; // In-page data; never reaches the network.
        }

        if (_disposed || !IsOwn(uri) || _current is not { } state || _root is not { } root)
        {
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            return;
        }

        // A service worker would outlive the page and answer its later requests itself; previews get none.
        if (request.Headers.Contains("Service-Worker"))
        {
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
            return;
        }

        var method = request.Method.ToUpperInvariant();
        if (method is not ("GET" or "HEAD"))
        {
            e.Response = environment.CreateWebResourceResponse(null, 405, "Method Not Allowed", "Allow: GET, HEAD");
            return;
        }

        var head = method == "HEAD";
        var path = uri.AbsolutePath;
        Func<Task<Reply>> produce;
        if (path.StartsWith(EntryPrefix, StringComparison.Ordinal))
        {
            var rest = path[EntryPrefix.Length..];
            var slash = rest.IndexOf('/');
            var token = slash < 0 ? rest : rest[..slash];
            if (!string.Equals(token, state.Token, StringComparison.Ordinal))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
                return;
            }

            var range = request.Headers.Contains("Range") ? request.Headers.GetHeader("Range") : null;
            produce = () => ServeEntryAsync(state, range, head);
        }
        else
        {
            produce = () => Task.FromResult(ServePageFile(root, path, head, _pageStreams));
        }

        var deferral = e.GetDeferral();
        _ = RespondAsync(environment, e, deferral, produce);
    }

    /// <summary>Builds the response off the UI thread, then hands it over on the UI thread; the deferral completes on every path.</summary>
    private static async Task RespondAsync(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Deferral deferral, Func<Task<Reply>> produce)
    {
        var reply = await Task.Run(async () =>
        {
            try
            {
                return await produce().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Reply.Status(500, "Internal Server Error");
            }
        });

        try
        {
            e.Response = environment.CreateWebResourceResponse(reply.Body, reply.StatusCode, reply.Reason, reply.Headers);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            reply.Body?.Dispose(); // The page or the controller is gone.
        }

        try
        {
            deferral.Complete();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // Closed meanwhile.
        }
    }

    private static async Task<Reply> ServeEntryAsync(EntryState state, string? rangeHeader, bool head)
    {
        if (state.Source is not { } source)
        {
            return state.Failure == 403 ? Reply.Status(403, "Forbidden") : state.Failure == 404 ? Reply.Status(404, "Not Found") : Reply.Status(500, "Internal Server Error");
        }

        var length = source.Length;
        var contentType = "Content-Type: " + MimeType(state.Entry.Name);
        const string common = "Accept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: sandbox; default-src 'none'"; // Opened on its own, the file is inert content, never our origin.
        var evaluated = ByteRange.Evaluate(rangeHeader, length);
        if (evaluated.Kind == ByteRangeKind.Unsatisfiable)
        {
            return new Reply(null, 416, "Range Not Satisfiable", $"{common}\r\nContent-Range: bytes */{length}");
        }

        var partial = evaluated.Kind == ByteRangeKind.Satisfiable;
        var range = partial ? evaluated.Range : (ByteRange?)null;
        var bodyLength = partial ? evaluated.Range.Length : length;
        var headers = $"{common}\r\n{contentType}\r\nContent-Length: {bodyLength}" +
            (partial ? $"\r\nContent-Range: bytes {evaluated.Range.Start}-{evaluated.Range.End}/{length}" : string.Empty);
        var (status, reason) = partial ? (206, "Partial Content") : (200, "OK");
        if (head)
        {
            return new Reply(null, status, reason, headers);
        }

        try
        {
            var stream = await source.OpenRangeAsync(range, state.Closing).ConfigureAwait(false);
            return new Reply(await BodyAsync(stream, bodyLength, state.Streams, state.Closing).ConfigureAwait(false), status, reason, headers);
        }
        catch (PluginPermissionException)
        {
            return Reply.Status(403, "Forbidden");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return Reply.Status(404, "Not Found"); // Another file is shown now.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reply.Status(500, "Internal Server Error");
        }
    }

    /// <summary>A file under the preview's webRoot; anything else (outside it, missing, a folder) is 404.</summary>
    private static Reply ServePageFile(string root, string encodedPath, bool head, StreamTracker tracker)
    {
        var segments = encodedPath.TrimStart('/').Split('/');
        var parts = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var part = Uri.UnescapeDataString(segment);
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            {
                return Reply.Status(404, "Not Found");
            }

            parts.Add(part);
        }

        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Reply.Status(404, "Not Found");
        }

        try
        {
            var file = new FileInfo(full);
            if (!file.Exists)
            {
                return Reply.Status(404, "Not Found");
            }

            var headers = $"Content-Type: {MimeType(file.Name)}\r\nContent-Length: {file.Length}\r\nCache-Control: no-cache\r\n" +
                $"X-Content-Type-Options: nosniff\r\nContent-Security-Policy: {PageSecurityPolicy}";
            if (head)
            {
                return new Reply(null, 200, "OK", headers);
            }

            Stream body;
            if (file.Length <= MaxBufferedBytes)
            {
                body = new MemoryStream(File.ReadAllBytes(full), writable: false);
            }
            else
            {
                body = tracker.Track(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan));
            }

            return new Reply(body, 200, "OK", headers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reply.Status(404, "Not Found");
        }
    }

    /// <summary>
    /// WebView2 reads the response on its own thread and may never finish (an aborted fetch), so small bodies are copied
    /// to memory and the file closed at once; large ones are tracked and closed at the end, on the entry's change or on dispose.
    /// </summary>
    private static async Task<Stream> BodyAsync(Stream stream, long length, StreamTracker tracker, CancellationToken cancellationToken)
    {
        if (length > MaxBufferedBytes)
        {
            return tracker.Track(stream);
        }

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[length];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return new MemoryStream(buffer, 0, read, writable: false);
        }
    }

    // ---- navigation ----

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsOwn(uri))
        {
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            OpenInBrowser(e.Uri);
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Frames may show the page's own files, about:blank/srcdoc and blobs; others are cancelled quietly.
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (IsOwn(uri) || uri.Scheme is "about" or "blob"))
        {
            return;
        }

        e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated)
        {
            OpenInBrowser(e.Uri);
        }
    }

    /// <summary>
    /// A link the user clicked leaves the preview for the browser panel, through the host of the plugin whose page is on
    /// screen (audited), at most once a second. Navigations a script starts on its own never get here: they could carry data out.
    /// </summary>
    private void OpenInBrowser(string url)
    {
        if (_disposed || _pageHost is not { } host || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || IsOwn(uri))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastExternalOpen < 1000)
        {
            return;
        }

        _lastExternalOpen = now;
        _ = OpenUrlAsync(host, uri.AbsoluteUri);
    }

    private static async Task OpenUrlAsync(IPluginHost host, string url)
    {
        try
        {
            await host.OpenUrlAsync(url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            host.Notify(ex.Message, isError: true);
        }
    }

    private bool IsOwn(Uri uri) =>
        _hostName is { } host && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    // ---- messages from the page ----

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_core is not { } core || _current is not { } state || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !IsOwn(source))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            try
            {
                HandleMessage(core, state, document.RootElement);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // A malformed message from the page is ignored.
            }
        }
    }

    private void HandleMessage(CoreWebView2 core, EntryState state, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "request" when message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id)
                && message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                && methodElement.GetString() is { Length: > 0 and <= 200 } method
                && message.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                && tokenElement.GetString() == state.Token:
                if (state.RequestsInFlight >= MaxRequestsInFlight)
                {
                    PostReply(core, state, id, ok: false, default, "too many requests");
                    break;
                }

                var parameters = message.TryGetProperty("params", out var p) ? p.Clone() : NullElement;
                state.RequestsInFlight++;
                _ = HandleRequestAsync(core, state, id, method, parameters);
                break;
            case "notify" or "copyText" when !state.Messages.TryTake(Environment.TickCount64):
                break; // More than MaxMessagesPerSecond: dropped quietly.
            case "notify" when message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                var notice = text.GetString() ?? string.Empty;
                state.Host.Notify(notice.Length > MaxNotifyChars ? notice[..MaxNotifyChars] + "…" : notice,
                    message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True);
                break;
            case "copyText" when message.TryGetProperty("text", out var copy) && copy.ValueKind == JsonValueKind.String:
                _ = CopyAsync(state.Host, copy.GetString() ?? string.Empty);
                break;
            case "key" when message.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                && keyElement.GetString() == "Escape":
                // Fallback for an Esc the page left alone that WebView2 did not report as an accelerator key
                // (when it did, OnAcceleratorKey already offered it to the app moments ago).
                if (Environment.TickCount64 - _lastAcceleratorEscape > 1000)
                {
                    _runShortcut(new KeyStroke("Escape"));
                }

                break;
        }
    }

    private async Task HandleRequestAsync(CoreWebView2 core, EntryState state, long id, string method, JsonElement parameters)
    {
        var (ok, result, error) = await Task.Run(async () =>
        {
            try
            {
                var value = await state.Plugin.MessageAsync(state.Entry, method, parameters, state.Host, state.Closing).ConfigureAwait(false);
                return (true, value, (string?)null);
            }
            catch (OperationCanceledException)
            {
                return (false, default(JsonElement), (string?)null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (false, default(JsonElement), ex.Message);
            }
        });

        state.RequestsInFlight--;
        if ((!ok && error is null) || _current != state || _core != core)
        {
            return; // Cancelled, or the reply is about a file no longer shown.
        }

        PostReply(core, state, id, ok, result, error);
    }

    private static void PostReply(CoreWebView2 core, EntryState state, long id, bool ok, JsonElement result, string? error)
    {
        Post(core, writer =>
        {
            writer.WriteString("type", "reply");
            writer.WriteNumber("id", id);
            writer.WriteString("token", state.Token);
            if (ok)
            {
                writer.WritePropertyName("result");
                if (result.ValueKind == JsonValueKind.Undefined)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    result.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString("error", error);
            }
        });
    }

    private static async Task CopyAsync(IPluginHost host, string text)
    {
        try
        {
            await host.CopyTextAsync(text.Length > MaxCopyChars ? text[..MaxCopyChars] : text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            host.Notify(ex.Message, isError: true);
        }
    }

    private static void Post(CoreWebView2 core, Action<Utf8JsonWriter> body)
    {
        try
        {
            core.PostWebMessageAsJson(Json(writer =>
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The page is gone.
        }
    }

    // ---- keys ----

    /// <summary>
    /// Keys pressed inside the page: F-keys (with any modifiers), Esc, Tab, Ctrl+Q, Alt combos without Ctrl and Ctrl+Shift
    /// combos without Alt are offered to the app first (handled = the page never sees them); the rest stay with the page.
    /// Ctrl+Alt is AltGr on many keyboards (Polish ą, ł…), so Ctrl+Alt+&lt;character key&gt; always goes to the page.
    /// </summary>
    private void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
        {
            return;
        }

        var vk = (int)e.VirtualKey;
        var ctrl = WebView2Host.IsKeyDown(VkControl);
        var alt = WebView2Host.IsKeyDown(VkMenu);
        var shift = WebView2Host.IsKeyDown(VkShift);
        if (vk == VkEscape)
        {
            _lastAcceleratorEscape = Environment.TickCount64;
        }

        var forwarded = vk is (>= VkF1 and <= VkF12)
            || (!(ctrl && alt) && vk is VkEscape or VkTab)
            || (ctrl && !alt && !shift && vk == 'Q')
            || (alt && !ctrl)
            || (ctrl && shift && !alt);
        if (!forwarded || KeyName(vk) is not { } key)
        {
            return;
        }

        var modifiers = (ctrl ? CoreModifiers.Ctrl : 0) | (alt ? CoreModifiers.Alt : 0) | (shift ? CoreModifiers.Shift : 0);
        e.Handled = _runShortcut(new KeyStroke(key, modifiers));
    }

    /// <summary>Avalonia <c>Key</c> names (as <see cref="KeyStroke"/> expects) for the virtual keys forwarded to the keymap.</summary>
    private static string? KeyName(int vk) => vk switch
    {
        >= VkF1 and <= VkF12 => "F" + (vk - VkF1 + 1),
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => "D" + (char)vk,
        VkEscape => "Escape",
        VkTab => "Tab",
        0x0D => "Enter",
        0x08 => "Back",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        0xBC => "OemComma",
        0xBE => "OemPeriod",
        _ => null,
    };

    // ---- data ----

    private static void WriteEntryData(Utf8JsonWriter writer, EntryState state, string hostName)
    {
        var entry = state.Entry;
        writer.WriteString("token", state.Token);
        writer.WritePropertyName("entry");
        writer.WriteStartObject();
        writer.WriteString("name", entry.Name);
        writer.WriteString("path", entry.Path);
        writer.WriteString("kind", entry.Kind.ToString().ToLowerInvariant());
        writer.WriteNumber("size", entry.Size);
        if (entry.LastWriteTime == default)
        {
            writer.WriteNull("modified");
        }
        else
        {
            writer.WriteString("modified", entry.LastWriteTime);
        }

        writer.WriteEndObject();
        writer.WriteString("fileUrl", $"https://{hostName}{EntryPrefix}{state.Token}/{Uri.EscapeDataString(entry.Name)}");
        writer.WritePropertyName("state");
        if (state.Preview.StateJson is { } stateJson)
        {
            try
            {
                using var parsed = JsonDocument.Parse(stateJson);
                parsed.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                writer.WriteNullValue();
            }
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteSkin(Utf8JsonWriter writer, SkinBrushes brushes)
    {
        writer.WriteStartObject();
        writer.WriteString("background", Hex(brushes.PanelBackground));
        writer.WriteString("foreground", Hex(brushes.FileForeground));
        writer.WriteString("accent", Hex(brushes.Accent));
        writer.WriteString("muted", Hex(brushes.MutedForeground));
        writer.WriteString("selection", Hex(brushes.SelectedBackground ?? brushes.CursorBackground));
        writer.WriteString("border", Hex(brushes.PanelBorder));
        writer.WriteString("fontFamily", brushes.Skin.Typography.FontFamily);
        writer.WriteNumber("fontSize", brushes.FontSize);
        writer.WriteEndObject();
    }

    /// <summary>JSON text safe inside a script: the default encoder escapes non-ASCII, and U+2028/2029 are escaped explicitly.</summary>
    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan).Replace(LineSeparator, "\\u2028", StringComparison.Ordinal).Replace(ParagraphSeparator, "\\u2029", StringComparison.Ordinal);
    }

    private static string Hex(IImmutableSolidColorBrush brush) => $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";

    private static System.Drawing.Color DrawingColor(IImmutableSolidColorBrush brush) =>
        System.Drawing.Color.FromArgb(255, brush.Color.R, brush.Color.G, brush.Color.B);

    private static string MimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json",
        ".txt" or ".md" or ".csv" or ".log" => "text/plain; charset=utf-8",
        ".xml" => "application/xml",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".wasm" => "application/wasm",
        ".pdf" => "application/pdf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream",
    };

    private readonly record struct Reply(Stream? Body, int StatusCode, string Reason, string Headers)
    {
        public static Reply Status(int statusCode, string reason) => new(null, statusCode, reason, "Cache-Control: no-store");
    }

    /// <summary>One shown file: its token, its source (null when it could not be opened), and what to stop when it is replaced.</summary>
    private sealed class EntryState(WebPreview preview, VfsEntry entry, ExternalPreviewPlugin plugin, IPluginHost host, EntryFileSource? source, string token, int failure)
    {
        // The token is captured once: the source is disposed on Close, and a cancelled, disposed source's token still works.
        private readonly (CancellationTokenSource Source, CancellationToken Token) _closing = NewClosing();
        private int _closed;

        public WebPreview Preview { get; } = preview;

        public VfsEntry Entry { get; } = entry;

        public ExternalPreviewPlugin Plugin { get; } = plugin;

        public IPluginHost Host { get; } = host;

        public EntryFileSource? Source { get; } = source;

        public string Token { get; } = token;

        /// <summary>HTTP status for file requests when <see cref="Source"/> is null.</summary>
        public int Failure { get; } = failure;

        public StreamTracker Streams { get; } = new();

        /// <summary>notify/copyText messages from the page: at most <see cref="MaxMessagesPerSecond"/> a second.</summary>
        public RateWindow Messages { get; } = new(MaxMessagesPerSecond, 1000);

        /// <summary><c>c4k.request</c> calls not answered yet (UI thread only).</summary>
        public int RequestsInFlight { get; set; }

        /// <summary>Cancelled when another file replaces this one (pending plugin requests, reads in progress).</summary>
        public CancellationToken Closing => _closing.Token;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _closing.Source.Cancel();
            _closing.Source.Dispose();
            Streams.CloseAll();
            if (Source is { } source)
            {
                // Deletes a spooled copy; DisposeAsync catches its own IO errors.
                _ = Task.Run(async () => await source.DisposeAsync().ConfigureAwait(false));
            }
        }

        private static (CancellationTokenSource, CancellationToken) NewClosing()
        {
            var source = new CancellationTokenSource();
            return (source, source.Token);
        }
    }

    /// <summary>Response streams WebView2 may still be reading; closing them releases the file handles.</summary>
    private sealed class StreamTracker
    {
        private readonly HashSet<TrackedStream> _open = [];
        private bool _closed;

        public Stream Track(Stream inner)
        {
            var tracked = new TrackedStream(inner, this);
            lock (_open)
            {
                if (_closed)
                {
                    inner.Dispose();
                    return new MemoryStream([], writable: false);
                }

                _open.Add(tracked);
            }

            return tracked;
        }

        public void Remove(TrackedStream stream)
        {
            lock (_open)
            {
                _open.Remove(stream);
            }
        }

        public void CloseAll()
        {
            TrackedStream[] open;
            lock (_open)
            {
                _closed = true;
                open = [.. _open];
                _open.Clear();
            }

            foreach (var stream in open)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Read-only pass-through that closes its file at the end of the data or when the tracker closes it.</summary>
    private sealed class TrackedStream(Stream inner, StreamTracker tracker) : Stream
    {
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return 0;
            }

            int read;
            try
            {
                read = inner.Read(buffer);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                return 0; // Closed by the tracker while WebView2 was reading.
            }

            if (read == 0)
            {
                Dispose();
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                tracker.Remove(this);
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
