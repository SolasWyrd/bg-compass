using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;
using Xunit;
using static BgCompass.Tests.Support;

namespace BgCompass.Tests;

// BOOKED (hero powers and armor read from the embedded card data, build 256589):
//  1. Trade Prince Gallywix (armor 5) vs Chenvaala (armor 15): 10 armor apart -> armor rule recommends Gallywix, labelled Rule.
//  2. Ysera (17) vs Chenvaala (15): 2 apart, below the 3 threshold -> no recommendation, a Limit line.
//  3. Ini Stormcoil ("get a random Mech") vs Ysera with MECHANICAL excluded -> Ini is dropped, Ysera stays (Rule).
//  4. Ysera ("extra Dragon") shows tribe dependence three ways: DRAGON confirmed / not seen yet / excluded.
//  5. Personal stats: n >= 5 for both and a gap >= 1.0 place wins over the armor rule; n = 4 or a 0.5 gap does not.
//  6. A hero skin is not in the snapshot's catalog, so a synthetic catalog adds one pointing at Gallywix.
public class HeroAdvisorTests
{
    static readonly IReadOnlySet<string> Dragon = new HashSet<string> { "DRAGON" };
    static IReadOnlyDictionary<string, HeroStats> NoStats => new Dictionary<string, HeroStats>();

    static HeroPickAdvice Advise(HeroOption[] offers, CardCatalog? cards = null, IReadOnlySet<string>? confirmed = null,
        IReadOnlySet<string>? excluded = null, IReadOnlyDictionary<string, HeroStats>? personal = null) =>
        HeroAdvisor.Advise(offers, cards ?? Cards, confirmed ?? None, excluded ?? None, personal ?? NoStats);

    [Fact]
    public void ArmorRuleRecommendsTheLowArmorHeroAndSaysItIsAHeuristic()
    {
        var advice = Advise([Hero("Trade Prince Gallywix", 1), Hero("Chenvaala", 2)]);
        Assert.Equal(HeroId("Trade Prince Gallywix"), advice.RecommendedCardId);
        Assert.Equal(AdviceSource.Rule, advice.RecommendationReason!.Source);
        Assert.Contains("Blizzard", advice.RecommendationReason.Text);
        Assert.Contains("не статистика", advice.RecommendationReason.Text);
    }

    [Fact]
    public void SmallArmorGapRecommendsNothingAndSaysWhy()
    {
        var advice = Advise([Hero("Ysera", 1), Hero("Chenvaala", 2)]);
        Assert.Null(advice.RecommendedCardId);
        Assert.Equal(AdviceSource.Limit, advice.RecommendationReason!.Source);
    }

    [Fact]
    public void HeroLinesCarryFactsRulesAndAPersonalLine()
    {
        var advice = Advise([Hero("Trade Prince Gallywix", 1), Hero("Chenvaala", 2)]);
        var lines = advice.Heroes[0].Lines;
        Assert.Contains(lines, l => l.Source == AdviceSource.Fact && l.Text.StartsWith("Сила героя"));
        Assert.Contains(lines, l => l.Source == AdviceSource.Fact && l.Text == "Броня: 5");
        Assert.Contains(lines, l => l.Source == AdviceSource.Rule && l.Text.Contains("06.11.2026"));
        Assert.Contains(lines, l => l.Source == AdviceSource.Personal && l.Text.Contains("пока нет"));
        Assert.Equal(5, advice.Heroes[0].Armor);
        Assert.False(string.IsNullOrWhiteSpace(advice.Heroes[0].HeroPowerText));
    }

    [Fact]
    public void ExcludedTribeDropsTheHeroThatNeedsIt()
    {
        var advice = Advise([Hero("Ini Stormcoil", 1), Hero("Ysera", 2)], excluded: new HashSet<string> { "MECHANICAL" });
        Assert.Equal(HeroId("Ysera"), advice.RecommendedCardId);
        Assert.Equal(AdviceSource.Rule, advice.RecommendationReason!.Source);
        Assert.Contains(advice.Heroes[0].Lines, l => l.Text.Contains("исключено"));
    }

    [Fact]
    public void TribeDependenceIsConfirmedNotSeenOrExcluded()
    {
        var ysera = new[] { Hero("Ysera") };
        Assert.Contains(Advise(ysera, confirmed: Dragon).Heroes[0].Lines, l => l.Source == AdviceSource.Fact && l.Text.Contains("уже замечено"));
        Assert.Contains(Advise(ysera).Heroes[0].Lines, l => l.Source == AdviceSource.Limit && l.Text.Contains("пока не замечено"));
        Assert.Contains(Advise(ysera, excluded: Dragon).Heroes[0].Lines, l => l.Text.Contains("исключено"));
    }

