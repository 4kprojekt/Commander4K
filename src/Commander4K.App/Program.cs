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
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
