using System.Diagnostics;
using BgCompass.Core;
using BgCompass.Core.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace BgCompass.Tests;

/// <summary>
/// A whole real Battlegrounds game (build 139963, written by Hearthstone in 2022: GameState writer only), 231056 lines,
/// read from the file named by BGC_FULL_LOG. Without the variable these tests return immediately.
/// Expected values were derived independently of the C# parser: a throw-away Python script with plain regexes read the same
/// file (hero-attack BLOCK_START/BLOCK_END, META_DATA DAMAGE, CARDRACE and IS_BACON_POOL_MINION tags).
/// </summary>
public class FullLogTests(ITestOutputHelper output)
{
    private static (MatchTracker T, MatchRecord? Rec, long Ms) Run(string path)
    {
        var t = TestData.NewTracker(PowerStream.GameState);
        MatchRecord? rec = null;
        int ended = 0;
        t.GameEnded += r => { rec = r; ended++; };
        var sw = Stopwatch.StartNew();
        foreach (var l in File.ReadLines(path)) t.Feed(l);
        t.Flush();
        Assert.Equal(1, ended);
        return (t, rec, sw.ElapsedMilliseconds);
    }

    // Evidence, per round: the last ATTACK block of an even TURN whose attacker is a HERO, its first META_DATA DAMAGE
    // (python): rounds 1-5, 8, 9, 11-13, 15, 16 have one, attacker player=5 = the local player (PlayerID=5 has the
    // GameAccountId, PlayerID=13 is hi=0 lo=0). Rounds 6, 7, 10 and 14 have none -> tie.
    private static readonly (int Round, EncounterResult Result, int Damage)[] Rounds =
    [
        (1, EncounterResult.Win, 2), (2, EncounterResult.Win, 4), (3, EncounterResult.Win, 3), (4, EncounterResult.Win, 6),
        (5, EncounterResult.Win, 6), (6, EncounterResult.Tie, 0), (7, EncounterResult.Tie, 0), (8, EncounterResult.Win, 12),
        (9, EncounterResult.Win, 12), (10, EncounterResult.Tie, 0), (11, EncounterResult.Win, 11), (12, EncounterResult.Win, 12),
        (13, EncounterResult.Win, 17), (14, EncounterResult.Tie, 0), (15, EncounterResult.Win, 17), (16, EncounterResult.Win, 9),
    ];

    [Fact]
    public void Whole_game_matches_independent_evidence()
    {
        if (TestData.FullLog() is not { } path) return;
        var (t, rec, ms) = Run(path);
        output.WriteLine($"tracker time for {new FileInfo(path).Length / 1_000_000} MB: {ms} ms");
        var s = t.Snapshot;

        Assert.Equal(MatchPhase.GameOver, s.Phase);
        Assert.Equal(139963, s.BuildNumber);                     // line 150: DebugPrintGame BuildNumber=139963
        Assert.Equal(16, s.Round);                               // last TURN is 32 -> round 16
        Assert.Equal("TB_BaconShop_HERO_49_SKIN_A", s.LocalHeroCardId);   // the hero of player=5 in every attack block
        Assert.Equal(1, s.FinalPlace);                           // line 20485: own hero PLAYER_LEADERBOARD_PLACE value=1
        // Offers, as listed by the first DebugPrintEntityChoices of the game.
        Assert.Equal(["TB_BaconShop_HERO_23", "TB_BaconShop_HERO_67", "TB_BaconShop_HERO_49_SKIN_A", "BG22_HERO_003"],
            s.HeroOffers.Select(o => o.CardId));

        Assert.Equal(Rounds.Length, s.Encounters.Count);
        for (int i = 0; i < Rounds.Length; i++)
        {
            var e = s.Encounters[i];
            Assert.Equal(Rounds[i].Round, e.Round);
            Assert.Equal(Rounds[i].Result, e.Result);
            Assert.Equal(Rounds[i].Damage, e.DamageToEnemy);
            Assert.Equal(0, e.DamageToMe);
        }

        // python over all FULL/SHOW_ENTITY: pool minions (IS_BACON_POOL_MINION=1) exist for exactly these races;
        // DRAGON appears 3 times, never as a pool minion, so it must not be confirmed.
        Assert.Equal(new[] { "DEMON", "ELEMENTAL", "MURLOC", "NAGA", "PIRATE" }, s.ConfirmedTribes.Order());

        Assert.Equal(8, s.Players.Count);
        Assert.Equal(Enumerable.Range(1, 8), s.Players.Select(p => p.Place!.Value));
        Assert.Single(s.Players, p => p.IsLocal);
        Assert.Equal(1, s.Players[0].Place);
        Assert.True(s.Players[0].IsLocal);
        Assert.All(s.Players.Skip(1), p => Assert.True(p.IsDead));

        Assert.NotNull(rec);
        Assert.Equal(1, rec!.Place);
        Assert.Equal(16, rec.Rounds);
        Assert.Equal("TB_BaconShop_HERO_49_SKIN_A", rec.HeroCardId);
        Assert.Equal(139963, rec.BuildNumber);
        Assert.Equal("s_20261106T131754.2795495", rec.GameId);   // CREATE_GAME line 1: 13:17:54.2795495
        Assert.NotEmpty(rec.FinalBoardCardIds);
        Assert.Equal(s.ConfirmedTribes.Order(), rec.ConfirmedTribes.Order());
        Assert.True(ms < 15_000, $"{ms} ms");
    }

    [Fact]
    public void Re_reading_gives_the_same_game_id()
    {
        if (TestData.FullLog() is not { } path) return;
        Assert.Equal(Run(path).Rec!.GameId, Run(path).Rec!.GameId);
    }
}
