using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using BgCompass.App.Runtime;
using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;

namespace BgCompass.App.ViewModels;

public sealed class CatalogItemVm(CardInfo c, string lobby)
{
    public string Id { get; } = c.Id;
    public string Name { get; } = c.Name.Length > 0 ? c.Name : c.NameEn;
    public string NameEn { get; } = c.NameEn;
    public int Tier { get; } = c.Tier ?? 0;
    public string Stats { get; } = $"{c.Attack}/{c.Health}";
    public string Tribes { get; } = c.Tribes.Count == 0 ? "без племени" : string.Join(", ", c.Tribes.Select(Ru.Tribe));
    public string Text { get; } = c.Text;
    public bool HasText => Text.Length > 0;
    public string Keywords { get; } = string.Join(", ", Ru.Keywords(c.Mechanics));
    public bool HasKeywords => Keywords.Length > 0;
    public string Lobby { get; } = lobby;
    public bool HasLobby => Lobby.Length > 0;
    public bool InLobby => Lobby == "племя в лобби";
    /// <summary>Card text on one line; the full text lives in <see cref="Tip"/>.</summary>
    public string Line { get; } = c.Text.Replace('\n', ' ');
    public string Tip { get; } = string.Join("\n", new[] { c.NameEn, c.Text, string.Join(", ", Ru.Keywords(c.Mechanics)), lobby }.Where(x => x.Length > 0));
}

public sealed class CatalogVm : ViewModelBase
{
    public const string AnyTier = "Все ярусы", AnyTribe = "Все племена", AnyKeyword = "Все механики";
    public static readonly string[] LobbyOptions =
        ["Любой статус", "В лобби", "Не замечено", "Отсутствует", "Без племени"];

    UiState _state = UiState.Initial;
    string _search = "", _tier = AnyTier, _tribe = AnyTribe, _keyword = AnyKeyword, _lobby = LobbyOptions[0];
    string _count = "", _empty = "";
    IReadOnlyList<CatalogItemVm> _items = [];

    public IReadOnlyList<string> Tiers { get; } = [AnyTier, .. Enumerable.Range(1, 7).Select(i => $"Ярус {i}")];
    public IReadOnlyList<string> TribeOptions { get; } = [AnyTribe, .. TribeNames.Real.Select(Ru.Tribe)];
    public IReadOnlyList<string> KeywordOptions { get; } = [AnyKeyword, .. Ru.KeywordNames];
    public IReadOnlyList<string> Lobbies => LobbyOptions;

