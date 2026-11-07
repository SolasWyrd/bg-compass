using System.Text.RegularExpressions;
using BgCompass.Core;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.Tests;

/// <summary>
/// Runs synthetic_game.power.log (hand-built, see its header) through the tracker. The file holds four games: a ranked
/// game (ignored), a full Battlegrounds game ending in place 2, a game abandoned by the next CREATE_GAME, and a duos game.
/// </summary>
public class SyntheticGameTests
{
    private static readonly string[] Log = TestData.Fixture("synthetic_game.power.log");

    private static (Dictionary<string, MatchSnapshot> C, List<MatchRecord> R) Run(PowerStream s = PowerStream.PowerTaskList) =>
        TestData.RunChecks(Log, s);

    [Fact]
    public void Non_battlegrounds_game_is_ignored()
    {
        var (c, _) = Run();
        var s = c["ranked_ignored"];
        Assert.Equal(MatchPhase.Idle, s.Phase);
        Assert.Empty(s.Players);
        Assert.Empty(s.HeroOffers);
        Assert.Empty(s.Board);
    }

    [Fact]
    public void Hero_pick_then_idle_gap_then_recruit_with_numeric_zone_tag()
    {
        var (c, _) = Run();
        Assert.Equal(MatchPhase.HeroPick, c["heropick"].Phase);
        Assert.Equal(["TB_BaconShop_HERO_B", "TB_BaconShop_HERO_A"], c["heropick"].HeroOffers.Select(o => o.CardId));
        Assert.Equal(200123, c["heropick"].BuildNumber);

        // Chosen, TURN 1 not seen yet: nothing to show, but the hero is known.
        Assert.Equal(MatchPhase.Idle, c["picked"].Phase);
        Assert.Equal("TB_BaconShop_HERO_A", c["picked"].LocalHeroCardId);

        var r = c["recruit1"];
        Assert.Equal(MatchPhase.Recruit, r.Phase);
        Assert.Equal(1, r.Round);
        Assert.Equal(2, r.Gold);                       // RESOURCES 3 - RESOURCES_USED 1
        // The minion was moved HAND -> PLAY with the numeric tag "49=1" only, never with a ZONE name.
        Assert.Equal(["BG_MINE"], r.Board.Select(m => m.CardId));
        Assert.Equal(["BG_M1", "BG_M2", "BG_TOKEN", "BG_ALL", "BG_NONE"], r.Shop.Select(m => m.CardId));
        Assert.True(r.Shop[0].Keywords.HasFlag(MinionKeywords.Taunt));
        Assert.True(r.Shop[1].Keywords.HasFlag(MinionKeywords.DivineShield));
        // Confirmed: pool minions with a real race. Not: BG_TOKEN (not a pool minion), BG_ALL (ALL), BG_NONE (no race).
        Assert.Equal(new[] { "MECHANICAL", "MURLOC", "PIRATE" }, r.ConfirmedTribes.Order());
    }

    [Fact]
    public void Doubled_writers_do_not_leak_the_outcome_before_the_first_attack()
    {
        // The GameState writer already logged the hero attack (5 damage) before the PowerTaskList writer reached combat.
        var (c, _) = Run();
        var before = c["combat_before_attack"];
        Assert.Equal(MatchPhase.Combat, before.Phase);
        Assert.Empty(before.Encounters);
        Assert.Null(before.Combat);
        Assert.Equal(40, before.Players.Single(p => !p.IsLocal).Health);   // not 35 yet
        Assert.Empty(before.Shop);                                          // the shop left play at the turn change

        // Minion attack (not a hero attack) captures the boards, and still no result.
        var during = c["combat_after_minion_attack"];
        Assert.Empty(during.Encounters);
        var cb = Assert.IsType<CombatSnapshot>(during.Combat);
        Assert.True(cb.Complete);
        Assert.Equal(1, cb.Round);
        Assert.Equal(6, cb.OpponentPlayerId);
        Assert.Equal(["BG_MINE"], cb.Friendly.Select(m => m.CardId));
        Assert.Equal(["BG_E1", "BG_E2"], cb.Enemy.Select(m => m.CardId));   // the shop minions are not in the enemy board
    }

    [Fact]
    public void Combat_without_an_attacking_value_zero_still_ends_on_the_next_turn()
    {
        var (c, _) = Run();
        var r2 = c["recruit2"];
        Assert.Equal(MatchPhase.Recruit, r2.Phase);
        Assert.Equal(2, r2.TavernTier);
        Assert.Equal(4, r2.Gold);
        var e = Assert.Single(r2.Encounters);
        Assert.Equal((1, 6, EncounterResult.Win, 0, 5), (e.Round, e.OpponentPlayerId, e.Result, e.DamageToMe, e.DamageToEnemy));
        Assert.Equal(["BG_E1", "BG_E2"], e.EnemyBoard.Select(m => m.CardId));
        var opp = r2.Players.Single(p => p.PlayerId == 6);
        Assert.Equal(35, opp.Health);
        Assert.Equal(1, opp.LastBoard!.Round);
        Assert.Null(r2.Combat);

        // Round 2 has no hero-attack block at all: a tie with no damage.
        var e2 = c["recruit3"].Encounters[1];
        Assert.Equal((2, EncounterResult.Tie, 0, 0), (e2.Round, e2.Result, e2.DamageToMe, e2.DamageToEnemy));
    }

