using BgCompass.Core.Advice;
using Xunit;
using static BgCompass.Tests.Support;

namespace BgCompass.Tests;

// BOOKED: families are checked against the live pool, not against a hand-kept card list.
public class ArchetypeTests
{
    static readonly Archetypes Arch = Archetypes.LoadEmbedded();

    [Fact]
    public void FamiliesAreUniqueAndDated()
    {
        Assert.Equal(15, Arch.Families.Count);
        Assert.Equal(Arch.Families.Count, Arch.Families.Select(f => f.Id).Distinct().Count());
        Assert.All(Arch.Families, f =>
        {
            Assert.NotEmpty(f.Core);
            Assert.True(DateOnly.TryParseExact(f.Updated, "yyyy-MM-dd", out _));
            Assert.False(string.IsNullOrWhiteSpace(f.Plan));
        });
    }

    [Fact]
    public void EveryRoleMatchesAtLeastOneSoloPoolMinion()
    {
        var pool = Cards.PoolMinions.Where(c => !c.IsDuosExclusive).ToList();
        foreach (var f in Arch.Families)
            foreach (var r in f.Core.Concat(f.Support))
                Assert.True(pool.Any(c => Archetypes.Matches(f, r, c)), $"{f.Id}/{r.Id} matches no pool minion");
    }

    [Fact]
    public void EveryTribeFamilyUsesATribeTheDataKnows()
    {
        var tribes = Cards.PoolMinions.SelectMany(c => c.Tribes).ToHashSet();
        Assert.All(Arch.Families.Where(f => f.Tribe is not null), f => Assert.Contains(f.Tribe!, tribes));
    }

    [Fact]
    public void MostPoolMinionsBelongToSomeFamily()
    {
        var pool = Cards.PoolMinions.ToList();
        var covered = pool.Count(c => Arch.Families.Any(f => Archetypes.IsCore(f, c) || f.Support.Any(r => Archetypes.Matches(f, r, c))));
        Assert.True(covered >= pool.Count * 0.85, $"only {covered} of {pool.Count} pool minions are in a family");
    }

    [Fact]
    public void FitGrowsWithCoverageAndStaysInRange()
    {
        var murloc = Arch.Get("murloc")!;
        var one = Arch.Cover(murloc, [Card("Bile Spitter")]).Fit;
        var more = Arch.Cover(murloc, [Card("Bile Spitter"), Card("Primalfin Lookout"), Card("Bubble Gunner")]).Fit;
        Assert.InRange(one, 0.01, 1);
        Assert.InRange(more, one, 1);
        Assert.Equal(0, Arch.Cover(murloc, []).Fit);
    }

    [Fact]
    public void ClassifyNeedsAClearLead()
    {
        Assert.Null(Arch.Classify([]));
        Assert.Null(Arch.Classify([Card("Lullabot")]));
    }
}
