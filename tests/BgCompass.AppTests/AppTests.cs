using Avalonia;
using Avalonia.Headless;
using BgCompass.App;
using BgCompass.App.Dev;
using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.Sim;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.AppTests;

public sealed class TempHost : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bgc-apptest-" + Guid.NewGuid().ToString("N")[..8]);
    public AppHost Host { get; }

    public TempHost(string? replay = null, double speed = 1)
    {
        var paths = new DataPaths(Dir);
        Host = new AppHost(new HostOptions(paths, new SettingsStore(paths.Settings), a => a(), replay, speed, Offline: true));
    }

    public void Dispose()
    {
        Host.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { /* temp only */ }
    }
}

public class PanelPipelineTests
{
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic_game.power.log");

    static UiState StateFor(MatchSnapshot m, CardCatalog cat, AdvicePipeline pipeline)
    {
        var (hero, board) = pipeline.Advise(m, cat, new HashSet<string>(), 0);
        var sim = m.Combat is { Complete: true } c
            ? new SimView(SimStatus.Done, CombatSimulator.Run(c, cat, 300, seed: 1))
            : SimView.None;
        return new UiState(m, hero, board, sim, new HashSet<string>(), DemoScenario.Ready(cat), DemoScenario.LogOk, cat, false, 1);
    }

    [Fact]
    public void Synthetic_log_fills_the_panel_of_every_phase()
    {
        using var th = new TempHost();
        var cat = CardCatalog.LoadEmbedded();
        var panels = new CompassVm(th.Host, () => { });
        var pipeline = new AdvicePipeline(() => th.Host.History.HeroStatsByHero(cat));
        var tracker = new MatchTracker(new MatchTrackerOptions("s", new DateOnly(2026, 11, 6), PowerStream.PowerTaskList));
        var seen = new Dictionary<MatchPhase, string>();
        foreach (var line in File.ReadAllLines(Fixture))
        {
            tracker.Feed(line);
            if (!line.StartsWith("#CHECK ", StringComparison.Ordinal)) continue;
            var snap = tracker.Snapshot;
            var state = StateFor(snap, cat, pipeline);
            panels.Apply(state);   // must not throw, whatever the (fake) cards are
            switch (snap.Phase)
            {
                case MatchPhase.HeroPick when !seen.ContainsKey(snap.Phase):
                    Assert.NotEmpty(panels.Hero.Offers);
                    Assert.False(panels.Hero.HasEmpty);
                    seen[snap.Phase] = line;
                    break;
                case MatchPhase.Recruit when !seen.ContainsKey(snap.Phase):
                    Assert.Contains("Раунд", panels.TopBar.Round);
                    Assert.True(panels.Build.HasEmpty ^ panels.Build.HasData);
                    Assert.True(panels.Opponent.HasEmpty ^ panels.Opponent.HasData);
                    seen[snap.Phase] = line;
                    break;
                case MatchPhase.Combat when !seen.ContainsKey(snap.Phase):
                    Assert.NotEmpty(panels.Sim.Headline);
                    seen[snap.Phase] = line;
                    break;
                case MatchPhase.GameOver when !seen.ContainsKey(snap.Phase):
                    Assert.NotEmpty(panels.GameOver.Place);
                    seen[snap.Phase] = line;
                    break;
            }
        }
        Assert.True(seen.Count == 4, "phases reached: " + string.Join(", ", seen.Keys));
    }

    [Fact]
    public void Replay_reaches_the_ui_throttled_and_ends_on_the_latest_state()
    {
        using var th = new TempHost(Fixture, speed: 5000);
        var delivered = new List<UiState>();
        th.Host.StateChanged += s => { lock (delivered) delivered.Add(s); };
        th.Host.Start();

        // Wait until the replay is over and the publisher has caught up with the newest state.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int last = -1;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            int n; UiState? tail;
            lock (delivered) { n = delivered.Count; tail = delivered.LastOrDefault(); }
            if (n > 0 && n == last && tail!.Revision == th.Host.State.Revision && th.Host.History.History().Count > 0) break;
            last = n;
        }
        UiState final;
        lock (delivered) final = delivered.Last();
        Assert.Equal(th.Host.State.Revision, final.Revision);
        Assert.True(th.Host.History.History().Count >= 1);
        Assert.True(delivered.Count <= th.Host.State.Revision, "the UI must never receive more states than were produced");
        Assert.True(delivered.Select(s => s.Revision).SequenceEqual(delivered.Select(s => s.Revision).OrderBy(r => r)), "states arrive in order");
    }
}

public class TribeChipTests
{
    [Fact]
    public void Chip_click_marks_the_tribe_absent_and_clicking_again_clears_it()
    {
        using var th = new TempHost();
        var panels = new CompassVm(th.Host, () => { });
        var chip = panels.TopBar.Chips.First();
        chip.Toggle.Execute(null);
        Assert.Contains(chip.Tribe, th.Host.State.AbsentTribes);
        panels.Apply(th.Host.State);
        Assert.True(chip.IsAbsent);
        chip.Toggle.Execute(null);
        panels.Apply(th.Host.State);
        Assert.False(chip.IsAbsent);
        Assert.DoesNotContain(chip.Tribe, th.Host.State.AbsentTribes);
    }

