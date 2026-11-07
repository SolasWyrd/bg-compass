using BgCompass.Core;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.Tests;

/// <summary>real_2026_heroselect.power.log is a real 2026 log cut at the hero choice (both writers, no pick yet).</summary>
public class RealFixtureTests
{
    // Read by hand from the fixture: the four DebugPrintEntityChoices entries, entity id : card id, in the order listed.
    private static readonly (int Id, string Card)[] Offers =
        [(94, "TB_BaconShop_HERO_76"), (96, "BG23_HERO_304"), (95, "TB_BaconShop_HERO_67"), (93, "BG36_HERO_105")];

    [Theory]
    [InlineData(PowerStream.PowerTaskList)]
    [InlineData(PowerStream.GameState)]
    public void Hero_offers_come_in_on_screen_order_and_phase_is_hero_pick(PowerStream stream)
    {
        var t = TestData.NewTracker(stream);
        foreach (var l in TestData.Fixture("real_2026_heroselect.power.log")) t.Feed(l);
        var s = t.Snapshot;

        Assert.Equal(MatchPhase.HeroPick, s.Phase);
        Assert.Equal(Offers.Select(o => o.Card), s.HeroOffers.Select(o => o.CardId));
        Assert.Equal(Offers.Select(o => o.Id), s.HeroOffers.Select(o => o.EntityId));
        Assert.Equal([0, 1, 2, 3], s.HeroOffers.Select(o => o.Position));
        Assert.All(s.HeroOffers, o => Assert.False(string.IsNullOrEmpty(o.Name)));
        Assert.Null(s.LocalHeroCardId);
        Assert.Null(s.FinalPlace);
        Assert.Empty(s.Board);
        Assert.Empty(s.Encounters);
    }

    [Fact]
    public void Feeding_the_same_log_twice_keeps_one_game()
    {
        var t = TestData.NewTracker(PowerStream.PowerTaskList);
        var lines = TestData.Fixture("real_2026_heroselect.power.log");
        foreach (var l in lines) t.Feed(l);
        var first = t.Snapshot.HeroOffers.Select(o => o.EntityId).ToArray();
        foreach (var l in lines) t.Feed(l);   // a second CREATE_GAME restarts the game; the offers must not double up
        Assert.Equal(first, t.Snapshot.HeroOffers.Select(o => o.EntityId));
    }
}
