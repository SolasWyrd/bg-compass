using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using Xunit;

namespace BgCompass.Tests;

/// <summary>Boards here are built from real card names. They are synthetic: no match was played to produce them.</summary>
public class TurnAdvisorTests
{
    static readonly CardCatalog Cat = Support.Cards;
    static int _id = 1000;

    static CardInfo Find(string name) => Cat.All.First(c => c.Type == "MINION" && !c.IsDuosExclusive && c.NameEn == name);

    static MinionView V(string name, int pos = 1, bool golden = false, int? cost = null)
    {
        var c = Find(name);
        return new(_id++, c.Id, c.Name, c.Attack ?? 0, c.Health ?? 0, pos, golden, MinionKeywords.None, c.Tribes, c.Tier ?? 1, cost ?? 3);
    }

    static IReadOnlyList<MinionView> Row(params string[] names) => names.Select((n, i) => V(n, i + 1)).ToList();

    static MatchSnapshot Turn(IReadOnlyList<MinionView> board, IReadOnlyList<MinionView> shop, int gold = 10, int round = 8, int tier = 4) =>
        MatchSnapshot.Empty with
        {
            Phase = MatchPhase.Recruit, Round = round, TavernTier = tier, Gold = gold, Board = board, Shop = shop, ShopSize = shop.Count,
            RefreshCost = 1,
            Players = [new PlayerView(1, "H", "Hero", true, 1, tier, 30, 0, false, false, null)],
        };

    static CardInfo InPool(Func<CardInfo, bool> test) => Cat.All.First(c => c.Type == "MINION" && !c.IsDuosExclusive && c.Tier is > 0 && test(c));

    static IReadOnlyList<MinionView> Tribe(string tribe, int n) =>
        Cat.All.Where(c => c.Type == "MINION" && !c.IsDuosExclusive && c.Tribes.SequenceEqual([tribe]) && c.Tier is > 0).Take(n)
            .Select((c, i) => new MinionView(_id++, c.Id, c.Name, c.Attack ?? 0, c.Health ?? 0, i + 1, false, MinionKeywords.None, c.Tribes, c.Tier ?? 1)).ToList();

    static MinionView OneOf(string tribe, IEnumerable<MinionView> not, int pos = 1)
    {
        var c = Cat.All.First(x => x.Type == "MINION" && !x.IsDuosExclusive && x.Tribes.SequenceEqual([tribe]) && x.Tier is > 0 && not.All(n => n.CardId != x.Id));
        return new(_id++, c.Id, c.Name, c.Attack ?? 0, c.Health ?? 0, pos, false, MinionKeywords.None, c.Tribes, c.Tier ?? 1);
    }

    [Fact]
    public void Undead_board_takes_undead_and_leaves_unrelated_battlecries()
    {
        var board = Tribe("UNDEAD", 5);
        var pirate = InPool(c => c.Tribes.SequenceEqual(["PIRATE"]) && c.Mechanics.Contains("BATTLECRY"));
        var undead = OneOf("UNDEAD", board, 2);
        var shop = new[]
        {
            new MinionView(_id++, pirate.Id, pirate.Name, pirate.Attack ?? 0, pirate.Health ?? 0, 1, false, MinionKeywords.None, pirate.Tribes, pirate.Tier ?? 1),
            undead,
        };
        var plan = TurnAdvisor.Plan(Turn(board, shop), Cat);
        Assert.Equal("UNDEAD", plan.MainTribe);
        Assert.Equal(5, plan.MainTribeCount);
        Assert.Contains(plan.Picks, p => p.CardId == undead.CardId);
        Assert.DoesNotContain(plan.Picks, p => p.CardId == pirate.Id);
    }