    public string Search { get => _search; set { if (Set(ref _search, value)) Rebuild(); } }
    public string Tier { get => _tier; set { if (Set(ref _tier, value)) Rebuild(); } }
    public string Tribe { get => _tribe; set { if (Set(ref _tribe, value)) Rebuild(); } }
    public string Keyword { get => _keyword; set { if (Set(ref _keyword, value)) Rebuild(); } }
    public string Lobby { get => _lobby; set { if (Set(ref _lobby, value)) Rebuild(); } }
    public IReadOnlyList<CatalogItemVm> Items { get => _items; private set => Set(ref _items, value); }
    public string Count { get => _count; private set => Set(ref _count, value); }
    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) Raise(nameof(HasEmpty)); } }
    public bool HasEmpty => _empty.Length > 0;
    public ICommand Clear => new RelayCommand(() =>
    {
        _search = ""; _tier = AnyTier; _tribe = AnyTribe; _keyword = AnyKeyword; _lobby = LobbyOptions[0];
        Raise(nameof(Search)); Raise(nameof(Tier)); Raise(nameof(Tribe)); Raise(nameof(Keyword)); Raise(nameof(Lobby));
        Rebuild();
    });

    CardCatalog? _catalog;
    IReadOnlyList<CatalogItemVm> _all = [];

    public void Apply(UiState s)
    {
        var lobbyChanged = !ReferenceEquals(s.Match.ConfirmedTribes, _state.Match.ConfirmedTribes)
            || !ReferenceEquals(s.AbsentTribes, _state.AbsentTribes);
        _state = s;
        if (!ReferenceEquals(s.Catalog, _catalog) || lobbyChanged)
        {
            _catalog = s.Catalog;
            _all = s.Catalog is null ? [] : s.Catalog.PoolMinions
                .OrderBy(c => c.Tier ?? 0).ThenBy(c => c.Name, StringComparer.Create(Ru.Culture, true))
                .Select(c => new CatalogItemVm(c, LobbyOf(c, s))).ToList();
            Rebuild();
        }
    }

    static string LobbyOf(CardInfo c, UiState s)
    {
        var real = c.Tribes.Where(t => TribeNames.Real.Contains(t)).ToList();
        if (real.Count == 0 || s.Match.Phase == MatchPhase.Idle) return "";
        if (real.Any(s.Match.ConfirmedTribes.Contains)) return "племя в лобби";
        if (real.All(s.AbsentTribes.Contains)) return "отмечено как отсутствующее";
        return "племя не замечено";
    }

    void Rebuild()
    {
        if (_catalog is null)
        {
            Items = []; Count = "";
            Empty = "Карты загружаются…";
            return;
        }
        var tierN = _tier == AnyTier ? 0 : int.Parse(_tier[^1..]);
        var tribeKey = _tribe == AnyTribe ? null : TribeNames.Real.FirstOrDefault(t => Ru.Tribe(t) == _tribe);
        var q = _search.Trim().ToLower(Ru.Culture);
        var lobby = _lobby;
        var kw = _keyword == AnyKeyword ? null : _keyword;
        var cards = _catalog.PoolMinions.ToDictionary(c => c.Id);
        var result = _all.Where(i =>
        {
            if (tierN != 0 && i.Tier != tierN) return false;
            var c = cards[i.Id];
            if (tribeKey is not null && !c.Tribes.Contains(tribeKey)) return false;
            if (kw is not null && !i.Keywords.Contains(kw, StringComparison.Ordinal)) return false;
            if (lobby == LobbyOptions[4] && i.Tribes != "без племени") return false;
            if (lobby == LobbyOptions[1] && i.Lobby != "племя в лобби") return false;
            if (lobby == LobbyOptions[2] && i.Lobby != "племя не замечено") return false;
            if (lobby == LobbyOptions[3] && i.Lobby != "отмечено как отсутствующее") return false;
            if (q.Length > 0
                && !(i.Name.ToLower(Ru.Culture).Contains(q) || i.NameEn.ToLowerInvariant().Contains(q)
                     || c.Text.ToLower(Ru.Culture).Contains(q) || c.TextEn.ToLowerInvariant().Contains(q)))
                return false;
            return true;
        }).ToList();
        Items = result;
        Count = $"{result.Count} из {_all.Count}";
        Empty = result.Count == 0 ? "Ничего не найдено" : "";
    }
}

public sealed class HeroRowVm(string name, HeroStats s)
{
    public string Name { get; } = name;
    public string Games { get; } = s.Games.ToString();
    public string Average { get; } = Ru.Num(s.AveragePlace);
    public string Top4 { get; } = Ru.Pct(s.Top4Rate);
    public string Wins { get; } = s.Wins.ToString();
    public int GamesN { get; } = s.Games;
    public string Small { get; } = s.Games < 5 ? "мало игр" : "";
    public string Tile { get; } = Ru.Num(s.AveragePlace);
    public bool IsGood { get; } = s.Games >= 5 && s.AveragePlace <= 4.0;
    public bool IsBad { get; } = s.Games >= 5 && s.AveragePlace >= 5.0;
    public string Sub { get; } = Ru.Games(s.Games) + " · топ-4 " + Ru.Pct(s.Top4Rate) + (s.Wins > 0 ? $" · побед {s.Wins}" : "");
}

public sealed class BuildRowVm(string name, HeroStats s, int max)
{
    public string Name { get; } = name;
    public string Games { get; } = Ru.Games(s.Games);
    public string Average { get; } = $"среднее место {Ru.Num(s.AveragePlace)}";
    public double Share { get; } = max == 0 ? 0 : (double)s.Games / max;
    public string Tile { get; } = Ru.Num(s.AveragePlace);
    public bool IsGood { get; } = s.Games >= 5 && s.AveragePlace <= 4.0;
    public bool IsBad { get; } = s.Games >= 5 && s.AveragePlace >= 5.0;
    public string Sub { get; } = Ru.Games(s.Games) + " · топ-4 " + Ru.Pct(s.Top4Rate);
}

public sealed class GameRowVm(MatchRecord r, string hero, string build)
{
    public string When { get; } = r.EndedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru.Culture);
    public string Hero { get; } = hero;
    public string Place { get; } = r.Place is int p ? p.ToString() : "-";
    public string Rounds { get; } = r.Rounds.ToString();
    public string Build { get; } = build;
    public bool HasPlace { get; } = r.Place is not null;
    public bool IsTop4 { get; } = r.Place is <= 4;
    public bool IsLoss { get; } = r.Place is > 4;
    public string Tip { get; } = $"Раундов: {r.Rounds}";
}

