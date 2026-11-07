using BgCompass.App.Runtime;
using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;
using BgCompass.Core.Logs;
using BgCompass.Core.Sim;

namespace BgCompass.App.Dev;

/// <summary>
/// A believable game built from real cards of the embedded catalog and run through the real advisors and the real
/// simulator. Used by --render-ui and the app tests; nothing here reaches the normal runtime path.
/// </summary>
public sealed class DemoScenario
{
    public CardCatalog Catalog { get; }
    public IReadOnlyList<string> Tribes { get; }
    public CardInfo[] Heroes { get; }
    readonly List<CardInfo> _board, _shop, _enemy;
    readonly HashSet<string> _confirmed;

    public DemoScenario(CardCatalog? catalog = null)
    {
        Catalog = catalog ?? CardCatalog.LoadEmbedded();
        var pool = Catalog.PoolMinions.Where(c => c.Name.Length > 0 && c.Attack > 0 && c.Tier is >= 1 and <= 6).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        Tribes = TribeNames.Real
            .Where(t => pool.Count(c => c.Tribes.Contains(t) && c.Tier <= 4) >= 6).Take(4).ToList();
        var main = Tribes[0];
        _board = pool.Where(c => c.Tribes.Contains(main) && c.Tier <= 4).Take(4)
            .Append(pool.First(c => c.Tribes.Contains(Tribes[1]) && c.Tier == 3)).ToList();
        _shop = pool.Where(c => c.Tier <= 4 && !_board.Contains(c) && c.Tribes.Count > 0)
            .GroupBy(c => c.Tribes[0]).Select(g => g.First()).Take(3).ToList();
        _shop.Add(pool.First(c => c.Tribes.Contains(main) && c.Tier == 4 && !_board.Contains(c)));
        _enemy = pool.Where(c => c.Tribes.Contains(Tribes[2]) && c.Tier <= 5).Skip(1).Take(5).ToList();
        _confirmed = [Tribes[0], Tribes[1], Tribes[2]];
        Heroes = HeroTips.Default.Ids.Select(Catalog.Get).Where(h => h is { IsBgHero: true }).Cast<CardInfo>().Take(8).ToArray();
    }

    public static CardsStatus Ready(CardCatalog c, string? warning = null) =>
        new(true, false, c.Version, warning, c.Version.Build);

    public static readonly LogStatus LogOk = new(
        @"C:\Program Files (x86)\Hearthstone\Logs\Hearthstone_2026_11_06_19_20_11\Power.log",
        new LogConfigStatus(true, true, []), @"C:\Users\player\AppData\Local\Blizzard\Hearthstone\log.config", null);

    public static readonly LogStatus LogMissing = new(null, new LogConfigStatus(true, true, []), LogOk.ConfigPath, null);
    public static readonly LogStatus LogDisabled = new(null, new LogConfigStatus(true, false, ["Раздел [Power] не найден"]), LogOk.ConfigPath, null);

    static MinionKeywords Kw(CardInfo c)
    {
        var k = MinionKeywords.None;
        foreach (var m in c.Mechanics)
            k |= m switch
            {
                "TAUNT" => MinionKeywords.Taunt, "DIVINE_SHIELD" => MinionKeywords.DivineShield,
                "POISONOUS" => MinionKeywords.Poisonous, "VENOMOUS" => MinionKeywords.Venomous,
                "REBORN" => MinionKeywords.Reborn, "WINDFURY" => MinionKeywords.Windfury,
                "STEALTH" => MinionKeywords.Stealth, "DEATHRATTLE" => MinionKeywords.Deathrattle,
                _ => MinionKeywords.None,
            };
        return k;
    }

    public static MinionView Mv(CardInfo c, int id, int pos, int plus = 0, bool golden = false) =>
        new(id, c.Id, c.Name, (c.Attack ?? 0) + plus, (c.Health ?? 0) + plus, pos, golden, Kw(c), c.Tribes, c.Tier ?? 1);

    List<MinionView> Views(IEnumerable<CardInfo> cards, int baseId, int plus = 0) =>
        cards.Select((c, i) => Mv(c, baseId + i, i + 1, plus, golden: false)).ToList();