    [Fact]
    public void Advisor_does_not_call_an_undead_board_a_battlecry_build()
    {
        var board = Tribe("UNDEAD", 5);
        var advice = BoardAdvisor.Advise(Turn(board, []), Cat, new HashSet<string>());
        Assert.Equal("undead", advice.Directions[0].Id);
        Assert.DoesNotContain(advice.Directions, d => d.Id == "battlecry");
    }

    [Fact]
    public void Aberration_board_values_deity_cards_and_a_deity_card_is_dead_without_aberrations()
    {
        var payoff = InPool(c => c.TextEn.Contains("Deity") && c.Tribes.Contains("ABERRATION"));
        var offer = new MinionView(_id++, payoff.Id, payoff.Name, payoff.Attack ?? 0, payoff.Health ?? 0, 1, false, MinionKeywords.None, payoff.Tribes, payoff.Tier ?? 1);
        var aberrations = Cat.All.Where(c => c.Type == "MINION" && !c.IsDuosExclusive && c.Tribes.Contains("ABERRATION") && c.Tier is > 0 && c.Id != payoff.Id).Take(4)
            .Select((c, i) => new MinionView(_id++, c.Id, c.Name, c.Attack ?? 0, c.Health ?? 0, i + 1, false, MinionKeywords.None, c.Tribes, c.Tier ?? 1)).ToList();
        var pick = Assert.Single(TurnAdvisor.Plan(Turn(aberrations, [offer]), Cat).Picks);
        Assert.Equal(payoff.Id, pick.CardId);
        Assert.Contains("божество", pick.Why);

        Assert.Empty(TurnAdvisor.Plan(Turn(Tribe("MURLOC", 5), [offer]), Cat).Picks);
    }

    [Fact]
    public void Picks_fit_the_gold_and_the_board_slots()
    {
        var board = Tribe("MURLOC", 4);
        var shop = Tribe("MURLOC", 7).Skip(4).Select((c, i) => c with { Position = i + 1 }).ToList();
        Assert.Empty(TurnAdvisor.Plan(Turn(board, shop, gold: 2), Cat).Picks);
        Assert.Single(TurnAdvisor.Plan(Turn(board, shop, gold: 3), Cat).Picks);
        Assert.Equal(3, TurnAdvisor.Plan(Turn(board, shop, gold: 9), Cat).Picks.Count);
        // One free slot: the rest of the gold has nowhere to go.
        var nearlyFull = Tribe("MURLOC", 6);
        var one = Tribe("MURLOC", 9).Skip(6).Select((c, i) => c with { Position = i + 1 }).ToList();
        var tight = TurnAdvisor.Plan(Turn(nearlyFull, one, gold: 9), Cat);
        Assert.Equal(tight.Sell is null ? 1 : 2, tight.Picks.Count);
    }

    [Fact]
    public void A_third_copy_comes_first_and_only_once_and_golden_copies_do_not_count()
    {
        var a = Tribe("MURLOC", 1)[0];
        var other = OneOf("BEAST", []);
        var board = new[] { a, a with { EntityId = _id++, Position = 2 } };
        var shop = new[] { a with { EntityId = _id++, Position = 1 }, a with { EntityId = _id++, Position = 2 }, other with { Position = 3 } };
        var plan = TurnAdvisor.Plan(Turn(board, shop, gold: 3), Cat);
        var pick = Assert.Single(plan.Picks);
        Assert.Equal(PickKind.Triple, pick.Kind);
        Assert.Equal(a.CardId, pick.CardId);

        var golden = new[] { a with { Golden = true }, a with { EntityId = _id++, Position = 2 } };
        Assert.DoesNotContain(TurnAdvisor.Plan(Turn(golden, shop, gold: 3), Cat).Picks, p => p.Kind == PickKind.Triple);
    }

