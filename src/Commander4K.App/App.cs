using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Commander4K.App;

public sealed class App : Application
{
    private static readonly Lazy<WindowIcon?> AppIcon = new(() =>
        typeof(App).Assembly.GetManifestResourceStream("commander4k.ico") is { } stream ? new WindowIcon(stream) : null);

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;

        // Every window (main, dialogs, viewers) gets the app icon in its title bar and on the taskbar.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Icon ??= AppIcon.Value);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow();
            desktop.MainWindow = main;
            Splash.SplashWindow.ShowWhileStarting(main);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
