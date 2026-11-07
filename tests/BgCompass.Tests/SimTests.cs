using System.Diagnostics;
using BgCompass.Core;
using BgCompass.Core.Cards;
using BgCompass.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace BgCompass.Tests;

/// <summary>
/// Expectations are derived by hand from the combat rules. With no unmodelled card the odds are widened by
/// eps = 0.04 toward the prior (0.455 / 0.09 / 0.455): a certain win prints 0.9782, a certain tie 0.9636.
/// </summary>
public class SimTests
{
    static readonly CardCatalog Cards = CardCatalog.LoadEmbedded();
    static readonly ScriptBook Book = new(Cards);
    readonly ITestOutputHelper _out;

    public SimTests(ITestOutputHelper output) => _out = output;

    const string Skeleton = "BG_ICC_026t"; // 1/1 Undead token with no text: a vanilla body
    const double Eps = 0.04, PWin = 0.455, PTie = 0.09, PLoss = 0.455;
    const double CertainWin = (1 - Eps) + Eps * PWin;      // 0.9782
    const double CertainLoss = (1 - Eps) + Eps * PLoss;    // 0.9782
    const double CertainTie = (1 - Eps) + Eps * PTie;      // 0.9636
    const double Never = Eps * PWin;                       // 0.0182

    static MinionView V(string id, int atk, int hp, MinionKeywords kw = MinionKeywords.None, int tier = 1, bool golden = false) =>
        new(0, id, id, atk, hp, 0, golden, kw, [], tier);

    static MinionView P(int atk, int hp, MinionKeywords kw = MinionKeywords.None, int tier = 1) => V(Skeleton, atk, hp, kw, tier);

    static IReadOnlyList<MinionView> Board(params MinionView[] v) => v.Select((m, i) => m with { EntityId = i + 1, Position = i }).ToList();

    static CombatSnapshot Snap(IReadOnlyList<MinionView> a, IReadOnlyList<MinionView> b, int? ft = null, int? et = null, int? fh = null) =>
        new(5, 2, "TB_BaconShop_HERO_01", a, b, ft, et, fh, null, true, null);

    static SimResult Run(CombatSnapshot s, int n = 3000, int seed = 7, CardCatalog? cards = null) =>
        CombatSimulator.Run(s, cards ?? Cards, n, seed);

    static Minion[] Ts(params MinionView[] v) => v.Select(T).ToArray();

    static Minion T(MinionView v)
    {
        var m = Book.Template(v, out _, out _, out var err);
        Assert.True(m is not null, err);
        return m!;
    }

    static string GoldenId(string baseId) => Cards.GetByDbf(Cards.Get(baseId)!.PremiumDbfId)!.Id;

    static void AssertSumsToOne(SimResult r) => Assert.Equal(1.0, r.Win + r.Tie + r.Loss, 9);

    // ---- outcomes -------------------------------------------------------------------------

    [Fact]
    public void Mutual_kill_is_a_tie()
    {
        var r = Run(Snap(Board(P(2, 2)), Board(P(2, 2))));
        Assert.True(r.Available);
        Assert.Equal(CertainTie, r.Tie, 9);
        Assert.Equal(Never, r.Win, 9);
        Assert.Equal(Never, r.Loss, 9);
        Assert.Null(r.DamageTakenP50);
        Assert.Null(r.DamageDealtP50);
        AssertSumsToOne(r);
    }

    [Fact]
    public void Three_three_always_beats_two_two()
    {
        // Whoever swings first, the 3/3 survives: it kills the 2/2 and takes at most 2.
        var r = Run(Snap(Board(P(3, 3)), Board(P(2, 2))));
        Assert.Equal(CertainWin, r.Win, 9);
        Assert.Equal(Never, r.Loss, 9);
        var reverse = Run(Snap(Board(P(2, 2)), Board(P(3, 3))));
        Assert.Equal(CertainLoss, reverse.Loss, 9);
    }

