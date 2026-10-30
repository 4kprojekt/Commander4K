using Avalonia;
using Avalonia.Styling;
using Commander4K.App.Composition;
using Commander4K.Core.Theming;

namespace Commander4K.App.Theming;

/// <summary>Holds the active skin; every view repaints from <see cref="Changed"/>. UI thread only.</summary>
public sealed class SkinManager
{
    private readonly AppHost? _host;

    public SkinManager(AppHost? host, Skin initial)
    {
        _host = host;
        Current = new SkinBrushes(initial);
        ApplyThemeVariant(initial);
    }

    public SkinBrushes Current { get; private set; }

    public event Action<SkinBrushes>? Changed;

    /// <summary>Shows a skin without remembering it (live preview in the picker and the AI generator).</summary>
    public void Preview(Skin skin)
    {
        Current = new SkinBrushes(skin);
        ApplyThemeVariant(skin);
        Changed?.Invoke(Current);
    }

    /// <summary>Applies and persists the skin as the user's choice.</summary>
    public void Apply(Skin skin)
    {
        Preview(skin);
        if (_host is not null)
        {
            _host.Settings.Skin = skin.Id;
            _ = _host.SaveSettingsAsync();
        }
    }

    private static void ApplyThemeVariant(Skin skin)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = string.Equals(skin.Chrome.ThemeVariant, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }
}
