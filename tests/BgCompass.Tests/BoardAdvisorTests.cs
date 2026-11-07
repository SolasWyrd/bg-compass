using BgCompass.Core;
using BgCompass.Core.Advice;
using Xunit;
using static BgCompass.Tests.Support;

namespace BgCompass.Tests;

// BOOKED: scenarios checked by hand against the card texts of the embedded card data (build 256589).
//  1. Murloc: Bile Spitter (Venomous Rally: give a Murloc Venomous) + Primalfin Lookout (Discover a Murloc, battlecry) +
//     Bubble Gunner (gains a Bonus Keyword) + Tad -> murloc leads; Hackerfin in the shop is a core murloc card.
//  2. Magnetic mechs: Lullabot, Red Volumizer, Annoy-o-Module (all Magnetic) + Mechagnome Interpreter -> mech leads.
//  3. Undead: Risen Rider (Reborn), Scarlet Skull (Reborn Deathrattle), Mummifier, Cadaver Caretaker (Deathrattles) -> undead leads.
//  4. Naga: four Spellcraft nagas -> naga leads, Waverider's text names Naga so the synergy line fires.
//  5. Quilboar: Geomancer, Tusked Camper, Roadboar, Prodigious Tusker (all Blood Gem) -> quilboar leads.
//  6. Mixed board (one mech, murloc, undead, naga) -> no direction reaches ClearDirection and the text says so.
//  7. Excluded QUILBOAR: no direction for quilboar and no suggested card that is only quilboar.
//  8. Shop flag: only cards actually in the shop are marked InShop.
//  9. Empty board and a card id the data does not know: no exception, a Limit names the problem.
// 10. Charging Czarina (gives Divine Shield minions +4 Attack) next to Annoy-o-Module -> shield synergy names both.
// 11. Falling Sky Golem (+stats per Deathrattle triggered) next to Cadaver Caretaker -> deathrattle synergy.
// 12. Stitched Salvager ("Destroy the minion to the left") in slot 2 -> positioning names the minion on its left.
public class BoardAdvisorTests
{
    static BoardAdvice Advise(MatchSnapshot s, params string[] excluded) =>
        BoardAdvisor.Advise(s, Cards, new HashSet<string>(excluded));

    static string AllText(BoardAdvice a) => string.Join("\n",
        a.Directions.SelectMany(d => d.Lines).Concat(a.Synergies).Concat(a.Gaps).Concat(a.Positioning).Concat(a.Limits).Select(l => l.Text));

