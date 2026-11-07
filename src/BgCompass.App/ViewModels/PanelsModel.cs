using System.Windows.Input;
using BgCompass.App.Runtime;
using BgCompass.Core;

namespace BgCompass.App.ViewModels;

public enum CompassPage { Status, Hero, Build, Sim, GameOver }

/// <summary>When the panel shows and what its first page is. Pure so it can be tested without any window.</summary>
public static class PanelRules
{
    public static bool Wanted(UiState s, AppSettings settings) =>
        settings.PanelsVisible && (s.Match.Phase != MatchPhase.Idle || EmptyStates.NoLog(s) is not null);

    public static CompassPage MainPage(UiState s) => s.Match.Phase switch
    {
        MatchPhase.HeroPick => CompassPage.Hero,
        MatchPhase.Recruit => CompassPage.Build,
        MatchPhase.Combat => CompassPage.Sim,
        MatchPhase.GameOver => CompassPage.GameOver,
        _ => CompassPage.Status,
    };

    public static string MainTitle(CompassPage p) => p switch
    {
        CompassPage.Hero => "Герои",
        CompassPage.Build => "Билд",
        CompassPage.Sim => "Бой",
        _ => "",
    };
}

/// <summary>
/// The one overlay panel. Its first tab follows the phase of the game; a tab the player opened by hand
/// holds only until the phase changes, so the panel is on the right page at every glance.
/// </summary>
public sealed class CompassVm : ViewModelBase
{
    readonly SettingsStore _settings;
    readonly AppHost _host;
    CompassPage _page = CompassPage.Status;
    int _tab;
    object? _body;
    bool _tabsWanted;
    string? _updateVersion;

    public CompassVm(AppHost host, Action openMain)
    {
        _host = host;
        _settings = host.Settings;
        TopBar = new TopBarVm(host, openMain);
        Hero = new HeroVm();
        Build = new BuildVm();
        Opponent = new OpponentVm();
        Sim = new SimVm();
        GameOver = new GameOverVm();
        Status = new StatusVm(TopBar.OpenMain);
        Tabs = [new TabVm("", () => SelectTab(0)), new TabVm("Соперники", () => SelectTab(1)), new TabVm("Племена", () => SelectTab(2))];
        ToggleCollapse = new RelayCommand(() => Collapsed = !Collapsed);
        Update = new RelayCommand(() => _host.Updater?.Restart());
        ShowPage();
    }

    public TopBarVm TopBar { get; }
    public HeroVm Hero { get; }
    public BuildVm Build { get; }
    public OpponentVm Opponent { get; }
    public SimVm Sim { get; }
    public GameOverVm GameOver { get; }
    public StatusVm Status { get; }
    public IReadOnlyList<TabVm> Tabs { get; }
    public ICommand ToggleCollapse { get; }
    public ICommand Update { get; }

    public object? Body { get => _body; private set => Set(ref _body, value); }
    public int SelectedTab => _tab;
    public bool ShowTabs => _tabsWanted && !Collapsed;
    public bool ShowBody => !Collapsed;
    public bool ShowStatus => TopBar.HasStatus && !Collapsed;
    public string CollapseTip => Collapsed ? "Развернуть" : "Свернуть";
    public bool HasUpdate => _updateVersion is not null;
    public string UpdateTip => $"Перезапустить с версией {_updateVersion}";

    public bool Collapsed
    {
        get => _settings.Value.Overlay.Collapsed;
        set
        {
            if (_settings.Value.Overlay.Collapsed == value) return;
            _settings.Value.Overlay.Collapsed = value;
            _settings.Save();
            Raise(); Raise(nameof(ShowBody)); Raise(nameof(ShowTabs)); Raise(nameof(ShowStatus)); Raise(nameof(CollapseTip));
        }
    }

    public void SelectTab(int i)
    {
        _tab = i;
        ShowPage();
    }

    void ShowPage()
    {
        for (var i = 0; i < Tabs.Count; i++) Tabs[i].IsSelected = i == _tab;
        Tabs[0].Title = PanelRules.MainTitle(_page);
        Body = _tab switch
        {
            1 => Opponent,
            2 => TopBar,
            _ => _page switch
            {
                CompassPage.Hero => Hero,
                CompassPage.Build => Build,
                CompassPage.Sim => Sim,
                CompassPage.GameOver => GameOver,
                _ => Status,
            },
        };
    }

    /// <summary>Hands one state to every page. A page that fails must not stop the others.</summary>
    public void Apply(UiState s)
    {
        Try(() => TopBar.Apply(s)); Try(() => Hero.Apply(s)); Try(() => Build.Apply(s));
        Try(() => Opponent.Apply(s)); Try(() => Sim.Apply(s)); Try(() => GameOver.Apply(s)); Try(() => Status.Apply(s));

        var page = PanelRules.MainPage(s);
        if (page != _page) { _page = page; _tab = 0; }
        _tabsWanted = page is CompassPage.Hero or CompassPage.Build or CompassPage.Sim;
        ShowPage();
        Raise(nameof(ShowTabs)); Raise(nameof(ShowStatus));
        if (_updateVersion != s.UpdateReadyVersion)
        {
            _updateVersion = s.UpdateReadyVersion;
            Raise(nameof(HasUpdate)); Raise(nameof(UpdateTip));
        }
    }

    static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Error("Панель не смогла обновиться", ex); }
    }
}