    [Fact]
    public void Hero_damage_is_survivor_tiers_plus_the_winners_tavern_tier()
    {
        // Our 5/5s (tier 3 and 2) both survive a 1/1: 3 + 2, plus our tier 4 = 9.
        var won = Run(Snap(Board(P(5, 5, tier: 3), P(5, 5, tier: 2)), Board(P(1, 1)), ft: 4));
        Assert.Equal(9, won.DamageDealtP50);
        Assert.Null(won.DamageTakenP50);

        // Mirror: they win with the same two survivors and tier 5: 3 + 2 + 5 = 10, every time.
        var lost = Run(Snap(Board(P(1, 1)), Board(P(5, 5, tier: 3), P(5, 5, tier: 2)), et: 5));
        Assert.Equal((10, 10, 10), (lost.DamageTakenP10, lost.DamageTakenP50, lost.DamageTakenP90));
    }

    [Fact]
    public void Divine_shield_absorbs_exactly_one_hit()
    {
        // 2/1 vs 2/1 is a tie; with a shield on ours, ours always survives the exchange.
        var r = Run(Snap(Board(P(2, 1, MinionKeywords.DivineShield)), Board(P(2, 1))));
        Assert.Equal(CertainWin, r.Win, 9);
        var without = Run(Snap(Board(P(2, 1)), Board(P(2, 1))));
        Assert.Equal(CertainTie, without.Tie, 9);
    }

    [Fact]
    public void Taunt_is_always_the_defender_and_stealth_never_is()
    {
        var f = new Fighter(1);
        f.Setup(Ts(P(1, 1)),
            Ts(P(5, 5), P(5, 5, MinionKeywords.Taunt), P(5, 5, MinionKeywords.Taunt | MinionKeywords.Stealth), P(5, 5)));
        var taunt = f.B.Ms[1];
        for (var i = 0; i < 200; i++) Assert.Same(taunt, f.PickDefender(f.B));

        var g = new Fighter(2);
        g.Setup(Ts(P(1, 1)), Ts(P(5, 5, MinionKeywords.Stealth), P(5, 5), P(5, 5, MinionKeywords.Stealth)));
        for (var i = 0; i < 200; i++) Assert.Same(g.B.Ms[1], g.PickDefender(g.B));
    }

    [Fact]
    public void Poisonous_kills_every_time_but_venomous_only_once()
    {
        // 1/100 against two 5/50: poison kills both (95 health left over); venom is spent on the first.
        var enemy = Board(P(5, 50), P(5, 50));
        var poison = Run(Snap(Board(P(1, 100, MinionKeywords.Poisonous)), enemy));
        Assert.Equal(CertainWin, poison.Win, 9);
        var venom = Run(Snap(Board(P(1, 100, MinionKeywords.Venomous)), enemy));
        Assert.Equal(CertainLoss, venom.Loss, 9);
    }

    [Fact]
    public void Reborn_returns_at_one_health_with_printed_attack_in_the_same_spot()
    {
        // Risen Rider prints 2/1. Shown as 6/9 taunt reborn, dead at index 1 of three.
        var f = new Fighter(3);
        f.Setup(Ts(P(1, 1), V("BG25_001", 6, 9, MinionKeywords.Taunt | MinionKeywords.Reborn), P(1, 1)), Ts(P(1, 1)));
        f.A.Ms[1].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(3, f.A.N);
        var reborn = f.A.Ms[1];
        Assert.Equal((2, 1, false, true), (reborn.Atk, reborn.Hp, reborn.Reborn, reborn.Taunt));
    }

    [Fact]
    public void Reborn_does_not_save_a_minion_that_is_still_outclassed()
    {
        // 6/9 reborn against a 10/10: it dies, comes back as a 2/1, and dies again. The 10/10 always wins.
        var r = Run(Snap(Board(V("BG25_001", 6, 9, MinionKeywords.Reborn)), Board(P(10, 10))));
        Assert.Equal(CertainLoss, r.Loss, 9);
    }

    [Fact]
    public void Windfury_swings_twice_and_a_plain_minion_once()
    {
        var f = new Fighter(4);
        f.Setup(Ts(P(3, 100, MinionKeywords.Windfury), P(3, 100)), Ts(P(0, 10)));
        f.Attack(f.A.Ms[0], f.A, f.B);
        Assert.Equal(4, f.B.Ms[0].Hp);
        f.Attack(f.A.Ms[1], f.A, f.B);
        Assert.Equal(1, f.B.Ms[0].Hp);
    }

