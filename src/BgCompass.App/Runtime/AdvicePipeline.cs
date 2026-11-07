using System.Text;
using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;

namespace BgCompass.App.Runtime;

/// <summary>
/// Turns a snapshot plus the player's tribe marks into advice. Advisors run again only when the part of the snapshot
/// they read (or the card data, the marks, the history) changed, so a gold tick costs nothing.
/// </summary>
public sealed class AdvicePipeline(Func<IReadOnlyDictionary<string, HeroStats>> personalStats)
{
    readonly object _gate = new();
    string? _heroKey, _boardKey;
    CardCatalog? _heroCat, _boardCat;
    HeroPickAdvice? _hero;
    BoardAdvice? _board;

    public int HeroRuns { get; private set; }
    public int BoardRuns { get; private set; }

    /// <summary>The marks the advisors get: tribes the player marked absent, minus any the log has since proven present.</summary>
    public static IReadOnlySet<string> Effective(IReadOnlySet<string> marked, IReadOnlySet<string> confirmed) =>
        marked.Where(t => !confirmed.Contains(t)).ToHashSet();

    public (HeroPickAdvice? Hero, BoardAdvice? Board) Advise(
        MatchSnapshot m, CardCatalog? cards, IReadOnlySet<string> marked, int historyRevision)
    {
        lock (_gate)
        {
            if (cards is null) return (null, null);
            var excluded = Effective(marked, m.ConfirmedTribes);

            HeroPickAdvice? hero = null;
            if (m.Phase == MatchPhase.HeroPick && m.HeroOffers.Count > 0)
            {
                var key = Key(m.HeroOffers.Select(o => o.CardId), excluded, m.ConfirmedTribes, historyRevision.ToString());
                if (key != _heroKey || !ReferenceEquals(cards, _heroCat))
                {
                    _hero = HeroAdvisor.Advise(m.HeroOffers, cards, m.ConfirmedTribes, excluded, personalStats());
                    _heroKey = key;
                    _heroCat = cards;
                    HeroRuns++;
                }
                hero = _hero;
            }

            BoardAdvice? board = null;
            if (m.Phase is MatchPhase.Recruit or MatchPhase.Combat)
            {
                var key = Key(
                    m.Board.Select(Sig).Concat(m.Shop.Select(Sig)).Append($"t{m.TavernTier}r{m.Round}"),
                    excluded, m.ConfirmedTribes, "");
                if (key != _boardKey || !ReferenceEquals(cards, _boardCat))
                {
                    _board = BoardAdvisor.Advise(m, cards, excluded);
                    _boardKey = key;
                    _boardCat = cards;
                    BoardRuns++;
                }
                board = _board;
            }
            return (hero, board);
        }
    }

    static string Sig(MinionView v) => $"{v.CardId}:{v.Attack}/{v.Health}:{v.Position}:{(int)v.Keywords}:{v.Golden}:{v.EntityId}";

    static string Key(IEnumerable<string> parts, IReadOnlySet<string> excluded, IReadOnlySet<string> confirmed, string extra)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p).Append('|');
        sb.Append("#x:").AppendJoin(',', excluded.Order()).Append("#c:").AppendJoin(',', confirmed.Order()).Append('#').Append(extra);
        return sb.ToString();
    }
}
