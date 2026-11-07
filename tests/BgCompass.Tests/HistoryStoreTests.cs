using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;
using Xunit;
using static BgCompass.Tests.Support;

namespace BgCompass.Tests;

// BOOKED: averages checked by hand. Gallywix places 1, 3, 8 -> 4.0 average, top-4 rate 2/3, 1 win.
public sealed class HistoryStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bgc-history-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "history.jsonl");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    static MatchRecord Rec(string id, string hero, int? place, int day = 1, string? archetype = null, string[]? board = null) =>
        new(id, new DateTimeOffset(2026, 11, day, 20, 0, 0, TimeSpan.Zero), 256589, hero, "h", place, 12,
            board ?? [], ["MURLOC"], archetype);

    [Fact]
    public void AppendIsDedupedByGameIdAcrossInstances()
    {
        var store = new HistoryStore(_dir);
        Assert.True(store.Append(Rec("g1", "H1", 3)));
        Assert.False(store.Append(Rec("g1", "H1", 1)));
        Assert.False(new HistoryStore(_dir).Append(Rec("g1", "H1", 1)));
        Assert.Single(store.History());
        Assert.Equal(3, store.History()[0].Place);
        Assert.Single(System.IO.File.ReadAllLines(File_));
    }

    [Fact]
    public void CorruptAndTruncatedLinesAreSkippedAndLaterWritesStillWork()
    {
        var store = new HistoryStore(_dir);
        store.Append(Rec("g1", "H1", 2, 1));
        // garbage line plus a half-written last line without a newline, as a crash would leave it
        System.IO.File.AppendAllText(File_, "not json at all\n{\"GameId\":\"g2\",\"EndedAt\":\"2026-1");

        var after = new HistoryStore(_dir);
        Assert.Equal(["g1"], after.History().Select(r => r.GameId));
        Assert.True(after.Append(Rec("g3", "H1", 4, 3)));
        Assert.Equal(["g3", "g1"], after.History().Select(r => r.GameId));
    }

    [Fact]
    public void HistoryIsNewestFirst()
    {
        var store = new HistoryStore(_dir);
        store.Append(Rec("old", "H1", 5, 1));
        store.Append(Rec("new", "H1", 5, 9));
        store.Append(Rec("mid", "H1", 5, 5));
        Assert.Equal(["new", "mid", "old"], store.History().Select(r => r.GameId));
    }

    [Fact]
    public void AggregatesPerHeroOverallAndPerArchetypeCountOnlyFinishedGames()
    {
        var gally = HeroId("Trade Prince Gallywix");
        var store = new HistoryStore(_dir);
        store.Append(Rec("a", gally, 1, 1, "murloc"));
        store.Append(Rec("b", gally, 3, 2, "murloc"));
        store.Append(Rec("c", gally, 8, 3, null));
        store.Append(Rec("d", HeroId("Ysera"), 6, 4, "mech"));
        store.Append(Rec("e", HeroId("Ysera"), null, 5, "mech")); // abandoned: no place, not counted

        var g = store.HeroStatsByHero(Cards)[gally];
        Assert.Equal(3, g.Games);
        Assert.Equal(4.0, g.AveragePlace, 6);
        Assert.Equal(2 / 3.0, g.Top4Rate, 6);
        Assert.Equal(1, g.Wins);

        Assert.Equal(1, store.HeroStatsByHero(Cards)[HeroId("Ysera")].Games);
        var overall = store.Overall()!;
        Assert.Equal(4, overall.Games);
        Assert.Equal((1 + 3 + 8 + 6) / 4.0, overall.AveragePlace, 6);

        var byArchetype = store.StatsByArchetype();
        Assert.Equal(2, byArchetype["murloc"].Games);
        Assert.Equal(2.0, byArchetype["murloc"].AveragePlace, 6);
        Assert.Equal(1, byArchetype["mech"].Games);
        Assert.False(byArchetype.ContainsKey(""));
    }

    [Fact]
    public void SkinGamesAreCountedUnderTheBaseHero()
    {
        var gally = Cards.All.First(c => c.IsBgHero && c.NameEn == "Trade Prince Gallywix" && c.SkinParentDbfId is null);
        var skin = gally with { Id = "SKIN_G", DbfId = 99999002, SkinParentDbfId = gally.DbfId };
        var catalog = new CardCatalog([.. Cards.All, skin], Cards.Version);
        var store = new HistoryStore(_dir);
        store.Append(Rec("a", gally.Id, 2, 1));
        store.Append(Rec("b", "SKIN_G", 4, 2));
        Assert.Equal(2, store.HeroStatsByHero(catalog)[gally.Id].Games);
    }

    [Fact]
    public void EmptyStoreHasNoStats()
    {
        var store = new HistoryStore(_dir);
        Assert.Empty(store.History());
        Assert.Null(store.Overall());
        Assert.Empty(store.HeroStatsByHero(Cards));
    }

    [Fact]
    public void ArchetypeIsStoredOnlyWhenTheFinalBoardClearlyLeads()
    {
        var arch = Archetypes.LoadEmbedded();
        string[] Ids(params string[] names) => names.Select(n => Card(n).Id).ToArray();

        Assert.Equal("undead", HistoryStore.ClassifyBoard(
            Ids("Risen Rider", "Scarlet Skull", "Mummifier", "Cadaver Caretaker", "Harmless Bonehead"), Cards, arch));
        Assert.Null(HistoryStore.ClassifyBoard(Ids("Lullabot", "Tad", "Risen Rider", "Mini-Myrmidon"), Cards, arch));
        Assert.Null(HistoryStore.ClassifyBoard([], Cards, arch));
        Assert.Null(HistoryStore.ClassifyBoard(["NOPE_1"], Cards, arch));
    }
}
