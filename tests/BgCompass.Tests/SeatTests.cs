using BgCompass.Core;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.Tests;

/// <summary>Seen in a real 2026 game: the game creates combat copies of a hero with tier 0 and fresh health, while the seat's
/// tier, armor and damage stay on the copy that holds the leaderboard place.</summary>
public class SeatTests
{
    private const string Seat = "[entityName=Opp id=111 zone=SETASIDE zonePos=0 cardId=TB_BaconShop_HERO_OPP player=13]";

    private static MatchSnapshot After(params string[] extra)
    {
        var log = TestData.Fixture("synthetic_game.power.log");
        int from = Array.IndexOf(log, "#CHECK ranked_ignored") + 1, to = Array.IndexOf(log, "#CHECK recruit1");
        var t = TestData.NewTracker(PowerStream.PowerTaskList);
        foreach (var l in log[from..to]) t.Feed(l);
        foreach (var l in extra) t.Feed("D 13:01:11.0000000 PowerTaskList.DebugPrintPower() -     " + l);
        return t.Snapshot;
    }


    [Fact]
    public void A_combat_copy_does_not_reset_the_seat_and_armor_counts()
    {
        var s = After(
            $"TAG_CHANGE Entity={Seat} tag=PLAYER_TECH_LEVEL value=3",
            $"TAG_CHANGE Entity={Seat} tag=ARMOR value=5",
            $"TAG_CHANGE Entity={Seat} tag=DAMAGE value=12",
            "FULL_ENTITY - Creating ID=900 CardID=TB_BaconShop_HERO_OPP",
            "    tag=CONTROLLER value=13", "    tag=CARDTYPE value=HERO", "    tag=ZONE value=PLAY", "    tag=PLAYER_ID value=6",
            "    tag=HEALTH value=30", "    tag=ARMOR value=0", "    tag=PLAYER_TECH_LEVEL value=0");

        var opp = Assert.Single(s.Players, p => !p.IsLocal);
        Assert.Equal(3, opp.TavernTier);
        Assert.Equal(40 - 12 + 5, opp.Health);
        Assert.False(opp.IsDead);
    }

    [Fact]
    public void Shop_size_counts_a_tavern_spell_and_hand_lists_local_minions()
    {
        var s = After(
            "FULL_ENTITY - Creating ID=901 CardID=BG_SPELL",
            "    tag=CONTROLLER value=13", "    tag=CARDTYPE value=BATTLEGROUND_SPELL", "    tag=ZONE value=PLAY", "    tag=ZONE_POSITION value=6",
            "FULL_ENTITY - Creating ID=902 CardID=BG_HELD",
            "    tag=CONTROLLER value=5", "    tag=CARDTYPE value=MINION", "    tag=ZONE value=HAND", "    tag=ZONE_POSITION value=1");

        Assert.Equal(s.Shop.Count + 1, s.ShopSize);
        Assert.Equal("BG_HELD", Assert.Single(s.Hand!).CardId);
    }

    [Fact]
    public void Hero_slots_follow_the_zone_not_the_order_the_log_lists_them()
    {
        var (c, _) = TestData.RunChecks(TestData.Fixture("synthetic_game.power.log"));
        var offers = c["heropick"].HeroOffers;
        Assert.Equal(("TB_BaconShop_HERO_B", 1), (offers[0].CardId, offers[0].ScreenSlot));
        Assert.Equal(("TB_BaconShop_HERO_A", 0), (offers[1].CardId, offers[1].ScreenSlot));
    }
}