    [Fact]
    public void Upgrade_needs_the_gold_stops_at_the_last_tier_and_uses_the_hero_limit()
    {
        var board = Tribe("MURLOC", 3);
        var behind = Turn(board, [], gold: 9, round: 8, tier: 3) with { UpgradeCost = 9 };
        Assert.True(TurnAdvisor.Plan(behind, Cat).Upgrade);
        Assert.False(TurnAdvisor.Plan(behind with { Gold = 8 }, Cat).Upgrade);
        Assert.False(TurnAdvisor.Plan(behind with { UpgradeCost = null }, Cat).Upgrade);
        Assert.False(TurnAdvisor.Plan(behind with { TavernTier = 6, MaxTier = 6 }, Cat).Upgrade);
        Assert.True(TurnAdvisor.Plan(behind with { TavernTier = 6, MaxTier = 7, Round = 20 }, Cat).Upgrade);
    }

    [Fact]
    public void Low_health_buys_the_board_before_the_tavern()
    {
        var board = Tribe("MURLOC", 4);
        var shop = Tribe("MURLOC", 5).Skip(4).Select(c => c with { Position = 1 }).ToList();
        var m = Turn(board, shop, gold: 9, round: 8, tier: 3) with { UpgradeCost = 9 };
        Assert.True(TurnAdvisor.Plan(m, Cat).Upgrade);
        var low = m with { Players = [new PlayerView(1, "H", "Hero", true, 1, 3, 6, 0, false, false, null)] };
        var plan = TurnAdvisor.Plan(low, Cat);
        Assert.False(plan.Upgrade);
        Assert.Single(plan.Picks);
    }

    [Fact]
    public void Damage_counts_tiers_and_stops_at_the_cap()
    {
        var mine = new[] { V("Risen Rider") with { TechLevel = 2 }, V("Risen Rider") with { TechLevel = 5 } };
        var enemy = new PlayerView(2, "E", "Enemy", false, 2, 5, 20, 0, false, true, new LastSeenBoard(5, [mine[1], mine[1]]));
        var m = Turn(mine, [], round: 8, tier: 4) with { Players = [new PlayerView(1, "H", "Hero", true, 1, 4, 12, 0, false, false, null), enemy] };

        var open = TurnAdvisor.Plan(m, Cat).Damage;
        Assert.Equal(11, open.Dealt);
        Assert.Equal(15, open.Taken);
        Assert.Equal(3, open.TakenBoardAge);

        var capped = TurnAdvisor.Plan(m with { DamageCap = 10 }, Cat).Damage;
        Assert.Equal(10, capped.Dealt);
        Assert.Equal(11, capped.DealtUncapped);
        Assert.Equal(10, capped.Taken);
        Assert.Equal(15, capped.TakenUncapped);
    }

    [Fact]
    public void A_choice_names_what_suits_the_board_and_stays_silent_on_a_coin_flip()
    {
        var board = Tribe("UNDEAD", 5);
        var undead = OneOf("UNDEAD", board);
        var beast = OneOf("BEAST", []);
        var m = Turn(board, []) with { Choices = [new(1, beast.CardId, beast.Name), new(2, undead.CardId, undead.Name)] };
        Assert.Equal(undead.CardId, TurnAdvisor.Plan(m, Cat).Choice?.CardId);

        Assert.Null(TurnAdvisor.Plan(m with { Choices = [new(1, undead.CardId, undead.Name), new(2, undead.CardId, undead.Name)] }, Cat).Choice);
        Assert.Null(TurnAdvisor.Plan(m with { Choices = [new(1, undead.CardId, undead.Name), new(2, "NOT_A_CARD", "?")] }, Cat).Choice);
    }

    [Fact]
    public void Early_game_takes_the_strongest_body_when_there_is_no_build_yet()
    {
        var shop = Tribe("MURLOC", 1).Concat(Tribe("BEAST", 1)).Select((c, i) => c with { Position = i + 1 }).ToList();
        var plan = TurnAdvisor.Plan(Turn([], shop, gold: 3, round: 1, tier: 1) with { UpgradeCost = 5 }, Cat);
        Assert.Single(plan.Picks);
    }
}
