using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;
using Commander4K.Core.Updates;

namespace Commander4K.App.Splash;

public enum SplashMode
{
    /// <summary>Runs while the app starts: holds at 90% until <see cref="SplashScene.Complete"/>, then raises <see cref="SplashScene.Finished"/>.</summary>
    Startup,

    /// <summary>"About": the full 7 s loop, restarting by itself; keys change the active panel and the tempo.</summary>
    Demo,
}

/// <summary>
/// The start-up animation, drawn by hand (no HTML engine): an isometric desk with two file panels in a V, files flying
/// from the left panel to the right one, a ring round the logo and bars on the floor showing progress. Colours come
/// from the skin, so it looks like the app it introduces; light skins draw it without glow.
/// </summary>
public sealed class SplashScene : Control
{
    private const double DW = 245, DH = 130, RH = 11, R0 = 17;
    private const int Slots = 10, Cur = 4, NSeg = 28, NPk = 10;
    private const double PulseMs = 550, FlightMs = 850;
    private static readonly double C = Math.Cos(Math.PI / 6), S = Math.Sin(Math.PI / 6);

    // Name, size text, folder?
    private static readonly (string Name, string Size, bool Dir)[] Data =
    [
        ("..", Loc.T("<W GÓRĘ>"), true), (".git", Loc.T("<KATALOG>"), true), ("build", Loc.T("<KATALOG>"), true), ("config", Loc.T("<KATALOG>"), true),
        ("docs", Loc.T("<KATALOG>"), true), ("src", Loc.T("<KATALOG>"), true), ("tests", Loc.T("<KATALOG>"), true), ("tools", Loc.T("<KATALOG>"), true),
        ("build.ps1", "3,8 KB", false), ("CHANGELOG.md", "12,6 KB", false), ("Commander4K.sln", "6,2 KB", false),
        ("FileViewportCanvas.cs", "48,1 KB", false), ("SkinPalette.cs", "6,4 KB", false), ("SkinLoader.cs", "11,0 KB", false),
        ("grafit-dark.json", "1,9 KB", false), ("grafit-light.json", "1,9 KB", false), ("norton-commander.json", "1,8 KB", false),
        ("skin.schema.json", "7,2 KB", false), ("PanelView.cs", "22,5 KB", false), ("TabStrip.cs", "8,3 KB", false),
        ("FunctionKeyBar.cs", "4,1 KB", false), ("SplashScene.cs", "9,7 KB", false), ("QuickView.cs", "15,2 KB", false),
        ("SftpSession.cs", "18,9 KB", false), ("SmbBrowser.cs", "12,3 KB", false), ("pl.json", "41,0 KB", false), ("en.json", "36,6 KB", false),
        ("icons.svg", "14,8 KB", false), ("LICENSE", "1,1 KB", false), ("README.md", "4,0 KB", false), ("release-notes.md", "1,8 KB", false),
        ("Commander4K-x64.zip", "38,4 MB", false), ("publish.cmd", "0,9 KB", false), ("Directory.Build.props", "1,9 KB", false),
    ];

    private readonly SplashMode _mode;
    private readonly ScenePalette _c;
    private readonly (double Threshold, string Text)[] _messages;
    private readonly long _clock = Stopwatch.GetTimestamp();
    private readonly double _ox, _oy;
    private readonly Typeface _regular, _bold;
    private readonly Dictionary<(string, double, bool, IBrush), FormattedText> _texts = [];
    private readonly Dictionary<(IBrush, double), IPen> _pens = [];
    private readonly Matrix _floor, _dispA, _dispB, _ledsA, _ledsB, _hubTop, _hubFront, _baseTop, _baseFront;
    private readonly Geometry[] _baseBox, _hubBox, _packetBox;
    private readonly Geometry[] _wallA, _wallB;
    private readonly Point[][] _wires;
    private readonly Point[][] _arcs;
    private readonly Arc[] _flightArcs;
    private readonly Rect[] _segs;
    private readonly List<Flight> _flights = [];
    private readonly List<(int Item, double Time)> _arrivedFiles = [];
    private readonly double[] _ledA = new double[3], _ledB = new double[3], _arcPulse = [double.MinValue, double.MinValue, double.MinValue];
    private double _wirePulse1 = double.MinValue, _wirePulse2 = double.MinValue, _l2Until;
    private double _p, _doneAt, _off, _lastLaunch, _outUntil, _now;
    private double? _prev;
    private bool _done, _ready, _finishedRaised, _running;
    private bool _activeLeft = true;
    private int _base, _launches, _arrived;
    private double _scale = -1;