public sealed class StatsVm : ViewModelBase
{
    readonly AppHost _host;
    string _totals = "", _empty = "", _sumGames = "", _sumAverage = "", _sumTop4 = "", _sumWins = "";
    IReadOnlyList<HeroRowVm> _heroes = [];
    IReadOnlyList<BuildRowVm> _builds = [];
    IReadOnlyList<GameRowVm> _games = [];

    public StatsVm(AppHost host)
    {
        _host = host;
        host.GameRecorded += _ => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    public string MmrNote { get; } =
        "Рейтинг (MMR) недоступен: Hearthstone никогда не пишет его в Power.log, а читать память игры программа не умеет и не будет.";
    /// <summary>Fallback line for a history without places; otherwise the summary numbers below are shown.</summary>
    public string Totals { get => _totals; private set { if (Set(ref _totals, value)) Raise(nameof(HasTotals)); } }
    public bool HasTotals => _totals.Length > 0;
    public bool HasSummary => _sumGames.Length > 0;
    public string SumGames { get => _sumGames; private set { if (Set(ref _sumGames, value)) Raise(nameof(HasSummary)); } }
    public string SumAverage { get => _sumAverage; private set => Set(ref _sumAverage, value); }
    public string SumTop4 { get => _sumTop4; private set => Set(ref _sumTop4, value); }
    public string SumWins { get => _sumWins; private set => Set(ref _sumWins, value); }
    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) { Raise(nameof(HasEmpty)); Raise(nameof(HasData)); } } }
    public bool HasEmpty => _empty.Length > 0;
    public bool HasData => _empty.Length == 0;
    public IReadOnlyList<HeroRowVm> Heroes { get => _heroes; private set => Set(ref _heroes, value); }
    public IReadOnlyList<BuildRowVm> Builds { get => _builds; private set => Set(ref _builds, value); }
    public IReadOnlyList<GameRowVm> Games { get => _games; private set => Set(ref _games, value); }

    public void Reload()
    {
        try
        {
            var history = _host.History.History();
            if (history.Count == 0)
            {
                Empty = "Партий пока нет. Сыграйте с включёнными логами.";
                Heroes = []; Builds = []; Games = []; Totals = ""; SumGames = ""; SumAverage = ""; SumTop4 = ""; SumWins = "";
                return;
            }
            Empty = "";
            var cards = _host.State.Catalog ?? CardCatalog.LoadEmbedded();
            var arch = Archetypes.LoadEmbedded();
            string HeroName(string id) => cards?.Get(id)?.Name is { Length: > 0 } n ? n : id;
            string BuildName(string? id) => id is null ? "не определён" : arch.Get(id)?.Name ?? id;

            var all = _host.History.Overall();
            Totals = all is null ? $"Партий: {history.Count}, мест в логе нет" : "";
            SumGames = all is null ? "" : all.Games.ToString();
            SumAverage = all is null ? "" : Ru.Num(all.AveragePlace);
            SumTop4 = all is null ? "" : Ru.Pct(all.Top4Rate);
            SumWins = all is null ? "" : all.Wins.ToString();

            Heroes = _host.History.HeroStatsByHero(cards)
                .OrderByDescending(kv => kv.Value.Games).ThenBy(kv => kv.Value.AveragePlace)
                .Select(kv => new HeroRowVm(HeroName(kv.Key), kv.Value)).ToList();
            var byBuild = _host.History.StatsByArchetype();
            var max = byBuild.Count == 0 ? 0 : byBuild.Values.Max(v => v.Games);
            Builds = byBuild.OrderByDescending(kv => kv.Value.Games)
                .Select(kv => new BuildRowVm(BuildName(kv.Key), kv.Value, max)).ToList();
            Games = history.OrderByDescending(r => r.EndedAt)
                .Select(r => new GameRowVm(r, r.HeroName.Length > 0 ? r.HeroName : HeroName(r.HeroCardId), BuildName(r.ArchetypeId))).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать историю", ex);
            Empty = "Не удалось прочитать историю. Подробности в app.log.";
        }
    }
}

public sealed class HotkeyRowVm(Overlay.HotkeyStatus h)
{
    public string Name { get; } = h.Name;
    public string Keys { get; } = h.Keys;
    public string State { get; } = h.Registered ? "работает" : "не работает";
    public bool Ok { get; } = h.Registered;
    public string Detail { get; } = h.Detail;
}

public sealed class SettingsVm : ViewModelBase
{
    readonly AppHost _host;
    readonly Overlay.IHotkeys _hotkeys;
    readonly Func<Task<string?>> _pickFolder;
    readonly Action _resetPositions;
    string _logState = "", _logPath = "", _logTip = "", _configState = "", _configTip = "", _enableResult = "", _enableTip = "", _cardsState = "", _cardsTip = "", _cardsWarning = "", _refreshNote = "";
    string? _updateVersion;
    bool _canEnable, _logOk, _configOk;