    [Fact]
    public void Summons_stop_at_seven()
    {
        // Harmless Bonehead (two skeletons) dies on a board of seven: one slot opens, one skeleton fits.
        var f = new Fighter(5);
        f.Setup(Ts(V("BG28_300", 1, 1), P(1, 1), P(1, 1), P(1, 1), P(1, 1), P(1, 1), P(1, 1)), Ts(P(1, 1)));
        f.A.Ms[0].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(7, f.A.N);
        Assert.Equal(1, f.A.Ms[0].Atk); // the skeleton took the Bonehead's slot
    }

    [Fact]
    public void Deathrattle_tokens_come_from_the_card_text_and_golden_doubles_them()
    {
        // Bonehead 1/1 trades with a 1/1 and leaves two 1/1 skeletons (tier 1 each) behind; our tavern tier 2.
        var r = Run(Snap(Board(V("BG28_300", 1, 1)), Board(P(1, 1)), ft: 2));
        Assert.Equal(CertainWin, r.Win, 9);
        Assert.Equal(2 * 1 + 2, r.DamageDealtP50);

        // The golden one summons four.
        var golden = Run(Snap(Board(V(GoldenId("BG28_300"), 2, 2, golden: true)), Board(P(2, 2)), ft: 2));
        Assert.Equal(CertainWin, golden.Win, 9);
        Assert.Equal(4 * 1 + 2, golden.DamageDealtP50);
    }

    [Fact]
    public void Titus_makes_deathrattles_trigger_twice()
    {
        var f = new Fighter(6);
        f.Setup(Ts(V("BG28_300", 1, 1), V("BG25_354", 1, 7)), Ts(P(1, 1)));
        f.A.Ms[0].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(5, f.A.N); // Titus + 2 x 2 skeletons
    }

    [Fact]
    public void The_larger_board_attacks_first()
    {
        // [5/5, 1/1] vs [5/5]: if we start, the 5/5s trade and the 1/1 remains - a win every time.
        // If they started, their 5/5 could hit the 1/1 instead and the fight could end as a tie.
        var r = Run(Snap(Board(P(5, 5), P(1, 1)), Board(P(5, 5))));
        Assert.Equal(CertainWin, r.Win, 9);
    }

    [Fact]
    public void Equal_boards_are_a_fair_coin()
    {
        var a = Board(P(4, 4), P(2, 2), P(3, 5));
        var r = Run(Snap(a, a));
        Assert.True(Math.Abs(r.Win - r.Loss) < 0.05, $"win {r.Win} loss {r.Loss}");
        AssertSumsToOne(r);
    }

    [Fact]
    public void Odds_sum_to_one_and_stay_inside_their_bounds()
    {
        foreach (var (a, b) in new[]
        {
            (Board(P(2, 2)), Board(P(2, 2))),
            (Board(P(9, 9), P(3, 3)), Board(P(1, 1))),
            (Board(P(1, 1)), Board(P(4, 6), P(4, 6), P(2, 2))),
        })
        {
            var r = Run(Snap(a, b));
            AssertSumsToOne(r);
            Assert.InRange(r.Win, 0, 1);
            Assert.InRange(r.Tie, 0, 1);
            Assert.InRange(r.Loss, 0, 1);
        }
    }

    [Fact]
    public void An_unmodelled_card_keeps_the_odds_off_zero_and_one()
    {
        // Showy Cyclist is on the hard list: eps = 0.04 + 0.02 = 0.06, so a certain win leaves 0.06 * 0.455 for the loss.
        var r = Run(Snap(Board(V("BG31_925", 30, 30)), Board(P(1, 1))));
        Assert.Equal(0.06 * PLoss, r.Loss, 9);
        Assert.Equal(0.94 + 0.06 * PWin, r.Win, 9);
        Assert.InRange(r.Win, 0.01, 0.99);
        Assert.Equal([Cards.Get("BG31_925")!.Name], r.Unmodelled);
        AssertSumsToOne(r);
    }

