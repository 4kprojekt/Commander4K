using Commander4K.Core.Input;

namespace Commander4K.Core.Tests;

public sealed class KeymapTests
{
    [Theory]
    [InlineData("ctrl+shift+f5", "Ctrl+Shift+F5")]
    [InlineData("Shift+Control+F5", "Ctrl+Shift+F5")]
    [InlineData("Esc", "ESCAPE")]
    [InlineData("Alt+D1", "Alt+1")]
    [InlineData("Ctrl++", "Ctrl+PLUS")]
    [InlineData("Ctrl+PgDn", "Ctrl+PAGEDOWN")]
    public void Parse_normalises(string text, string expected) =>
        Assert.Equal(expected, KeyStroke.Parse(text).ToString());

    [Fact]
    public void Parse_rejects_unknown_modifier() =>
        Assert.Throws<FormatException>(() => KeyStroke.Parse("Hyper+A"));

    [Fact]
    public void Single_stroke_maps_to_command()
    {
        var engine = new KeymapEngine(DefaultKeymap.Bindings);

        var result = engine.Process(new KeyStroke("F5"));

        Assert.Equal(new KeymapResult(KeymapResultKind.Command, CommandIds.Copy), result);
        Assert.Equal(KeymapResultKind.NotHandled, engine.Process(new KeyStroke("F5", KeyModifiers.Win)).Kind);
    }

    [Fact]
    public void Chord_waits_for_second_stroke()
    {
        var engine = new KeymapEngine(new Dictionary<string, string> { ["Ctrl+K Ctrl+C"] = "cm_Comment" });

        Assert.Equal(KeymapResultKind.ChordPending, engine.Process(new KeyStroke("K", KeyModifiers.Ctrl)).Kind);
        Assert.Equal("cm_Comment", engine.Process(new KeyStroke("C", KeyModifiers.Ctrl)).CommandId);
        Assert.False(engine.IsChordPending);
    }

    [Fact]
    public void Broken_chord_falls_back_to_single_stroke()
    {
        var engine = new KeymapEngine(new Dictionary<string, string>
        {
            ["Ctrl+K Ctrl+C"] = "cm_Comment",
            ["F5"] = CommandIds.Copy,
        });

        engine.Process(new KeyStroke("K", KeyModifiers.Ctrl));

        Assert.Equal(CommandIds.Copy, engine.Process(new KeyStroke("F5")).CommandId);
    }

    [Fact]
    public async Task Registry_executes_after_freeze()
    {
        var registry = new CommandRegistry();
        var calls = 0;
        registry.Register(CommandIds.Refresh, "Refresh", () => calls++);
        registry.Freeze();

        Assert.True(await registry.ExecuteAsync("CM_REFRESH", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await registry.ExecuteAsync("cm_Missing", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => registry.Register("cm_Late", "", () => { }));
    }
}
