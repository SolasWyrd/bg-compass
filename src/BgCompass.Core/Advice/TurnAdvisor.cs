using System.Text.RegularExpressions;
using BgCompass.Core.Cards;

namespace BgCompass.Core.Advice;

public enum PickKind { Triple, Pair, Buy }

/// <summary>A shop card worth the gold now. Position is its place in Bob's row, 1-based.</summary>
public sealed record TurnPick(int EntityId, string CardId, string Name, PickKind Kind, double Score, int Cost, int Position, string Why);

/// <summary>The offered card that suits this board best.</summary>
public sealed record ChoicePick(string CardId, string Name, string Why);

/// <summary>The most a fight can cost each side now. Facts: a tier sum from the log, limited by the round's damage cap.</summary>
public sealed record DamageReach(int Dealt, int DealtUncapped, int? Taken, int? TakenUncapped, int? TakenBoardAge, int? Cap);

/// <summary>What to do with this turn's gold. Every step fits the gold there is; the picks are rules, the numbers are facts.</summary>
public sealed record TurnPlan(
    int Tier,
    int MaxTier,
    int? Gold,
    int? UpgradeCost,
    int? Health,
    DamageReach Damage,
    string? MainTribe,
    int MainTribeCount,
    IReadOnlyList<TurnPick> Picks,
    MinionView? Sell,
    string SellWhy,
    bool Upgrade,
    string UpgradeWhy,
    bool Refresh,
    bool Freeze,
    ChoicePick? Choice);

/// <summary>
/// Reads the board as it stands: which tribe it is, which keywords it pays for, what the hand and trinkets add. A shop card
/// scores by what it does for that board, so a Murloc board is never told to take a Pirate because the Pirate has a Battlecry.
/// </summary>
public static class TurnAdvisor
{
    const int MaxBoard = 7, DangerHealth = 12;
    const double MainShare = 0.5, PickFloor = 1.5, EarlyFloor = 0.8, FreezeFloor = 2.5, SellMargin = 0.5, DangerFactor = 1.5;
    const int EarlyPieces = 3, EarlyRound = 3, CheapUpgrade = 3;
    const double TripleBonus = 10;

    /// <summary>A keyword a card can bring, and the text of cards that pay for it.</summary>
    sealed record Link(string Label, Func<CardInfo, bool> Gives, Regex Pays);

    static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static Func<CardInfo, bool> Mech(string m) => c => c.Mechanics.Contains(m);
    static Func<CardInfo, bool> Says(string p) { var r = Rx(p); return c => r.IsMatch(c.TextEn); }

    static readonly Link[] Links =
    [
        new("перерождение", Mech("REBORN"), Rx(@"(minion|Undead) is\s+Reborn|after .{0,30}Reborn")),
        new("хрипы", Mech("DEATHRATTLE"), Rx(@"Deathrattles? (trigger|you've)|Deathrattle minions|left-most Deathrattle|your Deathrattles")),
        new("кличи", Mech("BATTLECRY"), Rx(@"trigger.{0,25}Battlecr|Battlecries trigger|your Battlecries")),
        new("щиты", Mech("DIVINE_SHIELD"), Rx(@"(with|loses?|gains?|for each).{0,40}Divine Shield")),
        new("раж", Mech("BACON_RALLY"), Rx(@"trigger.{0,25}Rally|friendly Rally|Rally minion|your Rallies")),
        new("заклинания таверны", Says(@"Spellcraft|Get a .{0,30}Tavern spell|Tavern spells? .{0,20}(hand|cost)"), Rx(@"(cast|after you cast|whenever you cast).{0,20}Tavern spell|your Tavern spells")),
        new("божество", c => c.Tribes.Contains("ABERRATION"), Rx(@"\bDeity\b")),
        new("сброс", Says(@"\bDiscard (a|your)"), Rx(@"(whenever|after) you discard|discarded")),
        new("магнетизм", Mech("MAGNETIC"), Rx(@"Magnetize|Magnetic minions")),
        new("самоцветы", Says(@"Blood Gem"), Rx(@"Blood Gem")),
        new("конец хода", Mech("END_OF_TURN_TRIGGER"), Rx(@"end of turn effects|your End of Turn")),
        new("начало боя", Mech("START_OF_COMBAT"), Rx(@"Start of Combat effects|your Start of Combat")),
        new("призыв", Says(@"\bSummon\b"), Rx(@"(after|whenever) you summon")),
    ];