    [Fact]
    public void Lethal_needs_known_life_and_the_enemy_tier_and_never_claims_certainty()
    {
        var snap = Snap(Board(P(1, 1)), Board(P(5, 5, tier: 3), P(5, 5, tier: 2)), et: 5, fh: 10);
        var r = Run(snap);
        // Every loss costs 3 + 2 + 5 = 10 >= 10 life: raw lethal 1, widened to 0.96 + 0.04 * 0.455 * 0.5.
        Assert.Equal(0.96 + 0.04 * PLoss * 0.5, r.LethalRisk, 9);
        Assert.True(r.LethalRisk <= r.Loss);
        Assert.Equal(0.0, Run(Snap(snap.Friendly, snap.Enemy, et: 5)).LethalRisk);          // life unknown
        Assert.Equal(0.0, Run(Snap(snap.Friendly, snap.Enemy, fh: 10)).LethalRisk);         // enemy tier unknown
        Assert.Equal(0.04 * PLoss * 0.5, Run(Snap(snap.Friendly, snap.Enemy, et: 5, fh: 11)).LethalRisk, 9); // 10 < 11 life
    }

    // ---- hand-written scripts ----------------------------------------------------------------

    [Fact]
    public void Card_text_that_no_longer_matches_drops_the_script_and_widens_the_odds()
    {
        var snap = Snap(Board(V("BGS_018", 7, 7)), Board(P(1, 1)));
        Assert.Empty(Run(snap).Unmodelled);

        var edited = new CardCatalog(
            Cards.All.Select(c => c.Id == "BGS_018" ? c with { TextEn = "Deathrattle: Your Beasts become Mechs." } : c).ToList(),
            Cards.Version);
        var r = Run(snap, cards: edited);
        Assert.Equal([Cards.Get("BGS_018")!.Name], r.Unmodelled);
    }

    [Fact]
    public void Sewer_lord_leaves_two_rats_that_leave_turtles()
    {
        var f = new Fighter(8);
        f.Setup(Ts(V("BG35_604", 4, 4)), Ts(P(1, 1)));
        f.A.Ms[0].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(2, f.A.N);
        Assert.All(new[] { f.A.Ms[0], f.A.Ms[1] }, m => Assert.Equal((3, 2), (m.Atk, m.Hp)));
        f.A.Ms[0].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(2, f.A.N);
        Assert.Equal((2, 3, true), (f.A.Ms[0].Atk, f.A.Ms[0].Hp, f.A.Ms[0].Taunt)); // Turtle: 2/3 Taunt
    }

    // ---- refusing to guess --------------------------------------------------------------------

    [Fact]
    public void An_incomplete_snapshot_is_unavailable_with_a_reason()
    {
        var s = Snap(Board(P(1, 1)), Board(P(1, 1))) with { Complete = false, IncompleteReason = "нет доски соперника" };
        var r = Run(s);
        Assert.False(r.Available);
        Assert.Contains("нет доски соперника", r.UnavailableReason);
        Assert.Equal(0, r.Iterations);
    }

    [Fact]
    public void An_unknown_card_fights_with_logged_stats_and_is_named_as_unmodelled()
    {
        var r = Run(Snap(Board(P(3, 3)), Board(V("BG_NOT_A_CARD_1", 1, 1))));
        Assert.True(r.Available);
        Assert.Single(r.Unmodelled);
        Assert.True(r.Win < 1.0, "unmodelled card must widen the odds");
    }

    [Fact]
    public void An_empty_side_is_unavailable()
    {
        Assert.False(Run(Snap(Board(P(1, 1)), Board())).Available);
        Assert.False(Run(Snap(Board(), Board(P(1, 1)))).Available);
    }

    [Fact]
    public void Too_many_unmodelled_cards_is_unavailable_but_ten_is_not()
    {
        string[] hard =
        [
            "BG20_104", "BG33_886", "BG33_883", "BG33_430", "BG31_835", "BG34_140", "BG25_009", "BG25_806",
            "BG31_801", "BG_TTN_401", "BG25_011",
        ];
        MinionView[] Of(IEnumerable<string> ids) => ids.Select(i => V(i, 5, 5)).ToArray();
        var eleven = Run(Snap(Board(Of(hard.Take(7)).ToArray()), Board(Of(hard.Skip(7)).ToArray())));
        Assert.False(eleven.Available);
        Assert.Contains("11", eleven.UnavailableReason);

        var ten = Run(Snap(Board(Of(hard.Take(7)).ToArray()), Board(Of(hard.Skip(7).Take(3)).ToArray())));
        Assert.True(ten.Available);
        Assert.Equal(10, ten.Unmodelled.Count);
        AssertSumsToOne(ten);
    }