    [Fact]
    public void MurlocVenomAndBattlecriesLeadToMurloc()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout", "Tad", "Bubble Gunner"], 6, 5, ["Hackerfin"], "MURLOC"));
        var lead = a.Directions[0];
        Assert.Equal("murloc", lead.Id);
        Assert.True(lead.Fit >= Archetypes.ClearDirection);
        Assert.Contains(lead.HaveCore, c => c.CardId == Card("Bile Spitter").Id && c.OnBoard);
        Assert.Equal(4, a.TribeCounts["MURLOC"]);
        Assert.DoesNotContain(a.Limits, l => l.Text.Contains("Явного направления"));
    }

    [Fact]
    public void ShopCardThatFitsIsFlaggedAndNothingElseIs()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout", "Tad"], 6, 5, ["Hackerfin", "Lullabot"], "MURLOC"));
        var hackerfin = Card("Hackerfin").Id;
        Assert.Contains(a.Directions[0].MissingCoreNow, c => c.CardId == hackerfin && c.InShop);
        Assert.Contains(a.Directions[0].Lines, l => l.Source == AdviceSource.Fact && l.Text.Contains(Card("Hackerfin").Name));
        var shop = new[] { hackerfin, Card("Lullabot").Id };
        foreach (var d in a.Directions)
            Assert.All(d.MissingCoreNow.Concat(d.NextTierTargets).Where(c => c.InShop), c => Assert.Contains(c.CardId, shop));
    }

    [Fact]
    public void MagneticMechsLeadToMech()
    {
        var a = Advise(Snapshot(["Lullabot", "Red Volumizer", "Mechagnome Interpreter", "Annoy-o-Module"], 5, 4, null, "MECHANICAL"));
        Assert.Equal("mech", a.Directions[0].Id);
        Assert.True(a.Directions[0].Fit >= 0.5);
    }

    [Fact]
    public void RebornAndDeathrattleUndeadLeadToUndead()
    {
        var a = Advise(Snapshot(["Risen Rider", "Scarlet Skull", "Mummifier", "Cadaver Caretaker"], 6, 4, null, "UNDEAD"));
        Assert.Equal("undead", a.Directions[0].Id);
    }

    [Fact]
    public void SpellcraftNagasLeadToNagaAndTextSynergyNamesTheTribe()
    {
        var a = Advise(Snapshot(["Mini-Myrmidon", "Thaumaturgist", "Waverider", "Deep-Sea Angler"], 5, 4, null, "NAGA"));
        Assert.Equal("naga", a.Directions[0].Id);
        Assert.Contains(a.Synergies, l => l.Source == AdviceSource.Fact && l.Text.Contains(Card("Waverider").Name));
    }

    [Fact]
    public void BloodGemQuilboarLeadToQuilboar()
    {
        var a = Advise(Snapshot(["Razorfen Geomancer", "Tusked Camper", "Roadboar", "Prodigious Tusker"], 4, 3, null, "QUILBOAR"));
        Assert.Equal("quilboar", a.Directions[0].Id);
    }

    [Fact]
    public void MixedBoardHasNoClearDirectionAndSaysSo()
    {
        var a = Advise(Snapshot(["Lullabot", "Tad", "Risen Rider", "Mini-Myrmidon"], 4, 3));
        Assert.All(a.Directions, d => Assert.True(d.Fit < Archetypes.ClearDirection));
        Assert.Contains(a.Limits, l => l.Source == AdviceSource.Limit && l.Text.Contains("Явного направления"));
        Assert.Contains(a.Limits, l => l.Text.Contains("не замечено ни одного племени"));
        Assert.Contains(a.Limits, l => l.Text.Contains("Не подтверждены в лобби"));
    }

    [Fact]
    public void FitIsAlwaysDescribedAsCoverageNotAWinChance()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout"], 6, 5, null, "MURLOC"));
        Assert.All(a.Directions, d => Assert.Contains("Это не шанс на победу", d.Lines[0].Text));
        Assert.All(a.Directions, d => Assert.Equal(AdviceSource.Fact, d.Lines[0].Source));
    }

    [Fact]
    public void ExcludedTribeRemovesItsDirectionAndItsOnlyCards()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout", "Roadboar"], 6, 5, ["Tusked Camper"], "MURLOC", "QUILBOAR"), "QUILBOAR");
        Assert.DoesNotContain(a.Directions, d => d.Tribe == "QUILBOAR" || d.Id == "quilboar");
        foreach (var r in a.Directions.SelectMany(d => d.MissingCoreNow.Concat(d.NextTierTargets)))
            Assert.False(Cards.Get(r.CardId)!.Tribes is ["QUILBOAR"], $"{r.Name} is quilboar-only");
        Assert.Contains(a.Limits, l => l.Text.Contains("исключённых"));
    }

    [Fact]
    public void ExcludedTribeFamilyIsNeverTheLead()
    {
        var a = Advise(Snapshot(["Lullabot", "Red Volumizer", "Mechagnome Interpreter"], 5, 4, null, "MECHANICAL"), "MECHANICAL");
        Assert.DoesNotContain(a.Directions, d => d.Id == "mech");
    }

    [Fact]
    public void DuosOnlyCardsAreNeverSuggested()
    {
        var duos = Cards.PoolMinions.Where(c => c.IsDuosExclusive).Select(c => c.Id).ToHashSet();
        Assert.NotEmpty(duos);
        foreach (var board in new[] { new[] { "Bile Spitter", "Tad" }, ["Lullabot", "Red Volumizer"], ["Risen Rider", "Mummifier"] })
        {
            var a = Advise(Snapshot(board, 8, 6));
            Assert.All(a.Directions.SelectMany(d => d.MissingCoreNow.Concat(d.NextTierTargets)), r => Assert.DoesNotContain(r.CardId, duos));
        }
    }

    [Fact]
    public void SuggestedTiersFollowTheTavernTier()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Tad"], 6, 4, null, "MURLOC"));
        Assert.All(a.Directions.SelectMany(d => d.MissingCoreNow), r => Assert.True(r.Tier <= 4));
        Assert.All(a.Directions.SelectMany(d => d.NextTierTargets), r => Assert.InRange(r.Tier, 5, 6));
    }

    [Fact]
    public void EmptyBoardDoesNotThrowAndSaysSo()
    {
        var a = Advise(Snapshot([], 3, 2));
        Assert.Empty(a.Directions);
        Assert.Empty(a.TribeCounts);
        Assert.Empty(a.Positioning);
        Assert.Contains(a.Limits, l => l.Text.Contains("Стол пуст"));
        Assert.NotNull(Advise(MatchSnapshot.Empty));
    }

    [Fact]
    public void UnknownCardIsNamedAndIgnored()
    {
        var s = Snapshot(["Bile Spitter", "Primalfin Lookout"], 6, 5, null, "MURLOC");
        var ghost = new MinionView(7, "NOPE_999", "Призрак", 3, 3, 3, false, MinionKeywords.None, ["MURLOC"], 3);
        var a = Advise(s with { Board = [.. s.Board, ghost] });
        Assert.Equal("murloc", a.Directions[0].Id);
        Assert.Contains(a.Limits, l => l.Text.Contains("Призрак"));
        Assert.Equal(2, a.TribeCounts["MURLOC"]);
    }

    [Fact]
    public void UnknownTavernTierIsAdmittedAndSuggestionsStillWork()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Tad"], 6, null, null, "MURLOC"));
        Assert.Contains(a.Limits, l => l.Text.Contains("Уровень таверны"));
        Assert.NotEmpty(a.Directions);
    }

    [Fact]
    public void DivineShieldPayoffNamesTheShieldedMinions()
    {
        var a = Advise(Snapshot(["Charging Czarina", "Annoy-o-Module", "Cord Puller"], 7, 5, null, "MECHANICAL"));
        var line = Assert.Single(a.Synergies, l => l.Text.Contains(Card("Charging Czarina").Name) && l.Text.Contains("щит"));
        Assert.Equal(AdviceSource.Fact, line.Source);
        Assert.Contains(Card("Annoy-o-Module").Name, line.Text);
        Assert.Contains(Card("Cord Puller").Name, line.Text);
    }

    [Fact]
    public void DeathrattlePayoffNamesTheDeathrattleMinions()
    {
        var a = Advise(Snapshot(["Falling Sky Golem", "Cadaver Caretaker"], 9, 6, null, "MECHANICAL", "UNDEAD"));
        Assert.Contains(a.Synergies, l => l.Text.Contains(Card("Falling Sky Golem").Name) && l.Text.Contains(Card("Cadaver Caretaker").Name));
    }

    [Fact]
    public void PositioningQuotesCardTextAndAdmitsItIsNotACalculation()
    {
        var a = Advise(Snapshot(["Risen Rider", "Stitched Salvager", "Gormling Gourmet"], 9, 6, null, "UNDEAD"));
        Assert.Contains(a.Positioning, l => l.Source == AdviceSource.Fact
            && l.Text.Contains(Card("Stitched Salvager").Name) && l.Text.Contains(Card("Risen Rider").Name));
        Assert.All(a.Positioning.SkipLast(1), l => Assert.Equal(AdviceSource.Fact, l.Source));
        Assert.Equal(AdviceSource.Limit, a.Positioning[^1].Source);
        Assert.Contains("не расчёт", a.Positioning[^1].Text);
    }

    [Fact]
    public void GapsAreRulesPhrasedAsUsualAndNameTheRound()
    {
        var a = Advise(Snapshot(["Lullabot"], 9, 3, null, "MECHANICAL"));
        Assert.NotEmpty(a.Gaps);
        Assert.All(a.Gaps, l =>
        {
            Assert.Equal(AdviceSource.Rule, l.Source);
            Assert.Contains("обычно", l.Text);
            Assert.Contains("9", l.Text);
        });
    }

    [Fact]
    public void EarlyRoundsHaveNoGaps()
    {
        Assert.Empty(Advise(Snapshot(["Lullabot"], 3, 2, null, "MECHANICAL")).Gaps);
    }

    [Fact]
    public void LateBoardWithoutTauntShieldOrRebornGetsTheProtectionRule()
    {
        var a = Advise(Snapshot(["Tad", "Bubble Gunner", "Hackerfin"], 9, 5, null, "MURLOC"));
        Assert.Contains(a.Gaps, l => l.Text.Contains("провокация"));
        var protectedBoard = Advise(Snapshot(["Tad", "Risen Rider", "Hackerfin"], 9, 5, null, "MURLOC"));
        Assert.DoesNotContain(protectedBoard.Gaps, l => l.Text.Contains("провокация"));
    }

    [Fact]
    public void TransitionsAreRulesOnTheLeadingDirection()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout", "Tad", "Bubble Gunner"], 6, 5, null, "MURLOC"));
        var shared = a.Directions[0].Lines.Where(l => l.Text.Contains("Обычно между такими направлениями")).ToList();
        Assert.NotEmpty(shared);
        Assert.All(shared, l => Assert.Equal(AdviceSource.Rule, l.Source));
    }

    [Fact]
    public void NoSourceIsMislabelled()
    {
        var a = Advise(Snapshot(["Bile Spitter", "Primalfin Lookout", "Tad"], 9, 3, ["Hackerfin"]));
        Assert.All(a.Synergies, l => Assert.Equal(AdviceSource.Fact, l.Source));
        Assert.All(a.Limits, l => Assert.Equal(AdviceSource.Limit, l.Source));
        Assert.All(a.Directions.SelectMany(d => d.Lines).Where(l => l.Text.StartsWith("План:")), l =>
        {
            Assert.Equal(AdviceSource.Rule, l.Source);
            Assert.Contains("эвристика", l.Text);
        });
        Assert.DoesNotContain("победа", AllText(a).Replace("шанс на победу", ""));
    }
}