    /// <summary>The player's forces as advice sees them.</summary>
    sealed class Forces
    {
        public required List<(MinionView View, CardInfo Card)> Pieces;
        public required IReadOnlyList<CardInfo> Trinkets;
        public required Dictionary<string, int> Tribes;
        public string? Main;
        public int MainCount;
        public double MainShare;
        public int Round;
        public IReadOnlyDictionary<string, int> Copies = new Dictionary<string, int>();
        public ArchetypeFit? Lead;
        public Family? LeadFamily;
        public bool Committed => Main is not null && MainShare >= TurnAdvisor.MainShare && Pieces.Count >= EarlyPieces;
        public bool Early => Pieces.Count < EarlyPieces || Round <= EarlyRound;
    }

    static readonly Lazy<Archetypes> Embedded = new(Archetypes.LoadEmbedded);

    public static TurnPlan Plan(MatchSnapshot m, CardCatalog cards, BoardAdvice? advice = null)
    {
        var forces = Read(m, cards, advice);
        var tier = Math.Max(m.TavernTier ?? 1, 1);
        var reach = Reach(m, cards, tier);
        var health = m.Players.FirstOrDefault(p => p.IsLocal)?.Health;

        var scored = m.Shop.Concat(m.ShopSpells).Where(c => c.Position > 0)
            .Select(c => Offer(c, forces, cards)).OfType<TurnPick>().OrderByDescending(p => p.Score).ToList();

        var budget = m.Gold ?? int.MaxValue;
        var picks = new List<TurnPick>();
        // Hand minions are headed for the board, so they take its slots. A spell is cast on the spot and takes none.
        var room = Math.Max(0, MaxBoard - m.Board.Count - (m.Hand?.Count ?? 0));
        var spells = m.ShopSpells.Select(c => c.EntityId).ToHashSet();

        bool Take(TurnPick p)
        {
            if (p.Cost > budget) return false;
            budget = budget == int.MaxValue ? budget : budget - p.Cost;
            picks.Add(p);
            return true;
        }

        // A third copy pays for itself whatever else is on offer, and it fits even on a full board: the copies merge.
        // One copy completes it; a second copy of that card in the row is just another minion.
        var tripled = new HashSet<string>();
        for (var i = 0; i < scored.Count; i++)
        {
            if (scored[i].Kind != PickKind.Triple) continue;
            if (tripled.Add(scored[i].CardId)) Take(scored[i]);
            else scored[i] = scored[i] with { Kind = PickKind.Buy, Score = scored[i].Score - TripleBonus, Why = "Лишняя копия: для тройки хватит одной." };
        }
        scored = scored.OrderByDescending(p => p.Score).ToList();

        var (upgrade, upgradeWhy) = WantUpgrade(m, tier, budget, reach, health, scored);
        if (upgrade && m.UpgradeCost is int cost && budget != int.MaxValue) budget -= cost;

        var floor = forces.Early && !forces.Committed ? EarlyFloor : PickFloor;
        var wanted = scored.Where(p => p.Kind != PickKind.Triple && p.Score >= floor).ToList();

        MinionView? sell = null;
        var sellWhy = "";
        foreach (var p in wanted)
        {
            if (spells.Contains(p.EntityId)) { Take(p); continue; }
            if (room > 0 && Take(p)) { room--; continue; }
            if (room > 0 || sell is not null) continue;
            // Full: only a card clearly better than the weakest on the board earns the swap. Selling refunds 1 gold.
            var weak = Weakest(m, forces, cards);
            if (weak is null || weak.Value.Score + SellMargin >= p.Score || p.Cost > budget + 1) continue;
            sell = weak.Value.View;
            sellWhy = $"Стол полон, «{p.Name}» даст больше. Правило: проверь, не держит ли это существо билд.";
            if (budget != int.MaxValue) budget += 1;
            Take(p);
        }

        var refresh = picks.Count == 0 && !upgrade && m.Gold is not null && m.RefreshCost is int rc
            && budget >= rc + (m.Shop.FirstOrDefault()?.Cost ?? 3);
        var left = scored.Except(picks).FirstOrDefault(p => p.Score >= FreezeFloor || p.Kind == PickKind.Triple);
        var freeze = left is not null && m.Gold is not null && budget < left.Cost && !refresh;

        return new TurnPlan(tier, m.MaxTier, m.Gold, tier < m.MaxTier ? m.UpgradeCost : null, health, reach,
            forces.Main, forces.MainCount, picks, sell, sellWhy, upgrade, upgradeWhy, refresh, freeze, Choose(m, forces, cards));
    }

