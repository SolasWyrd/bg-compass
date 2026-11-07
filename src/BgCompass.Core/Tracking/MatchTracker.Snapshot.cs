using System.Collections.Frozen;
using BgCompass.Core.Logs;

namespace BgCompass.Core.Tracking;

public sealed partial class MatchTracker
{
    private static readonly FrozenSet<string> NoTribes = FrozenSet<string>.Empty;

    /// <summary>The local player's hero: the one the player picked, else a non-placeholder hero in play under the local controller.</summary>
    private static TrackedEntity? LocalHero(Game g)
    {
        if (g.LocalHeroId != 0 && g.Ents.TryGetValue(g.LocalHeroId, out var h)) return h;
        if (g.LocalPid == 0 || !g.HeroChosen) return null;
        TrackedEntity? best = null;
        foreach (var e in g.Ents.Values)
            if (e.IsHero && e.Controller == g.LocalPid && e.Zone == TagValues.ZonePlay && !IsPlaceholderHero(e.CardId)
                && (best is null || e.Id > best.Id))
                best = e;
        return best;
    }

    private static List<int> SeatPids(Game g)
    {
        var set = new HashSet<int>();
        foreach (var e in g.Ents.Values)
            if (e.IsHero && e.PlayerId > 0 && !IsPlaceholderHero(e.CardId)) set.Add(e.PlayerId);
        return [.. set];
    }

    private MatchPhase PhaseOf(Game g)
    {
        if (!g.Started || g.Ignored) return MatchPhase.Idle;
        if (g.Ended) return MatchPhase.GameOver;
        if (g.Offers.Count > 0 && !g.HeroChosen) return MatchPhase.HeroPick;
        if (g.Turn >= 1) return g.Turn % 2 == 1 ? MatchPhase.Recruit : MatchPhase.Combat;
        return MatchPhase.Idle;
    }

    private MatchSnapshot BuildSnapshot()
    {
        var g = _g;
        if (!g.Started) return MatchSnapshot.Empty;
        if (g.Ignored) return MatchSnapshot.Empty with { BuildNumber = g.Build };

        if (g.Pending is { } pending) NoteSighting(g, pending);
        var phase = PhaseOf(g);
        var hero = LocalHero(g);
        var localPlayer = g.LocalPid != 0 ? g.PlayerEntities.GetValueOrDefault(g.LocalPid) : null;

        var heroOffers = new List<HeroOption>(g.Offers.Count);
        var onScreen = g.Offers.OrderBy(o => g.Ents.GetValueOrDefault(o.Id)?.ZonePos ?? 0).ToList();
        for (int i = 0; i < g.Offers.Count; i++)
            heroOffers.Add(new HeroOption(g.Offers[i].Id, g.Offers[i].CardId, g.Offers[i].Name, i, onScreen.IndexOf(g.Offers[i])));
        if (heroOffers.Count == 0 && !g.HeroChosen) heroOffers = HandHeroes(g);

        var board = MinionsOf(g, g.LocalPid).ConvertAll(ToView);
        IReadOnlyList<MinionView> shop = [];
        int shopSize = 0;
        if (phase == MatchPhase.Recruit && AiPid(g) != 0)
        {
            var list = new List<MinionView>();
            foreach (var m in MinionsOf(g, AiPid(g)))
                if (!m.BornInCombat) list.Add(ToView(m));
            shop = list;
            // Tavern spells share the row with the minions, so a minion's place on screen depends on them too.
            int ai = AiPid(g);
            foreach (var e in g.Ents.Values)
                if (e.Controller == ai && e.Zone == TagValues.ZonePlay && e.ZonePos > 0 && !e.BornInCombat
                    && (e.IsMinion || e.CardType is TagValues.TypeSpell or TagValues.TypeTavernSpell))
                    shopSize++;
        }

        var hand = new List<MinionView>();
        if (phase == MatchPhase.Recruit && g.LocalPid != 0)
            foreach (var e in g.Ents.Values)
                if (e.IsMinion && e.Zone == TagValues.ZoneHand && e.Controller == g.LocalPid && !string.IsNullOrEmpty(e.CardId))
                    hand.Add(ToView(e));

        int? gold = null;
        if (phase == MatchPhase.Recruit && localPlayer is { HasResources: true })
            gold = Math.Max(0, localPlayer.Resources - localPlayer.ResourcesUsed);

        return new MatchSnapshot(
            phase, g.Build, (g.Turn + 1) / 2,
            hero?.CardId, hero is null ? null : hero.Name ?? hero.CardId,
            (g.LocalPid != 0 ? SeatTier(g, g.LocalPid) : null) ?? hero?.PlayerTech ?? localPlayer?.PlayerTech, gold,
            heroOffers, board, shop, BuildPlayers(g), g.Encounters.ToArray(),
            g.Confirmed.Count == 0 ? NoTribes : g.Confirmed.ToFrozenSet(StringComparer.Ordinal),
            g.Combat?.Snap, g.Ended ? hero?.Place : null, g.IsDuos, shopSize, hand);
    }

    private static List<HeroOption> HandHeroes(Game g)
    {
        var list = new List<TrackedEntity>();
        foreach (var e in g.Ents.Values)
            if (e.IsHero && e.Zone == TagValues.ZoneHand && e.Controller == g.LocalPid && g.LocalPid != 0 && e.CardId is not null
                && !IsPlaceholderHero(e.CardId))
                list.Add(e);
        list.Sort((a, b) => a.ZonePos.CompareTo(b.ZonePos));
        return list.ConvertAll(e => new HeroOption(e.Id, e.CardId!, e.Name ?? e.CardId!, 0))
            .Select((o, i) => o with { Position = i }).ToList();
    }

    private static List<PlayerView> BuildPlayers(Game g)
    {
        var byPid = new Dictionary<int, List<TrackedEntity>>();
        foreach (var e in g.Ents.Values)
        {
            if (!e.IsHero || e.PlayerId <= 0 || IsPlaceholderHero(e.CardId)) continue;
            if (!byPid.TryGetValue(e.PlayerId, out var l)) byPid[e.PlayerId] = l = [];
            l.Add(e);
        }

        var result = new List<PlayerView>(byPid.Count);
        foreach (var (pid, group) in byPid)
        {
            // Newest first: a re-created hero's values win and older ones only fill the gaps.
            group.Sort((a, b) => b.Id.CompareTo(a.Id));
            var primary = SeatHero(g, pid)!;
            int? place = primary.Place ?? group.Select(x => x.Place).FirstOrDefault(x => x is not null);
            int? tier = SeatTier(g, pid);
            int? triples = primary.Triples ?? group.Select(x => x.Triples).FirstOrDefault(x => x is not null);
            var withHealth = primary.Health > 0 ? primary : group.FirstOrDefault(x => x.Health > 0);
            int? left = HeroHealth(withHealth);
            bool known = left is not null;
            int health = left ?? 0;
            bool local = pid == g.LocalPid;
            result.Add(new PlayerView(
                pid, primary.CardId ?? "", primary.Name ?? primary.CardId ?? "", local, place, tier,
                known ? health : null, triples, known && health <= 0,
                !local && pid == g.NextOpp, g.LastBoards.GetValueOrDefault(pid)));
        }
        result.Sort((a, b) =>
        {
            int c = (a.Place ?? int.MaxValue).CompareTo(b.Place ?? int.MaxValue);
            return c != 0 ? c : a.PlayerId.CompareTo(b.PlayerId);
        });
        return result;
    }
}