    // ---- determinism and cancellation -----------------------------------------------------------

    [Fact]
    public void A_fixed_seed_gives_identical_results()
    {
        var snap = Snap(Board(P(4, 4), V("BG28_300", 2, 2), P(3, 5, MinionKeywords.Taunt)), Board(P(3, 3), P(5, 4), P(2, 6)), ft: 3, et: 4, fh: 12);
        var a = Run(snap, seed: 99);
        var b = Run(snap, seed: 99);
        Assert.Equal((a.Win, a.Tie, a.Loss, a.DamageTakenP50, a.DamageDealtP50, a.LethalRisk), (b.Win, b.Tie, b.Loss, b.DamageTakenP50, b.DamageDealtP50, b.LethalRisk));
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        var snap = Snap(Board(P(4, 4), P(3, 3)), Board(P(3, 3), P(4, 4)));
        using var already = new CancellationTokenSource();
        already.Cancel();
        Assert.Throws<OperationCanceledException>(() => CombatSimulator.Run(snap, Cards, 3000, 1, already.Token));

        using var later = new CancellationTokenSource();
        later.CancelAfter(20);
        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => CombatSimulator.Run(snap, Cards, 3_000_000, 1, later.Token));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms to notice the cancellation");
    }

    // ---- performance and coverage ---------------------------------------------------------------

    [Fact]
    public void Late_game_seven_on_seven_runs_in_time()
    {
        var a = Board(
            V("BG29_611", 22, 18, MinionKeywords.DivineShield, 5), V("BGS_119", 30, 24, MinionKeywords.DivineShield | MinionKeywords.Windfury, 6),
            V("BG25_001", 25, 25, MinionKeywords.Taunt | MinionKeywords.Reborn, 4), V("BG28_300", 18, 40, MinionKeywords.Deathrattle, 3),
            V("BG25_354", 12, 30, MinionKeywords.None, 5), V("BGS_131", 20, 20, MinionKeywords.Venomous, 4), V("BGS_034", 26, 26, MinionKeywords.DivineShield, 5));
        var b = Board(
            V("BGS_034", 28, 28, MinionKeywords.DivineShield, 5), V("BG25_001", 24, 22, MinionKeywords.Taunt | MinionKeywords.Reborn, 4),
            V("BGS_119", 26, 20, MinionKeywords.DivineShield | MinionKeywords.Windfury, 6), V("BG28_300", 20, 36, MinionKeywords.Deathrattle, 3),
            V("BG29_611", 24, 24, MinionKeywords.DivineShield, 5), V("BGS_131", 22, 18, MinionKeywords.Venomous, 4), V("BG25_354", 14, 28, MinionKeywords.None, 5));
        var snap = Snap(a, b, 5, 5, 20);
        Run(snap, 300); // warm up the JIT
        var sw = Stopwatch.StartNew();
        var r = Run(snap, 3000);
        sw.Stop();
        _out.WriteLine($"7v7, 3000 iterations: {sw.Elapsed.TotalMilliseconds:F1} ms (win {r.Win:F3} tie {r.Tie:F3} loss {r.Loss:F3})");
        AssertSumsToOne(r);
#if !DEBUG
        Assert.True(sw.ElapsedMilliseconds < 300, $"{sw.ElapsedMilliseconds} ms");
#endif
    }

    [Fact]
    public void Coverage_of_the_current_pool_is_reported()
    {
        var c = SimCoverage.Compute(Cards);
        _out.WriteLine($"pool {c.Pool}; in combat {c.InCombat}; scripted {c.Scripted}; unscripted {c.Unscripted}; hard-list (always widen) {c.WidenedAnyway}");
        _out.WriteLine("scripted:   " + string.Join(", ", c.ScriptedNames));
        _out.WriteLine("unscripted: " + string.Join(", ", c.UnscriptedNames));
        Assert.Equal(Cards.PoolMinions.Count, c.Pool);
        Assert.Equal(c.InCombat, c.Scripted + c.Unscripted);
        Assert.True(c.Scripted > 0 && c.Unscripted > 0 && c.InCombat < c.Pool);
    }
}
