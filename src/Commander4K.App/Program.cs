using Avalonia;
using Commander4K.App.Composition;
using Commander4K.Core.Localization;

namespace Commander4K.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The language must be known before any window or menu text is created; this reads two small files.
        var config = Path.Combine(AppHost.ResolveDataDirectory(), "config");
        Loc.Initialize(Loc.ReadLanguageSetting(Path.Combine(config, "settings.json")), config);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