    public SettingsVm(AppHost host, Overlay.IHotkeys hotkeys, Func<Task<string?>> pickFolder, Action resetPositions)
    {
        _host = host; _hotkeys = hotkeys; _pickFolder = pickFolder; _resetPositions = resetPositions;
        PickFolder = new RelayCommand(async () =>
        {
            var f = await _pickFolder();
            if (f is not null) _host.SetLogFolder(f);
        });
        ClearFolder = new RelayCommand(() => _host.SetLogFolder(null));
        EnableLogs = new RelayCommand(DoEnable);
        Refresh = new RelayCommand(() => { _host.RefreshCardsNow(); RefreshNote = "Запрос отправлен"; });
        Restart = new RelayCommand(() => _host.Updater?.Restart());
        ResetPositions = new RelayCommand(_resetPositions);
        Hotkeys = hotkeys.Status.Select(h => new HotkeyRowVm(h)).ToList();
    }

    public ICommand PickFolder { get; }
    public ICommand ClearFolder { get; }
    public ICommand EnableLogs { get; }
    public ICommand Refresh { get; }
    public ICommand ResetPositions { get; }
    public ICommand Restart { get; }
    public IReadOnlyList<HotkeyRowVm> Hotkeys { get; private set; }

    public string LogState { get => _logState; private set => Set(ref _logState, value); }
    public string LogTip { get => _logTip; private set => Set(ref _logTip, value); }
    public string LogPath { get => _logPath; private set { if (Set(ref _logPath, value)) Raise(nameof(HasLogPath)); } }
    public bool HasLogPath => _logPath.Length > 0;
    public bool LogOk { get => _logOk; private set => Set(ref _logOk, value); }
    public string ConfigState { get => _configState; private set => Set(ref _configState, value); }
    public string ConfigTip { get => _configTip; private set => Set(ref _configTip, value); }
    public bool ConfigOk { get => _configOk; private set => Set(ref _configOk, value); }
    public bool CanEnable { get => _canEnable; private set => Set(ref _canEnable, value); }
    public string EnableResult { get => _enableResult; private set { if (Set(ref _enableResult, value)) Raise(nameof(HasEnableResult)); } }
    public string EnableTip { get => _enableTip; private set => Set(ref _enableTip, value); }
    public bool HasEnableResult => _enableResult.Length > 0;
    public string CardsState { get => _cardsState; private set => Set(ref _cardsState, value); }
    public string CardsWarning { get => _cardsWarning; private set { if (Set(ref _cardsWarning, value)) Raise(nameof(HasCardsWarning)); } }
    public string CardsTip { get => _cardsTip; private set => Set(ref _cardsTip, value); }
    public bool HasCardsWarning => _cardsWarning.Length > 0;
    public string RefreshNote { get => _refreshNote; private set => Set(ref _refreshNote, value); }
    public bool IsReplay => _host.IsReplay;

    public string AppVersion { get; } = "BG Compass " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?");
    public bool HasUpdate => _updateVersion is not null;
    public bool NoUpdate => _updateVersion is null && _host.Updater is not null;
    public string UpdateText => $"Перезапустить и обновить до {_updateVersion}";

