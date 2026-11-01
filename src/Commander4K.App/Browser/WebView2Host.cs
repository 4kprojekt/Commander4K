using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Commander4K.App.Browser;

/// <summary>
/// Hosts a WebView2 (Edge) controller in a native child window. Avalonia owns the child HWND (position, visibility);
/// this control creates the controller on it and keeps its bounds in sync. The controller is closed when the control
/// leaves the visual tree. One <see cref="CoreWebView2Environment"/> per profile folder is shared by all instances, so
/// sign-ins (Gemini, ChatGPT…) survive closing the panel and restarting the app.
/// </summary>
public sealed partial class WebView2Host : NativeControlHost
{
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Environments = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _profileFolder;
    private readonly string? _browserArguments;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    /// <param name="browserArguments">Chromium switches for the profile's environment (only the first environment of a folder uses them).</param>
    public WebView2Host(string profileFolder, string? browserArguments = null)
    {
        _profileFolder = profileFolder;
        _browserArguments = browserArguments;
    }

    /// <summary>Raised on the UI thread once the browser can navigate.</summary>
    public event Action<CoreWebView2Controller>? Ready;

    /// <summary>Raised on the UI thread when WebView2 cannot start (runtime missing, profile locked…).</summary>
    public event Action<Exception>? Failed;

    public CoreWebView2Controller? Controller => _controller;

    /// <summary>Installed Edge WebView2 runtime version, or <c>null</c> when it is not installed.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or COMException)
        {
            return null;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _destroyed = false;
        _ = InitializeAsync(handle.Handle);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        _controller?.Close();
        _controller = null;
        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateBounds(size);
        return size;
    }

    /// <summary>Gives keyboard focus to the page (the native window does not take part in Avalonia's focus).</summary>
    public void FocusPage() => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>The shared environment for a profile folder (the browser and the Mermaid renderer use the same one).</summary>
    /// <param name="browserArguments">Chromium switches, used when this call creates the folder's environment.</param>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync(string profileFolder, string? browserArguments = null)
    {
        lock (Environments)
        {
            if (!Environments.TryGetValue(profileFolder, out var environment) || environment.IsFaulted)
            {
                Directory.CreateDirectory(profileFolder);
                environment = CoreWebView2Environment.CreateAsync(null, profileFolder,
                    browserArguments is null ? null : new CoreWebView2EnvironmentOptions(browserArguments));
                Environments[profileFolder] = environment;
            }

            return environment;
        }
    }

    private async Task InitializeAsync(nint hwnd)
    {
        try
        {
            var controller = await (await GetEnvironmentAsync(_profileFolder, _browserArguments)).CreateCoreWebView2ControllerAsync(hwnd);
            if (_destroyed)
            {
                controller.Close(); // The panel closed while the browser was starting.
                return;
            }

            _controller = controller;
            UpdateBounds(Bounds.Size);
            Ready?.Invoke(controller);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private void UpdateBounds(Size size)
    {
        if (_controller is null)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
    }

    /// <summary>Modifier state for keys the page reports through <c>AcceleratorKeyPressed</c>.</summary>
    internal static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
