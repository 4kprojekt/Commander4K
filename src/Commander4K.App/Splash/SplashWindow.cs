using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App.Splash;

/// <summary>
/// The start-up splash (borderless, over the main window until it has started) and the same animation as a normal
/// window from "About". Esc or a click skips the splash.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly SplashScene _scene;

    public SplashWindow(SkinBrushes brushes, SplashMode mode)
    {
        _scene = new SplashScene(brushes, mode);
        Title = "Commander4K";
        WindowSkin.Apply(this, brushes);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = _scene.ViewSize.Width;
        Height = _scene.ViewSize.Height;
        if (mode == SplashMode.Startup)
        {
            WindowDecorations = WindowDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            Topmost = true;
            Content = _scene;
            _scene.Finished += Close;
            PointerPressed += (_, _) => Close();
        }
        else
        {
            Height += 28;
            Content = new Grid().Rows("*,Auto").Children(
                _scene,
                new TextBlock()
                    .Row(1)
                    .Text(Loc.T("Spacja · od nowa   ←→ / Tab · aktywny panel   ↑↓ · tempo   Esc · zamknij"))
                    .Foreground(brushes.MutedForeground)
                    .FontFamily(brushes.MonospaceFamily)
                    .FontSize(11)
                    .HorizontalAlignment(HorizontalAlignment.Center)
                    .Margin(0, 4, 0, 8));
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (mode == SplashMode.Demo)
            {
                e.Handled = _scene.HandleKey(e.Key);
            }
        };
    }

    /// <summary>Skin for the start-up splash, chosen by <c>Program</c> from settings.json; <c>null</c> = splash turned off.</summary>
    public static Skin? StartupSkin { get; set; }

    public static string? StartupDensity { get; set; }

    /// <summary>Shows the splash over <paramref name="main"/> until its start-up has finished.</summary>
    public static void ShowWhileStarting(MainWindow main)
    {
        if (StartupSkin is not { } skin)
        {
            return;
        }

        var splash = new SplashWindow(new SkinBrushes(skin, StartupDensity), SplashMode.Startup);
        // Keep it on screen at most 80% of the work area (small or scaled displays).
        if (splash.Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            var fit = Math.Min(1, Math.Min(area.Width * 0.8 / splash.Width, area.Height * 0.8 / splash.Height));
            splash.Width *= fit;
            splash.Height *= fit;
        }

        splash.Closed += (_, _) => main.Activate();
        splash.Show();
        main.Started.ContinueWith(_ => Dispatcher.UIThread.Post(splash._scene.Complete), TaskScheduler.Default);
    }
}