    public double UserScale
    {
        get => _host.Settings.Value.UserScale;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0.5, 2.0), 2);
            if (Math.Abs(v - _host.Settings.Value.UserScale) < 0.001) return;
            _host.Settings.Value.UserScale = v;
            _host.Settings.Save();
            Raise(); Raise(nameof(UserScaleText));
        }
    }
    public string UserScaleText => $"{(int)Math.Round(UserScale * 100)}%";

    public bool ClickThrough
    {
        get => _host.Settings.Value.ClickThrough;
        set
        {
            if (_host.Settings.Value.ClickThrough == value) return;
            _host.Settings.Value.ClickThrough = value;
            _host.Settings.Save();
            Raise();
        }
    }

    public string Eula { get; } = LoadEula();

    static string LoadEula()
    {
        const string ru = "BG Compass читает только текстовый журнал Power.log, который Hearthstone пишет сам. Программа не внедряется в игру, "
            + "не читает её память, не нажимает кнопки за вас и не открывает процесс игры. Окно игры она находит только по его положению на экране.\n\n"
            + "Лицензионное соглашение Blizzard (раздел 1.C) запрещает программы, которые «облегчают игровой процесс» или дают преимущество. "
            + "Письменного разрешения на оверлеи у Blizzard нет. Трекеры, читающие логи, терпят годами, но это не гарантия. "
            + "Пользуйтесь на свой риск.";
        try
        {
            using var s = typeof(SettingsVm).Assembly.GetManifestResourceStream("DESIGN.md");
            if (s is null) return ru;
            using var r = new StreamReader(s);
            var text = r.ReadToEnd();
            var i = text.IndexOf("## Blizzard rules", StringComparison.Ordinal);
            if (i < 0) return ru;
            var body = text[(i + "## Blizzard rules".Length)..].Trim();
            var j = body.IndexOf("\n## ", StringComparison.Ordinal);
            if (j >= 0) body = body[..j].Trim();
            return ru + "\n\nИз проектной документации (англ.):\n" + body;
        }
        catch { return ru; }
    }

    void DoEnable()
    {
        try
        {
            var r = _host.EnableLogs();
            EnableResult = r.Changed ? "Включено. Перезапустите игру" : "Уже включено";
            EnableTip = r.Changed
                ? (r.BackupPath is { } b ? $"Копия прежнего файла: {b}\n" : "") + "Hearthstone читает log.config только при запуске."
                : "Файл не менялся.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось включить логи", ex);
            EnableResult = "Не удалось изменить log.config";
            EnableTip = ex.Message;
        }
        Apply(_host.State);
    }

    public void Apply(UiState s)
    {
        var log = s.Log;
        if (log.PowerLogPath is { } p)
        {
            LogOk = true;
            LogState = s.IsReplay ? "Повтор лога" : "Power.log найден";
            LogTip = s.IsReplay ? "Режим повтора: читается файл для проверки." : "Журнал игры, из которого программа читает партию.";
            LogPath = p;
        }
        else
        {
            LogOk = false;
            LogState = "Power.log не найден";
            LogTip = "Запустите Hearthstone один раз или выберите папку с игрой или Logs вручную.";
            LogPath = log.Override is { } o ? $"Указана папка: {o}" : "";
        }
        var c = log.Config;
        ConfigOk = c is { PowerEnabled: true };
        CanEnable = !ConfigOk && !s.IsReplay;
        ConfigState = c is null ? "log.config не проверен"
            : !c.FileExists ? "log.config нет"
            : c.PowerEnabled ? "[Power] включён"
            : "[Power] выключен";
        ConfigTip = (c is null ? "log.config не удалось проверить." : !c.FileExists ? "Без log.config Hearthstone не пишет Power.log." : "Раздел [Power] в log.config.")
            + $"\nФайл: {log.ConfigPath}\nКнопка дописывает раздел [Power] и сохраняет копию прежнего файла. После этого перезапустите Hearthstone."
            + (c is { Problems.Count: > 0 } ? "\n" + string.Join(" ", c.Problems) : "");
        var cs = s.Cards;
        if (cs.Version is { } v)
        {
            CardsState = $"Сборка {v.Build?.ToString() ?? "?"} · {v.FetchedAt.ToLocalTime():dd.MM HH:mm}" + (cs.Refreshing ? " · обновляется" : "");
            CardsTip = $"Источник: {v.Source}\nСборка игры из лога: {cs.GameBuild?.ToString() ?? "пока неизвестна"}";
        }
        else
        {
            CardsState = cs.Ready ? "Данные загружены" : "Данные загружаются";
            CardsTip = "";
        }
        CardsWarning = cs.Warning ?? "";
        _updateVersion = s.UpdateReadyVersion;
        Raise(nameof(HasUpdate)); Raise(nameof(NoUpdate)); Raise(nameof(UpdateText));
    }
}

public sealed class MainVm : ViewModelBase
{
    int _tab;
    public MainVm(CatalogVm catalog, StatsVm stats, SettingsVm settings, bool replay)
    {
        Catalog = catalog; Stats = stats; Settings = settings; IsReplay = replay;
        Tabs = new[] { "Каталог", "Статистика", "Настройки" }
            .Select((t, i) => new TabVm(t, () => SelectedTab = i)).ToList();
        Tabs[0].IsSelected = true;
    }
    public CatalogVm Catalog { get; }
    public StatsVm Stats { get; }
    public SettingsVm Settings { get; }
    public bool IsReplay { get; }
    public IReadOnlyList<TabVm> Tabs { get; }
    public bool ShowCatalog => _tab == 0;
    public bool ShowStats => _tab == 1;
    public bool ShowSettings => _tab == 2;
    public int SelectedTab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            for (var i = 0; i < Tabs.Count; i++) Tabs[i].IsSelected = i == value;
            Raise(nameof(ShowCatalog)); Raise(nameof(ShowStats)); Raise(nameof(ShowSettings));
            if (value == 1) Stats.Reload();
        }
    }
}
