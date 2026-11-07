using BgCompass.Core;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.Tests;

/// <summary>The numbers of a turn as the log gives them. Tag names and card ids were read from a real game's log; these lines are synthetic.</summary>
public class TurnFactsTests
{
    const string Prefix = "D 13:01:21.0000000 ";
    const string Power = Prefix + "PowerTaskList.DebugPrintPower() -     ";
    const string Tag = Power + "    ";

    static MatchTracker Recruiting()
    {
        var log = TestData.Fixture("synthetic_game.power.log");
        int from = Array.IndexOf(log, "#CHECK ranked_ignored") + 1, to = Array.IndexOf(log, "#CHECK recruit1");
        var t = TestData.NewTracker(PowerStream.PowerTaskList);
        foreach (var l in log[from..to]) t.Feed(l);
        return t;
    }

    static void Entity(MatchTracker t, int id, string cardId, string type, int controller, params (string Tag, string Value)[] tags)
    {
        t.Feed($"{Power}FULL_ENTITY - Creating ID={id} CardID={cardId}");
        t.Feed($"{Tag}tag=CONTROLLER value={controller}");
        t.Feed($"{Tag}tag=CARDTYPE value={type}");
        t.Feed($"{Tag}tag=ZONE value=PLAY");
        foreach (var (k, v) in tags) t.Feed($"{Tag}tag={k} value={v}");
    }

    static MatchSnapshot Snap(MatchTracker t)
    {
        t.Flush();
        return t.Snapshot;
    }

    [Fact]
    public void Upgrade_cost_follows_the_newest_button_of_the_local_player()
    {
        var t = Recruiting();
        Assert.Null(Snap(t).UpgradeCost);
        Entity(t, 901, "TB_BaconShopTechUp02_Button", "GAME_MODE_BUTTON", 5, ("COST", "5"));
        Assert.Equal(5, Snap(t).UpgradeCost);
        Entity(t, 902, "TB_BaconShopTechUp03_Button", "GAME_MODE_BUTTON", 5, ("COST", "7"));
        Assert.Equal(7, Snap(t).UpgradeCost);
        Entity(t, 903, "TB_BaconShopTechUp04_Button", "GAME_MODE_BUTTON", 13, ("COST", "1"));
        Assert.Equal(7, Snap(t).UpgradeCost);
        t.Feed($"{Power}TAG_CHANGE Entity=[entityName=Tech id=902 zone=PLAY zonePos=0 cardId=TB_BaconShopTechUp03_Button player=5] tag=COST value=6 ");
        Assert.Equal(6, Snap(t).UpgradeCost);
    }

    [Fact]
    public void Refresh_cost_and_hero_tier_limit_are_read()
    {
        var t = Recruiting();
        Entity(t, 904, "TB_BaconShop_8p_Reroll_Button", "GAME_MODE_BUTTON", 5, ("COST", "1"));
        Assert.Equal(1, Snap(t).RefreshCost);
        Assert.Equal(MatchSnapshot.DefaultMaxTier, Snap(t).MaxTier);
    }

    [Fact]
    public void Damage_cap_comes_from_the_game_and_can_be_off()
    {
        var t = Recruiting();
        Assert.Null(Snap(t).DamageCap);
        t.Feed($"{Power}TAG_CHANGE Entity=GameEntity tag=BACON_COMBAT_DAMAGE_CAP value=5 ");
        Assert.Equal(5, Snap(t).DamageCap);
        t.Feed($"{Power}TAG_CHANGE Entity=GameEntity tag=BACON_COMBAT_DAMAGE_CAP value=15 ");
        Assert.Equal(15, Snap(t).DamageCap);
        t.Feed($"{Power}TAG_CHANGE Entity=GameEntity tag=BACON_COMBAT_DAMAGE_CAP_ENABLED value=0 ");
        Assert.Null(Snap(t).DamageCap);
    }

    [Fact]
    public void Empty_trinket_slots_are_not_trinkets()
    {
        var t = Recruiting();
        Entity(t, 905, "BG30_Trinket_1st", "BATTLEGROUND_TRINKET", 5);
        Entity(t, 906, "BG30_MagicItem_994", "BATTLEGROUND_TRINKET", 5);
        Entity(t, 907, "BG30_MagicItem_914", "BATTLEGROUND_TRINKET", 13);
        Assert.Equal(["BG30_MagicItem_994"], Snap(t).Trinkets);
    }

    [Fact]
    public void An_offer_is_listed_until_it_is_chosen_and_a_new_turn_clears_it()
    {
        var t = Recruiting();
        void Offer()
        {
            t.Feed(Prefix + "GameState.DebugPrintEntityChoices() - id=9 Player=Tester#1234 TaskList=70 ChoiceType=GENERAL CountMin=1 CountMax=1");
            t.Feed(Prefix + "GameState.DebugPrintEntityChoices() -   Source=[entityName=Reward id=910 zone=PLAY zonePos=0 cardId=TB_BaconShop_Triples_01 player=5]");
            t.Feed(Prefix + "GameState.DebugPrintEntityChoices() -   Entities[0]=[entityName=One id=911 zone=SETASIDE zonePos=0 cardId=BG_A player=5]");
            t.Feed(Prefix + "GameState.DebugPrintEntityChoices() -   Entities[1]=[entityName=Two id=912 zone=SETASIDE zonePos=0 cardId=BG_B player=5]");
        }
        Offer();
        Assert.Equal(["BG_A", "BG_B"], Snap(t).Choices.Select(c => c.CardId));

        t.Feed(Prefix + "GameState.DebugPrintEntitiesChosen() - id=9 Player=Tester#1234 EntitiesCount=1");
        Assert.Empty(Snap(t).Choices);

        Offer();
        Assert.Equal(2, Snap(t).Choices.Count);
        t.Feed($"{Power}TAG_CHANGE Entity=GameEntity tag=TURN value=9 ");
        Assert.Empty(Snap(t).Choices);
    }
}