    // ---- reading the board ---------------------------------------------------------------------

    static Forces Read(MatchSnapshot m, CardCatalog cards, BoardAdvice? advice)
    {
        var pieces = m.Board.Concat(m.Hand ?? [])
            .Select(v => (View: v, Card: cards.Resolve(v.CardId)!)).Where(x => x.Card is not null).ToList();
        var trinkets = m.Trinkets.Select(cards.Resolve).OfType<CardInfo>().ToList();
        var tribes = new Dictionary<string, int>();
        foreach (var (v, c) in pieces)
            foreach (var t in Tribes(v, c)) tribes[t] = tribes.GetValueOrDefault(t) + 1;
        var top = tribes.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).FirstOrDefault();
        var main = top.Value >= 2 ? top.Key : null;

        var lead = advice?.Directions.FirstOrDefault();
        return new Forces
        {
            Pieces = pieces,
            Trinkets = trinkets,
            Tribes = tribes,
            Main = main,
            MainCount = main is null ? 0 : top.Value,
            MainShare = main is null || pieces.Count == 0 ? 0 : top.Value / (double)pieces.Count,
            Round = m.Round,
            Copies = m.Board.Concat(m.Hand ?? []).Where(v => !v.Golden).GroupBy(v => v.CardId).ToDictionary(g => g.Key, g => g.Count()),
            Lead = lead,
            LeadFamily = lead is null ? null : Embedded.Value.Get(lead.Id),
        };
    }

    static IEnumerable<string> Tribes(MinionView v, CardInfo c)
    {
        var known = c.Tribes.Count > 0 ? c.Tribes : v.Tribes;
        return known.Contains("ALL") ? TribeNames.Real : known.Distinct();
    }

    // ---- damage --------------------------------------------------------------------------------

    /// <summary>A win deals the winner's tavern tier plus the tier of every survivor, so this is the top of what the boards can do.</summary>
    static DamageReach Reach(MatchSnapshot m, CardCatalog cards, int tier)
    {
        int Sum(IEnumerable<MinionView> board) => board.Sum(v => v.TechLevel > 0 ? v.TechLevel : cards.Resolve(v.CardId)?.Tier ?? 1);
        int Cap(int d) => m.DamageCap is int c ? Math.Min(d, c) : d;
        var dealt = tier + Sum(m.Board);
        var enemy = m.Players.FirstOrDefault(p => p.IsNextOpponent && !p.IsDead);
        int? taken = enemy is { LastBoard: { Minions.Count: > 0 } b, TavernTier: int et } ? et + Sum(b.Minions) : null;
        return new DamageReach(Cap(dealt), dealt, taken is int t ? Cap(t) : null, taken,
            enemy?.LastBoard is { } seen ? Math.Max(0, m.Round - seen.Round) : null, m.DamageCap);
    }

    // ---- scoring -------------------------------------------------------------------------------

    static TurnPick? Offer(MinionView c, Forces f, CardCatalog cards)
    {
        var card = cards.Resolve(c.CardId);
        if (card is null) return null;
        var copies = c.Golden ? 0 : f.Copies.GetValueOrDefault(c.CardId);
        var why = new List<string>();
        var score = Worth(card, c, f, why);
        if (c.Golden) { score += 2; why.Insert(0, "Золотая карта"); }
        if (copies >= 2)
            return new(c.EntityId, c.CardId, card.Name, PickKind.Triple, score + TripleBonus, c.Cost, c.Position, $"Копий у тебя: {copies}. Третья даст золотую и награду.");
        if (copies == 1) { score += 1; why.Add("вторая копия"); }
        return new(c.EntityId, c.CardId, card.Name, copies == 1 ? PickKind.Pair : PickKind.Buy, score, c.Cost, c.Position,
            why.Count == 0 ? "Сильнее остальных по статам для своего уровня." : Capital(string.Join(", ", why)) + ".");
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>What a card does for these forces. <paramref name="self"/> is left out of the counts when it is already among them.</summary>
    static double Worth(CardInfo card, MinionView view, Forces f, List<string>? why, MinionView? self = null)
    {
        var score = 0.0;
        var linked = false;
        var others = self is null ? f.Pieces : f.Pieces.Where(p => p.View.EntityId != self.EntityId).ToList();
        void Say(string s) { if (why is not null && !why.Contains(s)) why.Add(s); }
        int Count(string tribe) => others.Count(p => Tribes(p.View, p.Card).Contains(tribe));

        // Same tribe as the board.
        var own = Tribes(view, card).ToList();
        if (f.Main is not null && own.Contains(f.Main))
        {
            score += 3 * f.MainShare;
            linked = true;
            Say($"{TribeNames.Ru(f.Main)} на столе: {f.MainCount}");
        }
        else
        {
            var side = own.Select(t => (Tribe: t, N: Count(t))).Where(x => x.N >= 2).OrderByDescending(x => x.N).FirstOrDefault();
            if (side.Tribe is not null && others.Count > 0)
            {
                score += 1.5 * side.N / others.Count;
                linked = true;
                Say($"{TribeNames.Ru(side.Tribe)} на столе: {side.N}");
            }
        }

        // The card names a tribe: it pays for that tribe's minions, or does nothing without them.
        foreach (var t in TribeNames.Mentioned(card.TextEn))
        {
            var n = Count(t);
            if (n > 0)
            {
                score += Math.Min(n, 5) * 0.7;
                linked = true;
                Say($"работает с типом «{TribeNames.Ru(t)}»: {n}");
            }
            else if (!own.Contains(t)) score -= 0.5;
        }

        // Cards already here name this card's tribe.
        var fans = others.Where(p => p.Card.Id != card.Id && TribeNames.Mentioned(p.Card.TextEn).Any(own.Contains)).ToList();
        fans.AddRange(f.Trinkets.Where(t => TribeNames.Mentioned(t.TextEn).Any(own.Contains)).Select(t => (view, t)));
        if (fans.Count > 0)
        {
            score += Math.Min(fans.Count, 3) * 0.6;
            linked = true;
            Say($"усиливает: {fans[0].Card.Name}");
        }

        // Keywords: a card that pays for what the board has, or brings what the board pays for.
        var pool = others.Select(p => p.Card).Concat(f.Trinkets).ToList();
        foreach (var k in Links)
        {
            if (k.Pays.IsMatch(card.TextEn))
            {
                var givers = others.Count(p => k.Gives(p.Card));
                if (givers > 0)
                {
                    score += Math.Min(givers, 4) * 0.7;
                    linked = true;
                    Say($"{k.Label} на столе: {givers}");
                }
                else score -= 0.5;
            }
            if (k.Gives(card))
            {
                var payers = pool.Where(p => k.Pays.IsMatch(p.TextEn)).ToList();
                if (payers.Count > 0)
                {
                    score += Math.Min(payers.Count, 2);
                    linked = true;
                    Say($"{k.Label}: {payers[0].Name}");
                }
            }
        }

        // The role the leading tribe build lists, when the board really is that tribe.
        if (f.Lead is { Tribe: not null } lead && lead.Tribe == f.Main && f.LeadFamily is { } fam && Archetypes.IsCore(fam, card))
        {
            score += 1;
            linked = true;
            Say("ключевая роль билда");
        }

        // Raw stats for the tier carry the early game; later they only break ties.
        var tier = Math.Max(card.Tier ?? view.TechLevel, 1);
        if (view.Attack + view.Health > 0)
            score += (f.Early && !f.Committed ? 1.5 : 0.6) * Math.Min((view.Attack + view.Health) / (4.0 + 2 * tier), 1.5) + 0.15 * tier;

        if (!linked && f.Committed) score -= own.Count == 0 ? 0.5 : 1.5;
        return score;
    }

    static (MinionView View, double Score)? Weakest(MatchSnapshot m, Forces f, CardCatalog cards)
    {
        (MinionView View, double Score)? worst = null;
        foreach (var v in m.Board.Where(v => !v.Golden))
        {
            var card = cards.Resolve(v.CardId);
            if (card is null) continue;
            var s = Worth(card, v, f, null, v);
            if (worst is null || s < worst.Value.Score) worst = (v, s);
        }
        return worst;
    }

    // ---- tavern --------------------------------------------------------------------------------

    static (bool Want, string Why) WantUpgrade(MatchSnapshot m, int tier, int budget, DamageReach reach, int? health, List<TurnPick> scored)
    {
        if (tier >= m.MaxTier || m.UpgradeCost is not int cost || m.Round < 1 || cost > budget) return (false, "");
        var expected = BoardAdvisor.ExpectedTier(m.Round);
        var behind = tier < expected;
        var cheap = cost <= CheapUpgrade;
        var worth = scored.Where(p => p.Kind != PickKind.Triple).ToList();
        var noBuy = !worth.Any(p => p.Score >= PickFloor);
        var danger = health is int h && (h <= DangerHealth || (reach.Taken is int t && h <= t * DangerFactor));

        if (danger && !noBuy) return (false, "");
        // Two cards the board wants beat a level that would leave no gold for either.
        var strong = worth.Where(p => p.Score >= FreezeFloor).ToList();
        if (!cheap && strong.Count >= 2 && budget - cost < strong.Min(p => p.Cost)) return (false, "");
        if (behind) return (true, $"На {m.Round}-м ходу таверна обычно {expected}-го уровня, у тебя {tier}-й. Цена {cost}.");
        if (cheap) return (true, $"Сейчас стоит всего {cost}: дальше скидок не будет.");
        if (noBuy && m.Round >= 2 && budget - cost < 3 && cost <= budget) return (true, $"В магазине нечего брать, а на {cost} золота можно поднять уровень.");
        return (false, "");
    }

    // ---- choices -------------------------------------------------------------------------------

    static ChoicePick? Choose(MatchSnapshot m, Forces f, CardCatalog cards)
    {
        if (m.Choices.Count < 2) return null;
        ChoicePick? best = null;
        var top = double.MinValue;
        var tied = false;
        foreach (var c in m.Choices)
        {
            var card = cards.Resolve(c.CardId);
            if (card is null) return null; // An unknown option could be the best one, so name none.
            var why = new List<string>();
            var view = new MinionView(c.EntityId, c.CardId, c.Name, card.Attack ?? 0, card.Health ?? 0, 0, false, MinionKeywords.None, card.Tribes, card.Tier ?? 0);
            var s = Worth(card, view, f, why);
            if (s > top + 0.01) { top = s; tied = false; best = new(c.CardId, card.Name, why.Count == 0 ? "Сильнее остальных по статам для своего уровня." : Capital(string.Join(", ", why)) + "."); }
            else if (Math.Abs(s - top) <= 0.01) tied = true;
        }
        // Options that read alike get no winner: a coin flip must not look like advice.
        return tied || top <= 0 ? null : best;
    }
}
