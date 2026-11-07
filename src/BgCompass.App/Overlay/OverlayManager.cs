using Avalonia.Controls;
using Avalonia.Threading;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;

namespace BgCompass.App.Overlay;

/// <summary>
/// Creates the panel window on demand, keeps it over the game rectangle and shows it while there is a game or a problem to
/// report. Polls the game window about twice a second (cheap: three Win32 calls) and does nothing when nothing moved.
/// </summary>
public sealed class OverlayManager : IDisposable
{
    readonly AppHost _host;
    readonly CompassVm _vm;
    readonly FieldVm _fieldVm;
    readonly IGameWindowProbe _probe;
    readonly IHotkeys _hotkeys;
    readonly Func<PxRect> _screen;
    OverlayWindow? _window;
    FieldWindow? _field;
    readonly DispatcherTimer _poll, _cursor;
    GameWindowInfo _game;
    UiState _state = UiState.Initial;
    bool _wasShown;

    public OverlayManager(AppHost host, CompassVm vm, FieldVm field, IGameWindowProbe probe, IHotkeys hotkeys, Func<PxRect> screen)
    {
        _host = host; _vm = vm; _fieldVm = field; _probe = probe; _hotkeys = hotkeys; _screen = screen;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _poll.Tick += (_, _) => Poll();
        // Only runs while the layer is on screen. Reading the cursor is one cheap call.
        _cursor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _cursor.Tick += (_, _) => ReadCursor();
        vm.GameOver.Changed += Reconcile;
        host.Settings.Saved += OnSettingsSaved;
    }

    public IReadOnlyList<HotkeyStatus> HotkeyStatus => _hotkeys.Status;
    public OverlayWindow? Window => _window;
    public GameWindowInfo Game => _game;

    public void Start()
    {
        _hotkeys.Pressed += a => Dispatcher.UIThread.Post(() => OnHotkey(a));
        _hotkeys.Start();
        Poll();
        _poll.Start();
    }

    public void Apply(UiState s)
    {
        _state = s;
        Reconcile();
    }

    void OnHotkey(HotkeyAction a)
    {
        var st = _host.Settings;
        if (a == HotkeyAction.TogglePanels) st.Value.PanelsVisible = !st.Value.PanelsVisible;
        else st.Value.ClickThrough = !st.Value.ClickThrough;
        st.Save();
    }

    /// <summary>Back to the default place and shown again.</summary>
    public void ResetPositions()
    {
        var p = _host.Settings.Value.Overlay;
        p.X = p.Y = null;
        _host.Settings.Value.FieldOffsetX = _host.Settings.Value.FieldOffsetY = 0;
        _host.Settings.Value.PanelsVisible = true;
        _host.Settings.Save();
    }

    void OnSettingsSaved() => Dispatcher.UIThread.Post(Reconcile);

    GameWindowInfo ReadGame()
    {
        var g = _probe.Poll();
        // Replays and non-Windows systems have no game window: the primary screen stands in for it.
        if (!g.Found && _host.IsReplay) g = new GameWindowInfo(true, _screen(), true);
        return g;
    }

    void Poll()
    {
        try
        {
            var g = ReadGame();
            if (g == _game) return;
            _game = g;
            Reconcile();
        }
        catch (Exception ex) { AppLog.Error("Опрос окна игры", ex); }
    }

    void Reconcile()
    {
        ReconcileField();
        try
        {
            var settings = _host.Settings.Value;
            var show = _game.Found && _game.ShouldShow && PanelRules.Wanted(_state, settings)
                && !(_state.Match.Phase == Core.MatchPhase.GameOver && _vm.GameOver.Dismissed);
            if (!show)
            {
                if (_window is { IsVisible: true }) _window.Hide();
                _wasShown = false;
                return;
            }
            var w = Ensure();
            w.SetClickThrough(settings.ClickThrough);
            w.SetScale(Scale());
            if (!w.IsVisible) w.Show();
            Place(w);
            if (!_wasShown) Platform.RaiseTopmost(w);
            _wasShown = true;
        }
        catch (Exception ex) { AppLog.Error("Панель не показалась", ex); }
    }

    void ReconcileField()
    {
        try
        {
            _fieldVm.SetWidth(_game.Found ? GameField.Width(_game.Rect) : 0);
            _fieldVm.Apply(_state);
            if (!(_game.Found && _game.ShouldShow && FieldRules.Visible(_state, _host.Settings.Value)))
            {
                _cursor.Stop();
                _fieldVm.Hover(null, null);
                if (_field is { IsVisible: true }) _field.Hide();
                return;
            }
            _field ??= new FieldWindow(_fieldVm);
            var appearing = !_field.IsVisible;
            if (appearing) _field.Show();
            _field.Follow(_game.Rect);
            if (appearing)
            {
                Platform.RaiseTopmost(_field);
                if (_window is { IsVisible: true }) Platform.RaiseTopmost(_window);
            }
            _cursor.Start();
        }
        catch (Exception ex) { AppLog.Error("Метки на поле не показались", ex); }
    }

    void ReadCursor()
    {
        if (Platform.CursorPosition() is not { } c) return;
        var overPanel = _window is { IsVisible: true } w && new PxRect(w.Position.X, w.Position.Y, w.PhysicalSize.W, w.PhysicalSize.H).Contains(c.X, c.Y);
        if (overPanel || !_game.Rect.Contains(c.X, c.Y)) { _fieldVm.Hover(null, null); return; }
        var (x, y) = GameField.ToField(_game.Rect, c.X, c.Y);
        _fieldVm.Hover(x, y);
    }

    double Scale()
    {
        var rect = _game.Rect;
        var s = PanelLayout.AutoScale(rect) * _host.Settings.Value.UserScale;
        // The panel never grows taller or wider than the game window, whatever the user scale.
        if (PanelLayout.DesignMaxHeight * s > rect.H * 0.96) s = rect.H * 0.96 / PanelLayout.DesignMaxHeight;
        if (PanelLayout.DesignWidth * s > rect.W) s = rect.W / PanelLayout.DesignWidth;
        return Math.Max(s, 0.2);
    }

    OverlayWindow Ensure()
    {
        if (_window is not null) return _window;
        var w = new OverlayWindow(_vm);
        w.Dragged += OnDragged;
        w.SizeChanged += (_, _) => { if (w.IsVisible && !w.IsDragging) Place(w); };
        return _window = w;
    }

    void Place(OverlayWindow w)
    {
        if (w.IsDragging || !_game.Found) return;
        var (pw, ph) = w.PhysicalSize;
        if (pw <= 0 || ph <= 0) return;
        var (x, y) = PanelLayout.Resolve(_host.Settings.Value.Overlay, _game.Rect, pw, ph);
        var target = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        if (w.Position != target) w.Position = target;
    }

    void OnDragged(int x, int y)
    {
        var (pw, ph) = _window!.PhysicalSize;
        var (cx, cy) = PanelLayout.Clamp(x, y, _game.Rect, pw, ph);
        var (fx, fy) = PanelLayout.ToFraction(cx, cy, _game.Rect);
        var p = _host.Settings.Value.Overlay;
        p.X = fx; p.Y = fy;
        _host.Settings.Save();
    }

    public void Dispose()
    {
        _poll.Stop();
        _cursor.Stop();
        _host.Settings.Saved -= OnSettingsSaved;
        _hotkeys.Dispose();
        try { _window?.Close(); } catch { }
        _window = null;
        try { _field?.Close(); } catch { }
        _field = null;
    }
}
