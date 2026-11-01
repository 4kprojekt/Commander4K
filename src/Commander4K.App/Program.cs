using Avalonia;
using Commander4K.App.Composition;
using Commander4K.App.Splash;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The language must be known before any window or menu text is created; this reads two small files.
        var config = Path.Combine(AppHost.ResolveDataDirectory(), "config");
        // C4K_LANGUAGE (e.g. "en") overrides the setting for one run: checking translations, the VS Code launch profile.
        var language = Environment.GetEnvironmentVariable("C4K_LANGUAGE") is { Length: > 0 } forced
            ? forced
            : Loc.ReadLanguageSetting(Path.Combine(config, "settings.json"));
        Loc.Initialize(language, config);
        var startup = AppSettings.ReadStartupSettings(Path.Combine(config, "settings.json"));
        MainWindow.StartupPlacement = startup?.Window;
        // User skins load with the host; until then the splash uses the built-in skin of that id, or Grafit.
        SplashWindow.StartupSkin = startup?.SplashScreen == false
            ? null
            : BuiltInSkins.All.FirstOrDefault(s => s.Id == startup?.Skin) ?? BuiltInSkins.ModernDark;
        SplashWindow.StartupDensity = startup?.Density;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // A language change asked for a restart: the new process starts once this one has saved its state and closed.
        if (RestartRequested && Environment.ProcessPath is { } executable)
        {
            var restart = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (var argument in args)
            {
                restart.ArgumentList.Add(argument);
            }

            System.Diagnostics.Process.Start(restart);
        }
    }

    /// <summary>Set before the main window closes to start the app again (the UI language is loaded only at start-up).</summary>
    internal static bool RestartRequested { get; set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
