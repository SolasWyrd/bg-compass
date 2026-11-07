using BgCompass.Core;
using BgCompass.Core.Cards;

namespace BgCompass.Tests;

/// <summary>Builds snapshots from real card names so scenarios read like a board, not like ids.</summary>
static class Support
{
    public static readonly CardCatalog Cards = CardCatalog.LoadEmbedded();
    public static readonly IReadOnlySet<string> None = new HashSet<string>();

    public static CardInfo Card(string nameEn) =>
        Cards.All.First(c => c.NameEn == nameEn && c.Type == "MINION" && !c.IsDuosExclusive);

    public static MinionView View(string nameEn, int position, int entity = 0)
    {
        var c = Card(nameEn);
        return new MinionView(entity == 0 ? 100 + position : entity, c.Id, c.Name, c.Attack ?? 1, c.Health ?? 1, position,
            false, MinionKeywords.None, c.Tribes, c.Tier ?? 1);
    }

    public static IReadOnlyList<MinionView> Minions(params string[] names) =>
        names.Select((n, i) => View(n, i + 1)).ToList();

    public static MatchSnapshot Snapshot(string[] board, int round = 4, int? tier = 3, string[]? shop = null,
        params string[] confirmed) =>
        MatchSnapshot.Empty with
        {
            Phase = MatchPhase.Recruit,
            Round = round,
            TavernTier = tier,
            Board = Minions(board),
            Shop = (shop ?? []).Select((n, i) => View(n, i + 1, 500 + i)).ToList(),
            ConfirmedTribes = new HashSet<string>(confirmed),
        };

    public static HeroOption Hero(string nameEn, int position = 1)
    {
        var h = Cards.All.First(c => c.IsBgHero && c.SkinParentDbfId is null && c.NameEn == nameEn);
        return new HeroOption(900 + position, h.Id, h.Name, position);
    }

    public static string HeroId(string nameEn) => Hero(nameEn).CardId;
}
