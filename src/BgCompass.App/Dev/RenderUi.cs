using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.Core;

namespace BgCompass.App.Dev;

/// <summary>
/// <c>--render-ui &lt;outDir&gt;</c>: draws every panel and every main-window tab to PNG with the headless Skia renderer,
/// at the scale of a 1080p and of a 1440p game window, from <see cref="DemoScenario"/>, then returns. The picture sits on a
/// flat backdrop so the translucent panel surface can be judged; the real overlay is transparent.
/// Card art is not downloaded here (offline), so art tiles stay empty in these pictures.
/// </summary>
public static class RenderUi
{
    public static int Run(string outDir, AppBuilder builder)
    {
        try
        {
            builder.SetupWithoutStarting();
            Directory.CreateDirectory(outDir);
            var tmp = Path.Combine(Path.GetTempPath(), "bgc-render-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var n = new Renderer(outDir, tmp).RenderAll();
                Console.WriteLine($"Нарисовано файлов: {n}, папка {outDir}");
                return 0;
            }
            finally { try { Directory.Delete(tmp, true); } catch { /* temp only */ } }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    sealed class Renderer
    {
        static readonly (string Name, double Scale)[] Scales = [("1080", 1.0), ("1440", 1440 / 1080.0)];
        readonly string _out;
        readonly AppHost _host;
        readonly DemoScenario _demo = new();
        int _count;

        public Renderer(string outDir, string tmp)
        {
            _out = outDir;
            var paths = new DataPaths(tmp);
            _host = new AppHost(new HostOptions(paths, new SettingsStore(paths.Settings), a => a(), Offline: true));
            ArtCache.Instance = _host.Art;
            _demo.SeedHistory(_host.History);
        }

        public int RenderAll()
        {
            var recruit = _demo.Snapshot(MatchPhase.Recruit);
            var scenarios = new (string Name, UiState State)[]
            {
                ("recruit", _demo.State(recruit, _host.History)),
                ("heropick", _demo.State(_demo.Snapshot(MatchPhase.HeroPick), _host.History)),
                ("combat", _demo.State(_demo.Snapshot(MatchPhase.Combat), _host.History)),
                ("combat-running", _demo.State(_demo.Snapshot(MatchPhase.Combat), _host.History, new SimView(SimStatus.Running, null))),
                ("combat-unavailable", _demo.State(_demo.Snapshot(MatchPhase.Combat), _host.History,
                    new SimView(SimStatus.Done, SimResult.Unavailable("Не видно всех существ соперника: бой не рассчитать.")))),
                ("gameover", _demo.State(_demo.Snapshot(MatchPhase.GameOver), _host.History)),
                ("loading", _demo.State(recruit, _host.History, withCatalog: false)),
                ("nodata", _demo.State(recruit with { Board = [], Shop = [], Players = [], Encounters = [] }, _host.History)),
                ("unknown", _demo.State(recruit with { Shop = [.. recruit.Shop.Take(2), recruit.Shop[2] with { CardId = "BG99_Unknown", Name = "Неведомый зверь", Attack = 3, Health = 3, Tribes = [], TechLevel = 3 }] }, _host.History)),
                ("nolog", _demo.State(MatchSnapshot.Empty, _host.History, log: DemoScenario.LogMissing)),
                ("logoff", _demo.State(MatchSnapshot.Empty, _host.History, log: DemoScenario.LogDisabled)),
            };
            foreach (var (name, state) in scenarios)
                foreach (var (sn, scale) in Scales)
                    Panels(name, state, sn, scale);

            foreach (var (name, state) in scenarios)
                if (name is "recruit" or "heropick" or "combat" or "combat-running") Field(name, name == "recruit" ? _demo.WithPreview(state) : state);
            Guides(_demo.State(recruit, _host.History));

            var ui = _demo.State(recruit, _host.History);
            Main(ui, "recruit");
            Main(_demo.State(MatchSnapshot.Empty, _host.History, log: DemoScenario.LogDisabled,
                cards: DemoScenario.Ready(_demo.Catalog, "Данные карт на 3 версии старше игры: часть новых карт может отсутствовать.")), "logoff");
            return _count;
        }

        void Panels(string scenario, UiState state, string scaleName, double scale)
        {
            if (!PanelRules.Wanted(state, _host.Settings.Value)) return;
            var vm = new CompassVm(_host, () => { });
            vm.Apply(state);
            var game = new PxRect(0, 0, 1920 * scale, 1080 * scale);
            string[] tabs = [PanelRules.MainPage(state).ToString().ToLowerInvariant(), "opponents", "tribes"];
            for (var t = 0; t < (vm.ShowTabs ? tabs.Length : 1); t++)
            {
                vm.SelectTab(t);
                var w = new OverlayWindow(vm) { Background = Backdrop };
                w.SetScale(PanelLayout.AutoScale(game));
                Save(w, $"{scenario}_{tabs[t]}_{scaleName}.png");
                w.Close();
            }
        }

        /// <summary>The layer over a plain table colour, at two window shapes, with the first mark hovered.</summary>
        void Field(string scenario, UiState state)
        {
            foreach (var (shape, game) in new (string, PxRect)[] { ("16x9", new(0, 0, 1920, 1080)), ("21x9", new(0, 0, 2560, 1080)) })
            {
                var vm = new FieldVm(_host.Settings);
                vm.SetWidth(GameField.Width(game));
                vm.Apply(state);
                var hover = vm.Marks.FirstOrDefault(k => k.Kind is FieldMarkKind.Triple or FieldMarkKind.Buy or FieldMarkKind.Hero or FieldMarkKind.Odds);
                if (hover is not null) vm.Hover(hover.Hit.X + 2, hover.Hit.Y + 2);
                var w = new FieldWindow(vm) { Background = FieldBackdrop };
                w.Follow(game);
                Save(w, $"field_{scenario}_{shape}.png");
                if (vm.Marks.FirstOrDefault(k => k.Kind == FieldMarkKind.Seat && k.Label.Length > 0) is { } seat)
                {
                    vm.Hover(seat.Hit.X + 2, seat.Hit.Y + 2);
                    Save(w, $"field_{scenario}_seat_{shape}.png");
                }
                w.Close();
            }
        }

        void Guides(UiState state)
        {
            _host.Settings.Value.FieldGuides = true;
            var game = new PxRect(0, 0, 1920, 1080);
            var vm = new FieldVm(_host.Settings);
            vm.SetWidth(GameField.Width(game));
            vm.Apply(state);
            var w = new FieldWindow(vm) { Background = FieldBackdrop };
            w.Follow(game);
            Save(w, "field_guides_16x9.png");
            w.Close();
            _host.Settings.Value.FieldGuides = false;
        }

        void Main(UiState state, string name)
        {
            var catalog = new CatalogVm();
            var stats = new StatsVm(_host);
            var settings = new SettingsVm(_host, new NoHotkeys(), () => Task.FromResult<string?>(null), () => { });
            catalog.Apply(state);
            settings.Apply(state);
            var vm = new MainVm(catalog, stats, settings, false);
            string[] tabs = ["catalog", "stats", "settings"];
            foreach (var (sn, scale) in Scales)
                for (var t = 0; t < tabs.Length; t++)
                {
                    var w = new MainWindow { DataContext = vm, Width = 1040 * scale, Height = 720 * scale };
                    var content = w.Content;
                    w.Content = null;
                    w.Content = new LayoutTransformControl { Child = (Control)content!, LayoutTransform = new ScaleTransform(scale, scale) };
                    vm.SelectedTab = t;
                    Save(w, $"main_{tabs[t]}_{name}_{sn}.png");
                    w.Close();
                }
        }

        static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x4A, 0x38, 0x28));

        /// <summary>A local game screenshot from BGC_FIELD_BACKDROP, to judge the layer against the real table.</summary>
        static IBrush FieldBackdrop => Environment.GetEnvironmentVariable("BGC_FIELD_BACKDROP") is { Length: > 0 } path
            ? new ImageBrush(new Avalonia.Media.Imaging.Bitmap(path)) { Stretch = Stretch.UniformToFill }
            : Backdrop;

        void Save(Window w, string file)
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("Кадр не получен: " + file);
            using (var fs = File.Create(Path.Combine(_out, file))) frame.Save(fs, new PngBitmapEncoderOptions());
            _count++;
        }
    }
}