    public MatchSnapshot Snapshot(MatchPhase phase)
    {
        var hero = Heroes[0];
        var offers = Heroes.Take(4).Select((h, i) => new HeroOption(100 + i, h.Id, h.Name, i + 1)).ToList();
        var players = Heroes.Concat(Heroes).Take(8).Select((h, i) => new PlayerView(
            i + 1, h.Id, i == 0 ? hero.Name : h.Name, IsLocal: i == 0, Place: i + 1, TavernTier: 2 + i % 4, Health: 40 - i * 4,
            Triples: i % 3, IsDead: false, IsNextOpponent: i == 3,
            LastBoard: i is 0 or 4 ? null : new LastSeenBoard(5, Views(_enemy.Take(3 + i % 3), 300 + i * 10)))).ToList();
        var board = Views(_board, 1, 2);
        var enemy = Views(_enemy, 200, 3);
        var encounters = new List<Encounter>
        {
            new(3, 6, Heroes[2].Id, Heroes[2].Name, Views(_enemy.Take(3), 400), Views(_board.Take(3), 410), EncounterResult.Win, null, 6),
            new(4, 5, Heroes[1].Id, Heroes[1].Name, Views(_enemy.Take(4), 420), Views(_board.Take(4), 430), EncounterResult.Loss, 9, null),
            new(5, 7, Heroes[3].Id, Heroes[3].Name, Views(_enemy.Take(4), 440), Views(_board, 450), EncounterResult.Tie, null, null),
        };
        var combat = new CombatSnapshot(6, 4, Heroes[3].Id, board, enemy, 3, 4, 31, 24, true, null);
        return phase switch
        {
            MatchPhase.HeroPick => MatchSnapshot.Empty with { Phase = phase, BuildNumber = Catalog.Version.Build, Round = 0, HeroOffers = offers, ConfirmedTribes = new HashSet<string>(_confirmed.Take(1)) },
            MatchPhase.Recruit => new MatchSnapshot(phase, Catalog.Version.Build, 6, hero.Id, hero.Name, 3, 7, [], board, Views(_shop, 500), players, encounters, _confirmed, null, null) with { UpgradeCost = 7, RefreshCost = 1, DamageCap = 10 },
            MatchPhase.Combat => new(phase, Catalog.Version.Build, 6, hero.Id, hero.Name, 3, 0, [], board, [], players, encounters, _confirmed, combat, null),
            MatchPhase.GameOver => new(phase, Catalog.Version.Build, 11, hero.Id, hero.Name, 5, 0, [], board, [], players, encounters, _confirmed, null, 3),
            _ => MatchSnapshot.Empty,
        };
    }

    public UiState State(MatchSnapshot m, HistoryStore? history = null, SimView? sim = null, LogStatus? log = null,
        CardsStatus? cards = null, bool withCatalog = true, IReadOnlySet<string>? absent = null)
    {
        absent ??= new HashSet<string> { Tribes[3] };
        var excluded = AdvicePipeline.Effective(absent, m.ConfirmedTribes);
        HeroPickAdvice? hero = null;
        if (withCatalog && m.Phase == MatchPhase.HeroPick && m.HeroOffers.Count > 0)
            hero = HeroAdvisor.Advise(m.HeroOffers, Catalog, m.ConfirmedTribes, excluded,
                history?.HeroStatsByHero(Catalog) ?? new Dictionary<string, HeroStats>());
        BoardAdvice? board = withCatalog && m.Phase is MatchPhase.Recruit or MatchPhase.Combat
            ? BoardAdvisor.Advise(m, Catalog, excluded) : null;
        if (sim is null && m.Combat is { } c && withCatalog)
            sim = new SimView(SimStatus.Done, CombatSimulator.Run(c, Catalog, 3000, seed: 7));
        return new UiState(m, hero, board, sim ?? SimView.None, absent,
            cards ?? (withCatalog ? Ready(Catalog) : new CardsStatus(false, true, null, null, null)),
            log ?? LogOk, withCatalog ? Catalog : null, false, 1)
        {
            Turn = withCatalog && m.Phase == MatchPhase.Recruit ? TurnAdvisor.Plan(m, Catalog, board) : null,
        };
    }

    /// <summary>The recruit scene with a what-if fight against the next opponent's last board.</summary>
    public UiState WithPreview(UiState s)
    {
        var c = Snapshot(MatchPhase.Combat).Combat!;
        return s with { PreviewCombat = c, Preview = new SimView(SimStatus.Done, CombatSimulator.Run(c, Catalog, 3000, seed: 7)) };
    }

    /// <summary>A dozen finished games over the offered heroes, so statistics and the "ваши игры" lines have something to say.</summary>
    public void SeedHistory(HistoryStore store)
    {
        var arche = Archetypes.LoadEmbedded();
        var places = new[] { 2, 5, 1, 4, 8, 3, 6, 2, 7, 1, 4, 3 };
        for (var i = 0; i < places.Length; i++)
        {
            var h = Heroes[i % 3];
            var boardIds = _board.Select(c => c.Id).ToList();
            store.Append(new MatchRecord($"demo-{i:00}", new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero).AddHours(-i * 20),
                Catalog.Version.Build, h.Id, h.Name, places[i], 8 + places[i], boardIds, _confirmed.ToList(),
                HistoryStore.ClassifyBoard(boardIds, Catalog, arche)));
        }
    }
}