    public SplashScene(SkinBrushes brushes, SplashMode mode)
    {
        _mode = mode;
        _c = new ScenePalette(brushes.Skin.Palette);
        _regular = new Typeface(brushes.MonospaceFamily);
        _bold = new Typeface(brushes.MonospaceFamily, weight: FontWeight.Bold);
        _messages =
        [
            (0, Loc.T("inicjalizacja")), (0.12, Loc.T("skórka {0}", brushes.Skin.DisplayName)), (0.3, Loc.T("dyski i sieć")),
            (0.55, Loc.T("wtyczki · Git · EXIF · SHA-256")), (0.8, Loc.T("pierwszy katalog")), (1, Loc.T("gotowe")),
        ];
        Speed = 1;

        // Frame the scene by its extreme corners, padded to 4:3 (the figure's own framing).
        (double X, double Y, double Z)[] corners = [(-24, -24, 0), (336, -24, 0), (-24, 336, 0), (336, 336, 0), (0, 0, 182), (300, 0, 182), (0, 300, 182), (140, 140, 180)];
        var projected = corners.Select(p => ((p.X - p.Y) * C, ((p.X + p.Y) * S) - p.Z)).ToArray();
        double x0 = projected.Min(p => p.Item1), x1 = projected.Max(p => p.Item1), y0 = projected.Min(p => p.Item2), y1 = projected.Max(p => p.Item2);
        var margin = 0.09 * Math.Max(x1 - x0, y1 - y0);
        double w = x1 - x0 + (2 * margin), h = y1 - y0 + (2 * margin);
        _ox = margin - x0;
        _oy = margin - y0;
        if (w / h < 4.0 / 3)
        {
            _ox += ((h * 4 / 3) - w) / 2;
            w = h * 4 / 3;
        }
        else
        {
            _oy += ((w * 3 / 4) - h) / 2;
            h = w * 3 / 4;
        }

        ViewSize = new Size(w, h);

        _floor = Top(0, 0, 10);
        _baseTop = Top(-24, -24, 10);
        _baseFront = Front(-24, 336, 10);
        _dispA = Front(40, 18, 170);
        _ledsA = Front(40, 18, 36);
        _dispB = Side(18, 285, 170);
        _ledsB = Side(18, 285, 36);
        _hubTop = Top(147, 147, 24);
        _hubFront = Front(147, 207, 24);
        _baseBox = Rounded(-24, -24, 0, 360, 360, 10, 16);
        _hubBox = Rounded(147, 147, 10, 60, 60, 14, 10);
        _packetBox = Rounded(-8, -6, 0, 16, 12, 3, 1.5);
        _wallA = FlatBox(0, 0, 10, 300, 18, 172);
        _wallB = FlatBox(0, 18, 10, 18, 282, 172);
        _wires =
        [
            [new(22, 240), new(110, 240), new(110, 177), new(147, 177)],
            [new(177, 147), new(177, 110), new(240, 110), new(240, 22)],
        ];
        var segs = new List<Rect>();
        for (var k = 0; k < 7; k++) segs.Add(new Rect(135 + (k * 12), 130, 10, 4));
        for (var k = 0; k < 7; k++) segs.Add(new Rect(220, 135 + (k * 12), 4, 10));
        for (var k = 0; k < 7; k++) segs.Add(new Rect(207 - (k * 12), 220, 10, 4));
        for (var k = 0; k < 7; k++) segs.Add(new Rect(130, 207 - (k * 12), 4, 10));
        _segs = [.. segs];
        _flightArcs = [.. Enumerable.Range(0, 3).Select(k => new Arc(
            new Point3(24, 200 - (k * 35), 103), new Point3(178 + (k * 8), 178 + (k * 8), 212 - (k * 14)), new Point3(130 + (k * 35), 24, 147)))];
        _arcs = [.. _flightArcs.Select(a => Enumerable.Range(0, 29).Select(i => P(a.At(i / 28.0))).ToArray())];
        Restart(0);
    }

    /// <summary>Raised once in <see cref="SplashMode.Startup"/>, a moment after 100% is shown.</summary>
    public event Action? Finished;

    /// <summary>Size of the scene in its own units; the control scales it to fit.</summary>
    public Size ViewSize { get; }

    /// <summary>Tempo: 0.5, 1, 2 or 4.</summary>
    public double Speed { get; private set; }

    /// <summary>The app is ready: the bar runs to 100% quickly and the splash finishes.</summary>
    public void Complete() => _ready = true;