    [Fact]
    public void PersonalStatsWinWhenBothHeroesHaveEnoughGamesAndARealGap()
    {
        var gally = HeroId("Trade Prince Gallywix");
        var chen = HeroId("Chenvaala");
        var personal = new Dictionary<string, HeroStats>
        {
            [gally] = new(6, 5.2, 0.3, 0),
            [chen] = new(8, 3.1, 0.7, 2),
        };
        var advice = Advise([Hero("Trade Prince Gallywix", 1), Hero("Chenvaala", 2)], personal: personal);
        Assert.Equal(chen, advice.RecommendedCardId);
        Assert.Equal(AdviceSource.Personal, advice.RecommendationReason!.Source);
        Assert.Contains("8 игр", advice.RecommendationReason.Text);
        Assert.Equal(8, advice.Heroes[1].PersonalGames);
        Assert.Equal(3.1, advice.Heroes[1].PersonalAveragePlace);
    }

    [Theory]
    [InlineData(4, 6, 1.5)]  // one hero below five games
    [InlineData(6, 6, 0.5)]  // gap below one place
    public void PersonalStatsAreIgnoredWhenTheSampleOrTheGapIsTooSmall(int gamesA, int gamesB, double gap)
    {
        var gally = HeroId("Trade Prince Gallywix");
        var chen = HeroId("Chenvaala");
        var personal = new Dictionary<string, HeroStats>
        {
            [gally] = new(gamesA, 5.0, 0.3, 0),
            [chen] = new(gamesB, 5.0 - gap, 0.7, 1),
        };
        var advice = Advise([Hero("Trade Prince Gallywix", 1), Hero("Chenvaala", 2)], personal: personal);
        // The armor rule still decides, and it is not presented as personal.
        Assert.Equal(gally, advice.RecommendedCardId);
        Assert.Equal(AdviceSource.Rule, advice.RecommendationReason!.Source);
        if (gamesA < 5) Assert.Contains(advice.Heroes[0].Lines, l => l.Source == AdviceSource.Personal && l.Text.Contains("выборка мала"));
    }

    [Fact]
    public void HeroSkinResolvesToTheBaseHeroForDataAndStats()
    {
        var gally = Cards.All.First(c => c.IsBgHero && c.NameEn == "Trade Prince Gallywix" && c.SkinParentDbfId is null);
        var skin = gally with { Id = "SKIN_GALLYWIX_X", DbfId = 99999001, SkinParentDbfId = gally.DbfId, HeroPowerDbfId = null };
        var catalog = new CardCatalog([.. Cards.All, skin], Cards.Version);
        var offers = new[] { new HeroOption(1, skin.Id, "Скин", 1), Hero("Chenvaala", 2) };
        var personal = new Dictionary<string, HeroStats>
        {
            [gally.Id] = new(7, 2.4, 0.9, 3),
            [HeroId("Chenvaala")] = new(7, 4.9, 0.4, 0),
        };

        var advice = Advise(offers, catalog, personal: personal);

        Assert.Equal(5, advice.Heroes[0].Armor);
        Assert.False(string.IsNullOrWhiteSpace(advice.Heroes[0].HeroPowerText));
        Assert.Equal(7, advice.Heroes[0].PersonalGames);
        Assert.Equal(skin.Id, advice.RecommendedCardId);
    }

    [Fact]
    public void UnknownHeroShowsTheLogNameWithALimitAndNeverThrows()
    {
        var unknown = new HeroOption(5, "NOPE_HERO_1", "Незнакомец", 1);
        var advice = Advise([unknown, Hero("Ysera", 2)]);
        var line = Assert.Single(advice.Heroes[0].Lines);
        Assert.Equal(AdviceSource.Limit, line.Source);
        Assert.Contains("Незнакомец", line.Text);
        Assert.Null(advice.RecommendedCardId);
        Assert.NotNull(Advise([]));
        Assert.NotNull(Advise([unknown]));
    }

    [Fact]
    public void TipsCoverEveryBaseHeroButTwoAndHaveNoStaleIds()
    {
        var baseHeroes = Cards.All.Where(c => c.IsBgHero && c.SkinParentDbfId is null).Select(c => c.Id).ToHashSet();
        Assert.Equal(121, HeroTips.Default.Count);
        Assert.All(HeroTips.Default.Ids, id => Assert.Contains(id, baseHeroes));
        Assert.Equal(["BG36_HERO_000", "BG36_HERO_002"], baseHeroes.Except(HeroTips.Default.Ids).Order());
    }
}
