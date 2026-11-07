using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;

namespace BgCompass.App;

/// <summary>
/// Wires the running app together: runtime, panels, overlay windows, main window and tray icon.
/// Owns the shutdown order so that a clean exit stops the tailer, the simulator and the overlay before the process ends.
/// </summary>
public sealed class Shell : IDisposable
{
    readonly Application _app;
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly CliOptions _cli;
    AppHost? _host;
    OverlayManager? _overlay;
    MainWindow? _main;
    MainVm? _mainVm;
    TrayIcon? _tray;
    NativeMenuItem? _toggleItem;
    NativeMenuItem? _updateItem;
    DispatcherTimer? _smoke;
    bool _disposed;

    public Shell(Application app, IClassicDesktopStyleApplicationLifetime desktop, CliOptions cli)
    {
        _app = app; _desktop = desktop; _cli = cli;
    }

    public void Start()
    {
        var replay = _cli.Replay is not null;
        var paths = DataPaths.Resolve(replay);
        AppLog.Init(paths.AppLog);
        InstallExceptionLogging();
        var settings = new SettingsStore(paths.Settings);
        _host = new AppHost(new HostOptions(paths, settings, a => Dispatcher.UIThread.Post(a),
            _cli.Replay, _cli.Speed, _cli.GameState, Offline: replay));
        ArtCache.Instance = _host.Art;

        _main = new MainWindow();
        var compass = new CompassVm(_host, ShowMain);
        var hotkeys = Platform.CreateHotkeys();
        var catalog = new CatalogVm();
        var stats = new StatsVm(_host);
        _overlay = new OverlayManager(_host, compass, new FieldVm(settings), Platform.CreateProbe(PrimaryScreen), hotkeys, PrimaryScreen);
        var settingsVm = new SettingsVm(_host, hotkeys, PickFolder, _overlay.ResetPositions);
        _mainVm = new MainVm(catalog, stats, settingsVm, replay);
        _main.DataContext = _mainVm;

        _host.StateChanged += s =>
        {
            Guard("Окно", () => { catalog.Apply(s); settingsVm.Apply(s); });
            Guard("Панель", () => compass.Apply(s));
            Guard("Оверлей", () => _overlay.Apply(s));
            Guard("Трей", () => ShowUpdateInTray(s.UpdateReadyVersion));
        };
        settings.Saved += () => Dispatcher.UIThread.Post(UpdateTrayText);

        _host.Start();
        _overlay.Start();
        BuildTray();

        if (!replay && _cli.RenderUi is null && _cli.SmokeExit is null)
        {
            _host.Updater = Updater.TryCreate(_host.SetUpdateReady, () => Dispatcher.UIThread.Post(() => _desktop.Shutdown(0)));
            _host.Updater?.Start();
        }

        if (!settings.Value.FirstRunDone || replay == false && _host.State.Log.PowerLogPath is null)
        {
            _mainVm.SelectedTab = 2;
            ShowMain();
            settings.Value.FirstRunDone = true;
            settings.Save();
        }

        if (_cli.SmokeExit is { } secs)
        {
            _smoke = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
            _smoke.Tick += (_, _) => { _smoke.Stop(); AppLog.Info("smoke-exit"); _desktop.Shutdown(0); };
            _smoke.Start();
        }
    }

    PxRect PrimaryScreen()
    {
        var b = (_main?.Screens.Primary ?? _main?.Screens.All.FirstOrDefault())?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        return new PxRect(b.X, b.Y, b.Width, b.Height);
    }

    async Task<string?> PickFolder()
    {
        if (_main is null) return null;
        ShowMain();
        var r = await _main.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Папка с логами Hearthstone", AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    void ShowMain()
    {
        if (_main is null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    void BuildTray()
    {
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowMain();
        _toggleItem = new NativeMenuItem();
        _toggleItem.Click += (_, _) =>
        {
            var st = _host!.Settings;
            st.Value.PanelsVisible = !st.Value.PanelsVisible;
            st.Save();
        };
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => _desktop.Shutdown(0);
        var menu = new NativeMenu();
        menu.Add(open); menu.Add(_toggleItem); menu.Add(new NativeMenuItemSeparator()); menu.Add(exit);

        _tray = new TrayIcon { Icon = TrayIconFactory.Create(_app), ToolTipText = "BG Compass", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(_app, new TrayIcons { _tray });
        UpdateTrayText();
    }

    void ShowUpdateInTray(string? version)
    {
        if (version is null || _updateItem is not null || _tray?.Menu is not { } menu) return;
        _updateItem = new NativeMenuItem($"Обновить до {version}");
        _updateItem.Click += (_, _) => _host?.Updater?.Restart();
        menu.Items.Insert(0, _updateItem);
    }

    void UpdateTrayText()
    {
        if (_toggleItem is null || _host is null) return;
        _toggleItem.Header = _host.Settings.Value.PanelsVisible ? "Скрыть панели" : "Показать панели";
    }

    static void Guard(string what, Action a)
    {
        try { a(); } catch (Exception ex) { AppLog.Error(what + ": ошибка обновления", ex); }
    }

    static void InstallExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Необработанное исключение", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Необработанное исключение задачи", e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) => { AppLog.Error("Необработанное исключение интерфейса", e.Exception); e.Handled = true; };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _smoke?.Stop();
        Guard("Трей", () => { if (_tray is not null) { _tray.IsVisible = false; _tray.Dispose(); } });
        Guard("Оверлей", () => _overlay?.Dispose());
        Guard("Рантайм", () => _host?.Dispose());
        Guard("Окно", () => _main?.Quit());
    }
}

/// <summary>The tray icon is drawn at runtime: a gold square with a compass needle. No image file ships with the app.</summary>
public static class TrayIconFactory
{
    public static WindowIcon Create(Application app)
    {
        IBrush Brush(string key) => app.TryFindResource(key, app.ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;
        var gold = Brush("AccentBrush");
        var dark = Brush("SurfaceSolidBrush");
        var canvas = new Canvas { Width = 32, Height = 32, Background = gold };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon
        {
            Fill = dark,
            Points = [new Point(16, 4), new Point(22, 16), new Point(16, 28), new Point(10, 16)],
        });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = gold, [Canvas.LeftProperty] = 13.0, [Canvas.TopProperty] = 13.0 });
        canvas.Measure(new Size(32, 32));
        canvas.Arrange(new Rect(0, 0, 32, 32));
        var bmp = new RenderTargetBitmap(new PixelSize(32, 32));
        bmp.Render(canvas);
        return new WindowIcon(bmp);
    }
}