    /// <summary>Space restarts, ←/→ and Tab pick the active panel, ↑/↓ change the tempo.</summary>
    public bool HandleKey(Avalonia.Input.Key key)
    {
        switch (key)
        {
            case Avalonia.Input.Key.Space or Avalonia.Input.Key.Enter:
                Restart(_now);
                return true;
            case Avalonia.Input.Key.Left:
                _activeLeft = true;
                return true;
            case Avalonia.Input.Key.Right:
                _activeLeft = false;
                return true;
            case Avalonia.Input.Key.Tab:
                _activeLeft = !_activeLeft;
                return true;
            case Avalonia.Input.Key.Up or Avalonia.Input.Key.Down:
                double[] levels = [0.5, 1, 2, 4];
                var i = Math.Clamp(Array.IndexOf(levels, Speed) + (key == Avalonia.Input.Key.Up ? 1 : -1), 0, levels.Length - 1);
                Speed = levels[i];
                return true;
            default:
                return false;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => ViewSize;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _running = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false;
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!_running)
        {
            return;
        }

        Step(Stopwatch.GetElapsedTime(_clock).TotalMilliseconds);
        InvalidateVisual();
        RequestFrame();
    });

    // ---------- state ----------

    private void Step(double now)
    {
        _now = now;
        if (_prev is null)
        {
            _prev = now;
            _lastLaunch = now;
        }

        var dt = Math.Clamp(now - _prev.Value, 0, 64) * Speed;
        _prev = now;

        if (!_done)
        {
            if (_mode == SplashMode.Demo)
            {
                _p += dt / 7000;
            }
            else
            {
                // Start-up: a steady 2.4 s run that waits at 90% for the app, then hurries to 100%.
                _p = _ready ? _p + Math.Max(dt / 2400, dt / 450) : Math.Min(0.9, _p + (dt / 2400));
            }

            if (_p >= 1)
            {
                Finish(now);
            }
        }
        else if (_mode == SplashMode.Demo && _flights.Count == 0 && now - _doneAt > 2600 / Speed)
        {
            Restart(now);
        }
        else if (_mode == SplashMode.Startup && !_finishedRaised && now - _doneAt > 450)
        {
            _finishedRaised = true;
            Finished?.Invoke();
        }

        // The left panel scrolls, fast while indexing.
        _off += dt * (_done ? 0 : _p < 0.32 ? 0.016 : 0.007);
        _base = (int)Math.Floor(_off) % Data.Length;

        // Files take off…
        if (!_done && _p > 0.06 && now - _lastLaunch > 150 / Speed)
        {
            _lastLaunch = now;
            Launch(now);
        }

        // …and land.
        for (var i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            if ((now - f.Start) / (FlightMs / Speed) >= 1)
            {
                _flights.RemoveAt(i);
                Arrive(f, now);
            }
        }
    }

    private void Launch(double now)
    {
        var item = (_base + Cur) % Data.Length;
        if (Data[item].Name == ".." || _flights.Count >= NPk)
        {
            return;
        }

        var k = _launches++ % 3;
        _flights.Add(new Flight(k, now, item));
        _outUntil = now + 140;
        _arcPulse[k] = now;
        _wirePulse1 = now;
        _ledB[k] = now + 120;
    }

    private void Arrive(Flight f, double now)
    {
        _arrivedFiles.Insert(0, (f.Item, now));
        if (_arrivedFiles.Count > Slots)
        {
            _arrivedFiles.RemoveRange(Slots, _arrivedFiles.Count - Slots);
        }

        _arrived++;
        _wirePulse2 = now;
        _ledA[f.Arc] = now + 120;
        if (!_done)
        {
            _l2Until = now + 110;
        }
    }

    private void Restart(double now)
    {
        _p = 0;
        _done = false;
        _off = 0;
        _base = 0;
        _launches = 0;
        _arrived = 0;
        _lastLaunch = now;
        _flights.Clear();
        _arrivedFiles.Clear();
    }

    private void Finish(double now)
    {
        _p = 1;
        _done = true;
        _doneAt = now;
    }

    // ---------- drawing ----------

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var k = Math.Min(bounds.Width / ViewSize.Width, bounds.Height / ViewSize.Height);
        if (k <= 0)
        {
            return;
        }

        if (k != _scale)
        {
            _scale = k;
            _pens.Clear();
        }

        context.FillRectangle(_c.Bg, bounds);
        var now = _now;
        if (_c.Glow)
        {
            // The ambient pool of light under the desk.
            var pool = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.52, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.52, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.45, RelativeUnit.Relative),
                GradientStops = [new GradientStop(_c.AccentDimColor, 0), new GradientStop(Colors.Transparent, 0.72)],
            };
            context.FillRectangle(pool, bounds);
        }

        var view = Matrix.CreateScale(k, k) * Matrix.CreateTranslation((bounds.Width - (ViewSize.Width * k)) / 2, (bounds.Height - (ViewSize.Height * k)) / 2);
        using var _ = context.PushTransform(view);

        // Back to front, as the figure stacks its groups.
        DrawFaces(context, _baseBox);
        using (context.PushTransform(_baseTop))
        {
            context.DrawRectangle(null, Hair(_c.Detail), new RoundedRect(new Rect(8, 8, 344, 344), 10));
        }

        using (context.PushTransform(_baseFront))
        {
            context.DrawRectangle(_c.Recess, null, new RoundedRect(new Rect(12, 3.5, 336, 3), 1.5));
            Led(context, new Rect(12, 3.5, 336 * _p, 3), 1.5, true);
        }

        DrawFloor(context, now);
        DrawFaces(context, _wallA);
        using (context.PushTransform(_dispA))
        {
            DrawDisplay(context, left: false, @"D:\Kopie\Commander4K", now);
        }

        using (context.PushTransform(_ledsA))
        {
            DrawLeds(context, Loc.T("PANEL PRAWY · {0}", "D:"), _ledA, now);
        }

        DrawFaces(context, _wallB);
        using (context.PushTransform(_dispB))
        {
            DrawDisplay(context, left: true, @"C:\Projekty\Commander4K", now);
        }

        using (context.PushTransform(_ledsB))
        {
            DrawLeds(context, Loc.T("PANEL LEWY · {0}", "C:"), _ledB, now);
        }

        DrawHub(context, now);
        DrawFlights(context, now);
    }

    private void DrawFloor(DrawingContext context, double now)
    {
        using var _ = context.PushTransform(_floor);
        var detail = Hair(_c.Detail);
        for (var a = 40.0; a <= 320; a += 20) context.DrawLine(detail, new Point(a, 34), new Point(a, 270));
        for (var a = 40.0; a <= 260; a += 20) context.DrawLine(detail, new Point(34, a), new Point(320, a));
        Led(context, new Rect(20, 19, 278, 3), 1.5, true);
        Led(context, new Rect(19, 22, 3, 276), 1.5, true);

        var under = Pen(_c.Deck, 3.5, scaled: true); // Masks the grid lines under the wire.
        var pulses = new[] { _wirePulse1, _wirePulse2 };
        for (var i = 0; i < _wires.Length; i++)
        {
            var geometry = Polyline(_wires[i]);
            context.DrawGeometry(null, under, geometry);
            context.DrawGeometry(null, Hair(_done ? _c.Line : _c.Accent), geometry);
            DrawPulse(context, _wires[i], pulses[i], now);
        }

        // Progress ring round the logo, with one bright segment running round it.
        var n = _done ? NSeg : (int)Math.Floor(_p * NSeg);
        var spin = _done ? -1 : (int)(Math.Floor(now / 40) % NSeg);
        for (var i = 0; i < _segs.Length; i++)
        {
            var brush = i == spin ? _c.AccentHi : i < n ? _c.Accent : _c.Detail;
            Glow(context, _segs[i], 1, brush, i == spin || i < n ? 1.6 : 0);
            context.DrawRectangle(brush, null, new RoundedRect(_segs[i], 1));
        }

        var title = Text("Commander", 21, true, _c.InkHi);
        DrawText(context, title, 40, 300);
        DrawText(context, Text("4K", 21, true, _c.Accent), 40 + title.WidthIncludingTrailingWhitespace, 300);
        var pct = (int)Math.Round(_p * 100);
        DrawText(context, Text(pct + "%", 21, true, _c.AccentHi), 326, 300, 1);
        var message = _messages.Last(m => _p >= m.Threshold).Text + (_done ? string.Empty : "…");
        DrawText(context, Text(message, 8, false, _c.Ink), 40, 313);
        DrawText(context, Text(_mode == SplashMode.Startup ? Loc.T("Esc · pomiń") : Loc.T("Esc · zamknij"), 8, false, _c.Ink), 326, 313, 1);
        context.DrawRectangle(_c.Recess, null, new RoundedRect(new Rect(40, 319, 286, 4), 2));
        Led(context, new Rect(40, 319, 286 * _p, 4), 2, true);
    }

    private void DrawDisplay(DrawingContext context, bool left, string path, double now)
    {
        var on = _activeLeft == left;
        var hot = _done || on;
        if (_c.Glow)
        {
            Bloom(context, new Rect(-12, -12, DW + 24, DH + 24), 12, _done ? 0.4 : 0.22);
        }

        var glass = new RoundedRect(new Rect(0, 0, DW, DH), 4);
        context.DrawRectangle(hot ? _c.GlassOn : _c.Glass, hot ? Pen(_c.Accent, 1, scaled: true) : null, glass);
        using var clip = context.PushClip(glass);
        context.FillRectangle(on ? _c.AccentDim : _c.Recess, new Rect(0, 0, DW, 14));
        if (on)
        {
            context.FillRectangle(_c.Accent, new Rect(0, 0, 3, 14));
        }

        DrawText(context, Text(path, 7, true, _c.InkHi), 9, 9.8);
        using (context.PushClip(new Rect(0, 16, DW, DH - 30)))
        {
            for (var i = 0; i < Slots; i++)
            {
                if (left)
                {
                    var item = (_base + i) % Data.Length;
                    var cursor = i == Cur ? (on ? RowCursor.Active : RowCursor.Inactive) : RowCursor.None;
                    DrawRow(context, i, item, cursor, fresh: false, outgoing: i == Cur && now < _outUntil);
                }
                else if (i < _arrivedFiles.Count)
                {
                    var cursor = i == 0 ? (on ? RowCursor.Active : RowCursor.Inactive) : RowCursor.None;
                    DrawRow(context, i, _arrivedFiles[i].Item, cursor, fresh: i < 3 && now - _arrivedFiles[i].Time < 420, outgoing: false);
                }
            }
        }

        if (left && !_done && _p < 0.34)
        {
            // The indexing beam sweeping the list.
            var beam = new Rect(0, R0 + ((0.5 - (0.5 * Math.Cos(now / 260))) * 96), DW, 2.5);
            Glow(context, beam, 0, _c.Accent, 2);
            context.FillRectangle(_c.Accent, beam);
        }

        context.DrawLine(Hair(_c.Detail), new Point(0, DH - 13), new Point(DW, DH - 13));
        var pct = (int)Math.Round(_p * 100);
        string status, count;
        if (left)
        {
            status = _done ? Loc.T("gotowe") : _p < 0.34 ? Loc.T("indeksowanie…") : "C: · NTFS";
            count = Loc.T("{0} pozycji", Math.Round(_p * 48210).ToString("N0", CultureInfo.CurrentCulture));
        }
        else
        {
            status = _done ? Loc.T("gotowe") : Loc.T("wczytywanie {0}%", pct);
            count = Loc.P(_arrived, "{0} plik|{0} pliki|{0} plików");
        }

        DrawText(context, Text(status, 6, false, _c.Ink), 9, DH - 4);
        DrawText(context, Text(count, 6, false, _c.Ink), DW - 8, DH - 4, 1);
    }

    private void DrawRow(DrawingContext context, int slot, int item, RowCursor cursor, bool fresh, bool outgoing)
    {
        var (name, size, dir) = Data[item];
        using var _ = context.PushTransform(Matrix.CreateTranslation(0, R0 + (slot * RH)));
        var background = new RoundedRect(new Rect(3, 0, DW - 6, RH - 1), 1.5);
        switch (cursor)
        {
            case RowCursor.Active:
                context.DrawRectangle(_c.Accent, null, background);
                break;
            case RowCursor.Inactive:
                context.DrawRectangle(null, Hair(_c.Accent), background);
                break;
            default:
                if (fresh)
                {
                    context.DrawRectangle(_c.AccentDim, null, background);
                }

                break;
        }

        var active = cursor == RowCursor.Active;
        var icon = new RoundedRect(new Rect(8, 2.5, 5, 5), 1);
        context.DrawRectangle(dir && !active ? _c.AccentDim : null, Hair(active ? _c.Panel : dir ? _c.InkHi : _c.Ink), icon);
        var nameBrush = active ? _c.Panel : outgoing || fresh ? _c.AccentHi : dir ? _c.InkHi : _c.Ink;
        DrawText(context, Text(name, 6.6, dir || fresh, nameBrush), 18, 7.4);
        using (context.PushOpacity(active ? 1 : 0.7))
        {
            DrawText(context, Text(size, 6, false, active ? _c.Panel : _c.Ink), DW - 8, 7.4, 1);
        }
    }

    private void DrawLeds(DrawingContext context, string tag, double[] until, double now)
    {
        DrawText(context, Text(tag, 6, false, _c.Ink), 0, 8);
        for (var i = 0; i < 3; i++)
        {
            var lit = now < until[i];
            var center = new Point(DW - 18 + (i * 8), 6);
            if (lit && _c.Glow)
            {
                context.DrawEllipse(_c.AccentGlow, null, center, 4, 4);
            }

            context.DrawEllipse(lit ? _c.Accent : _c.Detail, null, center, 2, 2);
        }
    }

    private void DrawHub(DrawingContext context, double now)
    {
        DrawFaces(context, _hubBox);
        using (context.PushTransform(_hubTop))
        {
            if (_c.Glow)
            {
                Bloom(context, new Rect(2, 2, 56, 56), 14, _done ? 0.4 : 0.22);
            }

            Glow(context, new Rect(15, 12, 12, 36), 3, _c.Accent, 4);
            context.DrawRectangle(_c.Accent, null, new RoundedRect(new Rect(15, 12, 12, 36), 3));
            var l2 = _done || now < _l2Until;
            using (context.PushOpacity(0.7))
            {
                Led(context, new Rect(33, 12, 12, 36), 3, l2);
            }
        }

        using (context.PushTransform(_hubFront))
        {
            DrawText(context, Text("C4K · " + AppVersion.Current, 6, false, _c.Ink), 30, 9.5, 0.5);
        }
    }

    private void DrawFlights(DrawingContext context, double now)
    {
        var arcPen = Pen(_done ? _c.Detail : _c.AccentDim, 1, dashed: true);
        for (var k = 0; k < _arcs.Length; k++)
        {
            context.DrawGeometry(null, arcPen, Polyline(_arcs[k]));
            DrawPulse(context, _arcs[k], _arcPulse[k], now);
        }

        foreach (var f in _flights)
        {
            var t = (now - f.Start) / (FlightMs / Speed);
            var e = t < 0.5 ? 2 * t * t : 1 - (Math.Pow((-2 * t) + 2, 2) / 2);
            var q = _flightArcs[f.Arc].At(e);
            using var _ = context.PushTransform(Matrix.CreateTranslation((q.X - q.Y) * C, ((q.X + q.Y) * S) - q.Z));
            if (_c.Glow)
            {
                using (context.PushOpacity(0.35))
                {
                    context.DrawGeometry(_c.Accent, Pen(_c.Accent, 3, scaled: true), _packetBox[1]);
                }
            }

            context.DrawGeometry(_c.Accent, Hair(_c.AccentHi), _packetBox[0]);
            context.DrawGeometry(_c.AccentHi, Hair(_c.AccentHi), _packetBox[1]);
        }
    }

    /// <summary>A 70-unit dash running the length of a wire in 0.55 s (the figure's <c>stroke-dasharray</c> pulse).</summary>
    private void DrawPulse(DrawingContext context, Point[] points, double start, double now)
    {
        var t = (now - start) / PulseMs;
        if (t is < 0 or >= 1)
        {
            return;
        }

        var from = (-70 + (1070 * t)) / 1000;
        var part = Slice(points, Math.Max(0, from), Math.Min(1, from + 0.07));
        if (part.Length < 2)
        {
            return;
        }

        using var _ = context.PushOpacity(t < 0.9 ? 1 : (1 - t) * 10);
        context.DrawGeometry(null, Pen(_c.AccentHi, 2.2, round: true), Polyline(part));
    }

    private void DrawFaces(DrawingContext context, Geometry[] faces)
    {
        var line = Hair(_c.Line);
        for (var i = 0; i < faces.Length; i++)
        {
            // The last face of every box is its top.
            context.DrawGeometry(i == faces.Length - 1 ? _c.Deck : _c.Body, line, faces[i]);
        }
    }

    /// <summary>A lit bar: accent fill with a soft halo (the figure's <c>.led.hot</c> + <c>filter: soft</c>).</summary>
    private void Led(DrawingContext context, Rect rect, double radius, bool hot)
    {
        if (rect.Width <= 0)
        {
            return;
        }

        if (hot)
        {
            Glow(context, rect, radius, _c.Accent, 2.2);
        }

        context.DrawRectangle(hot ? _c.Accent : _c.Detail, null, new RoundedRect(rect, radius));
    }

    /// <summary>Stand-in for an SVG Gaussian blur: a few inflated, faint copies of the shape.</summary>
    private void Glow(DrawingContext context, Rect rect, double radius, IBrush brush, double spread)
    {
        if (!_c.Glow || spread <= 0)
        {
            return;
        }

        for (var i = 3; i >= 1; i--)
        {
            var grow = spread * i / 3;
            using var _ = context.PushOpacity(0.16);
            context.DrawRectangle(brush, null, new RoundedRect(rect.Inflate(grow), radius + grow));
        }
    }

    /// <summary>The wide halo behind a lit display or the logo.</summary>
    private void Bloom(DrawingContext context, Rect rect, double radius, double opacity)
    {
        for (var i = -2; i <= 2; i++)
        {
            var grow = i * 5;
            using var _ = context.PushOpacity(opacity / 5);
            context.DrawRectangle(_c.Accent, null, new RoundedRect(rect.Inflate(grow), Math.Max(0, radius + grow)));
        }
    }

    // ---------- text and pens ----------

    private FormattedText Text(string text, double size, bool bold, IBrush brush)
    {
        if (_texts.Count > 400)
        {
            _texts.Clear(); // Counters change every frame; keep the cache bounded.
        }

        var key = (text, size, bold, brush);
        if (!_texts.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? _bold : _regular, size, brush);
            _texts[key] = formatted;
        }

        return formatted;
    }

    /// <summary>Draws with the baseline at <paramref name="baseline"/>; <paramref name="anchor"/> 0 = start, 0.5 = middle, 1 = end.</summary>
    private static void DrawText(DrawingContext context, FormattedText text, double x, double baseline, double anchor = 0) =>
        context.DrawText(text, new Point(x - (text.WidthIncludingTrailingWhitespace * anchor), baseline - text.Baseline));

    /// <summary>One device pixel wide whatever the zoom (the figure's <c>vector-effect: non-scaling-stroke</c>).</summary>
    private IPen Hair(IBrush brush) => Pen(brush, 1);

    private IPen Pen(IBrush brush, double width, bool scaled = false, bool round = false, bool dashed = false)
    {
        var thickness = scaled ? width : width / _scale;
        var key = (brush, thickness * (round ? -1 : 1) * (dashed ? 7 : 1));
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(brush, thickness, dashed ? new ImmutableDashStyle([2, 5], 0) : null,
                round ? PenLineCap.Round : PenLineCap.Flat, PenLineJoin.Round).ToImmutable();
            _pens[key] = pen;
        }

        return pen;
    }

    // ---------- isometric geometry ----------

    private Point P(Point3 p) => new(((p.X - p.Y) * C) + _ox, ((p.X + p.Y) * S) - p.Z + _oy);

    private static Point D(Point3 p) => new((p.X - p.Y) * C, ((p.X + p.Y) * S) - p.Z);

    /// <summary>The 2D frame of a face: origin <paramref name="o"/>, local x along <paramref name="u"/>, local y along <paramref name="v"/>.</summary>
    private Matrix Plane(Point3 o, Point3 u, Point3 v)
    {
        Point po = P(o), du = D(u), dv = D(v);
        return new Matrix(du.X, du.Y, dv.X, dv.Y, po.X, po.Y);
    }

    private Matrix Top(double x, double y, double z) => Plane(new(x, y, z), new(1, 0, 0), new(0, 1, 0));

    private Matrix Front(double x, double y, double z) => Plane(new(x, y, z), new(1, 0, 0), new(0, 0, -1));

    private Matrix Side(double x, double y, double z) => Plane(new(x, y, z), new(0, -1, 0), new(0, 0, -1));

    /// <summary>Side, front and top faces of a square box.</summary>
    private Geometry[] FlatBox(double x, double y, double z, double w, double d, double h)
    {
        Geometry Face(Matrix m, double fw, double fh) =>
            Polygon([m.Transform(new Point(0, 0)), m.Transform(new Point(fw, 0)), m.Transform(new Point(fw, fh)), m.Transform(new Point(0, fh))]);

        return [Face(Side(x + w, y + d, z + h), d, h), Face(Front(x, y + d, z + h), w, h), Face(Top(x, y, z + h), w, d)];
    }

    /// <summary>A box with rounded vertical edges: one continuous wall along the visible outline plus the top.</summary>
    private Geometry[] Rounded(double x, double y, double z, double w, double d, double h, double r)
    {
        r = Math.Min(r, Math.Min(w / 2, d / 2));
        const int n = 10;
        const double half = Math.PI / 2, quarter = Math.PI / 4;
        var z1 = z + h;
        IEnumerable<(double X, double Y)> Corner(double cx, double cy, double a0, double a1) =>
            Enumerable.Range(0, n + 1).Select(i => a0 + ((a1 - a0) * i / n)).Select(a => (cx + (r * Math.Cos(a)), cy + (r * Math.Sin(a))));

        var ring = Corner(x + w - r, y + r, -half, 0).Concat(Corner(x + w - r, y + d - r, 0, half))
            .Concat(Corner(x + r, y + d - r, half, 2 * half)).Concat(Corner(x + r, y + r, 2 * half, 3 * half)).ToArray();
        var visible = Corner(x + w - r, y + r, -quarter, 0).Concat(Corner(x + w - r, y + d - r, 0, half))
            .Concat(Corner(x + r, y + d - r, half, 3 * quarter)).ToArray();
        var wall = visible.Select(p => P(new(p.X, p.Y, z))).Concat(visible.Reverse().Select(p => P(new(p.X, p.Y, z1))));
        return [Polygon([.. wall]), Polygon([.. ring.Select(p => P(new(p.X, p.Y, z1)))])];
    }

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], true);
        for (var i = 1; i < points.Length; i++) g.LineTo(points[i]);
        g.EndFigure(true);
        return geometry;
    }

    private static StreamGeometry Polyline(Point[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], false);
        for (var i = 1; i < points.Length; i++) g.LineTo(points[i]);
        g.EndFigure(false);
        return geometry;
    }

    /// <summary>The part of a polyline between two fractions of its length.</summary>
    private static Point[] Slice(Point[] points, double from, double to)
    {
        var lengths = new double[points.Length];
        for (var i = 1; i < points.Length; i++) lengths[i] = lengths[i - 1] + Point.Distance(points[i - 1], points[i]);
        double a = from * lengths[^1], b = to * lengths[^1];
        if (b <= a)
        {
            return [];
        }

        Point At(double s)
        {
            var i = 1;
            while (i < points.Length - 1 && lengths[i] < s) i++;
            var span = lengths[i] - lengths[i - 1];
            var t = span <= 0 ? 0 : (s - lengths[i - 1]) / span;
            return points[i - 1] + ((points[i] - points[i - 1]) * t);
        }

        var result = new List<Point> { At(a) };
        for (var i = 1; i < points.Length - 1; i++)
        {
            if (lengths[i] > a && lengths[i] < b)
            {
                result.Add(points[i]);
            }
        }

        result.Add(At(b));
        return [.. result];
    }

    private enum RowCursor
    {
        None,
        Active,
        Inactive,
    }

    private readonly record struct Point3(double X, double Y, double Z);

    /// <summary>A file's flight: a quadratic Bézier from the left panel over the hub into the right one.</summary>
    private readonly record struct Arc(Point3 Start, Point3 Control, Point3 End)
    {
        public Point3 At(double t)
        {
            var u = 1 - t;
            double Mix(double a, double c, double e) => (u * u * a) + (2 * u * t * c) + (t * t * e);
            return new(Mix(Start.X, Control.X, End.X), Mix(Start.Y, Control.Y, End.Y), Mix(Start.Z, Control.Z, End.Z));
        }
    }

    private sealed record Flight(int Arc, double Start, int Item);

    /// <summary>The figure's colour tokens, derived from the skin (light skins: no glow, as in the original).</summary>
    private sealed class ScenePalette
    {
        public ScenePalette(SkinPalette p)
        {
            var bg = p.WindowBackground;
            var fg = p.WindowForeground ?? p.FileForeground;
            var accent = p.Accent;
            var light = Luma(bg) > 0.5;
            Glow = !light;
            Bg = Brush(bg);
            Panel = Brush(p.PanelBackground);
            Line = Brush(bg.Mix(fg, 0.22));
            Detail = Brush(bg.Mix(fg, 0.13));
            Body = Brush(bg.Mix(fg, 0.05));
            Deck = Brush(bg.Mix(fg, 0.08));
            Ink = Brush(p.MutedForeground ?? bg.Mix(fg, 0.5));
            InkHi = Brush(fg);
            Glass = Brush(bg.Mix(fg, light ? 0.06 : 0.015));
            GlassOn = Brush(bg.Mix(accent, light ? 0.12 : 0.09));
            Recess = Brush(bg.Mix(fg, light ? 0.04 : 0.01));
            Accent = Brush(accent);
            AccentHi = Brush(light ? accent.Mix(new RgbColor(0, 0, 0), 0.35) : accent.Mix(new RgbColor(255, 255, 255), 0.45));
            AccentDimColor = SkinBrushes.ToColor(accent with { A = 26 });
            AccentDim = new ImmutableSolidColorBrush(AccentDimColor);
            AccentGlow = new ImmutableSolidColorBrush(SkinBrushes.ToColor(accent with { A = 70 }));
        }

        public bool Glow { get; }

        public IBrush Bg { get; }

        public IBrush Panel { get; }

        public IBrush Line { get; }

        public IBrush Detail { get; }

        public IBrush Body { get; }

        public IBrush Deck { get; }

        public IBrush Ink { get; }

        public IBrush InkHi { get; }

        public IBrush Glass { get; }

        public IBrush GlassOn { get; }

        public IBrush Recess { get; }

        public IBrush Accent { get; }

        public IBrush AccentHi { get; }

        public IBrush AccentDim { get; }

        public Color AccentDimColor { get; }

        public IBrush AccentGlow { get; }

        private static double Luma(RgbColor c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255;

        private static ImmutableSolidColorBrush Brush(RgbColor c) => new(SkinBrushes.ToColor(c));
    }
}