    [Fact]
    public void Marking_a_tribe_absent_changes_the_build_advice()
    {
        var demo = new DemoScenario();
        var m = demo.Snapshot(MatchPhase.Recruit) with { ConfirmedTribes = new HashSet<string>() };
        static string Text(BoardAdvice a) => string.Join("|",
            a.Directions.Select(d => d.Name + d.Fit.ToString("F3")).Concat(a.Gaps.Select(l => l.Text)).Concat(a.Synergies.Select(l => l.Text)));
        var baseline = Text(BoardAdvisor.Advise(m, demo.Catalog, new HashSet<string>()));
        var changed = TribeNames.Real
            .Any(t => Text(BoardAdvisor.Advise(m, demo.Catalog, new HashSet<string> { t })) != baseline);
        Assert.True(changed, "excluding a tribe the board leans on must change the advice");
    }

    [Fact]
    public void A_proven_tribe_cannot_be_marked_absent()
    {
        using var th = new TempHost();
        Assert.False(th.Host.Marks.Toggle("BEAST", new HashSet<string> { "BEAST" }));
    }
}

public class PositionTests
{
    [Theory]
    [InlineData(1920, 1080, 2560, 1440)]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1920, 1080, 1024, 768)]
    public void A_dragged_position_keeps_its_place_across_resolutions(double w1, double h1, double w2, double h2)
    {
        var g1 = new PxRect(0, 0, w1, h1);
        var g2 = new PxRect(130, 40, w2, h2);   // the game window may also sit elsewhere on the desktop
        const double pw = 300, ph = 200;
        var (fx, fy) = PanelLayout.ToFraction(480, 270, g1);
        var (x, y) = PanelLayout.Resolve(new PanelState { X = fx, Y = fy }, g2, pw, ph);
        Assert.Equal(g2.X + fx * g2.W, x, 3);
        Assert.Equal(g2.Y + fy * g2.H, y, 3);
    }

    [Fact]
    public void A_saved_position_is_clamped_into_the_game_window()
    {
        var g = new PxRect(0, 0, 1000, 600);
        var (x, y) = PanelLayout.Resolve(new PanelState { X = 0.99, Y = 0.99 }, g, 300, 200);
        Assert.Equal(700, x, 3);
        Assert.Equal(400, y, 3);
    }

    [Fact]
    public void Garbage_saved_positions_fall_back_to_the_default()
    {
        var g = new PxRect(0, 0, 1920, 1080);
        var def = PanelLayout.Resolve(new PanelState(), g, 340, 400);
        var bad = PanelLayout.Resolve(new PanelState { X = double.NaN, Y = double.PositiveInfinity }, g, 340, 400);
        Assert.Equal(def, bad);
    }

    [Fact]
    public void Scale_follows_the_game_height()
    {
        Assert.Equal(1.0, PanelLayout.AutoScale(new PxRect(0, 0, 1920, 1080)), 3);
        Assert.Equal(2.0, PanelLayout.AutoScale(new PxRect(0, 0, 3840, 2160)), 3);
    }
}

public class UnknownCardTests
{
    [Fact]
    public void Panels_survive_cards_the_catalog_does_not_know()
    {
        using var th = new TempHost();
        var demo = new DemoScenario();
        var recruit = demo.Snapshot(MatchPhase.Recruit);
        var ghost = recruit.Shop[0] with { CardId = "BG99_Unknown", Name = "Неведомый зверь", Tribes = [], TechLevel = 3 };
        var m = recruit with { Shop = [ghost, .. recruit.Shop.Skip(1)], Board = [ghost, .. recruit.Board.Skip(1)] };
        var panels = new CompassVm(th.Host, () => { });
        panels.Apply(demo.State(m));

        var hero = demo.Snapshot(MatchPhase.HeroPick);
        hero = hero with { HeroOffers = [new HeroOption(1, "TB_NoSuchHero", "Неизвестный герой", 1), .. hero.HeroOffers.Skip(1)] };
        panels.Apply(demo.State(hero));
        Assert.Contains(panels.Hero.Offers, o => o.Name == "Неизвестный герой");
        Assert.Contains("нет в данных", panels.Hero.Offers.First().Tip);
    }
}

public class HeadlessRenderTests
{
    [Fact]
    public void Every_panel_and_tab_render_to_png()
    {
        // Avalonia binds its UI thread to the thread that sets it up, so the whole render runs on one dedicated thread.
        var dir = Path.Combine(Path.GetTempPath(), "bgc-apptest-png-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var code = -1;
            var t = new Thread(() =>
            {
                code = RenderUi.Run(dir, AppBuilder.Configure<BgCompass.App.App>().UseSkia()
                    .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }));
            });
            t.Start();
            t.Join();
            Assert.Equal(0, code);
            var files = Directory.GetFiles(dir, "*.png");
            Assert.True(files.Length >= 50, $"rendered {files.Length}");
            Assert.All(files, f => Assert.True(new FileInfo(f).Length > 500, f));
            Assert.Contains(files, f => f.EndsWith("unknown_build_1080.png"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