    [Fact]
    public void Re_created_opponent_hero_keeps_its_seat()
    {
        var (c, _) = Run();
        var s = c["combat2_after_attack"];
        Assert.Equal(2, s.Players.Count);
        Assert.Contains(s.Players, p => p.PlayerId == 6);
        Assert.Equal(6, s.Combat!.OpponentPlayerId);
        Assert.Equal(["BG_E3"], s.Combat.Enemy.Select(m => m.CardId));
    }

    [Fact]
    public void Game_end_reports_place_once_and_only_for_the_real_game()
    {
        var (c, records) = Run();
        var over = c["game2_over"];
        Assert.Equal(MatchPhase.GameOver, over.Phase);
        Assert.Equal(2, over.FinalPlace);
        Assert.Equal(3, over.Encounters.Count);
        Assert.Equal((EncounterResult.Loss, 40), (over.Encounters[2].Result, over.Encounters[2].DamageToMe));

        // Ranked game ignored, game 3 abandoned by a new CREATE_GAME, game 4 never finishes: exactly one record.
        var rec = Assert.Single(records);
        Assert.Equal("s_20261106T130100.0000000", rec.GameId);
        Assert.Equal(2, rec.Place);
        Assert.Equal(3, rec.Rounds);
        Assert.Equal("TB_BaconShop_HERO_A", rec.HeroCardId);
        Assert.Equal(["BG_MINE"], rec.FinalBoardCardIds);
        Assert.Equal(200123, rec.BuildNumber);
    }

    [Fact]
    public void Abandoned_game_is_replaced_and_duos_is_detected()
    {
        var (c, _) = Run();
        Assert.Equal(MatchPhase.HeroPick, c["game3_heropick"].Phase);
        Assert.Equal(["TB_BaconShop_HERO_C"], c["game3_heropick"].HeroOffers.Select(o => o.CardId));

        var g4 = c["game4_started"];
        Assert.True(g4.IsDuos);
        Assert.Empty(g4.HeroOffers);          // nothing of the abandoned game survives
        Assert.Null(g4.LocalHeroCardId);
        Assert.False(c["game3_heropick"].IsDuos);
    }

    [Fact]
    public void Game_id_is_stable_across_re_reads_and_differs_per_session()
    {
        var a = TestData.RunChecks(Log).Records.Single().GameId;
        var b = TestData.RunChecks(Log).Records.Single().GameId;
        var other = TestData.RunChecks(Log, session: "other").Records.Single().GameId;
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void Game_state_stream_mode_reads_the_same_game_from_the_early_writer()
    {
        // The fixture's GameState stream carries only the pieces the early writer would have (no recruit boards in PLAY
        // from the screen writer's point of view), so only stream-independent facts are compared.
        var (c, _) = Run(PowerStream.GameState);
        Assert.Equal(["TB_BaconShop_HERO_B", "TB_BaconShop_HERO_A"], c["heropick"].HeroOffers.Select(o => o.CardId));
        Assert.Equal(MatchPhase.Idle, c["ranked_ignored"].Phase);
    }

    [Fact]
    public void Midnight_rollover_moves_the_end_to_the_next_day_and_keeps_the_start_in_the_id()
    {
        // Game 2 shifted so that it starts at 23:59:00 and ends after midnight.
        int from = Array.IndexOf(Log, "#CHECK ranked_ignored") + 1, to = Array.IndexOf(Log, "#CHECK game2_over");
        var shifted = Log[from..to].Select(l => Shift(l, new TimeSpan(10, 58, 0))).ToArray();
        var t = TestData.NewTracker(PowerStream.PowerTaskList);
        MatchRecord? rec = null;
        t.GameEnded += r => rec = r;
        foreach (var l in shifted) t.Feed(l);

        Assert.NotNull(rec);
        Assert.StartsWith("s_20261106T2359", rec!.GameId);
        Assert.Equal(TestData.Day.AddDays(1), DateOnly.FromDateTime(rec.EndedAt.DateTime));
        Assert.Equal(2, rec.Place);
        Assert.Equal(3, rec.Rounds);
    }

    private static string Shift(string line, TimeSpan by)
    {
        var m = Regex.Match(line, @"^D (\d\d):(\d\d):(\d\d)(\.\d+)");
        if (!m.Success) return line;
        var t = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) + by;
        t = new TimeSpan(t.Ticks % TimeSpan.TicksPerDay);
        return $"D {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}{m.Groups[4].Value}" + line[m.Length..];
    }
}
