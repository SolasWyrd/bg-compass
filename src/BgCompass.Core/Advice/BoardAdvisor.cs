using System.Text.RegularExpressions;
using BgCompass.Core.Cards;

namespace BgCompass.Core.Advice;

/// <summary>
/// Reads the player's board and says what it can support. Facts come from card text and tags, directions are
/// role coverage (never a win chance), "usually" statements are labelled Rule, and what we cannot know is a Limit.
/// Solo mode is assumed: cards that exist only in Duos are never offered.
/// </summary>
public static class BoardAdvisor
{
    const int ListCap = 6;

    // Tavern tier a standard game reaches by a given round: tier 2 on round 2, 3 on 4, 4 on 6, 5 on 8.
    // This is bgtracker's "standard" leveling curve (data/curves.json, MIT, an editor's consensus, not a
    // measurement). Heroes with an economy or a pricey power level earlier or later, so a gap is only
    // reported when the player is two tiers or more behind it.
    static readonly (int Round, int Tier)[] Curve = [(2, 2), (4, 3), (6, 4), (8, 5)];

    static int ExpectedTier(int round) => 1 + Curve.Count(c => round >= c.Round);

    static readonly Regex ShieldPayoff = new(
        @"(with|loses|lose|gains?|gives?|for each)\b.{0,40}Divine Shield|Divine Shield.{0,30}(until next turn|when|while)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex RallyPayoff = new(@"[Tt]rigger.{0,25}Rally|friendly Rally|Rally minion", RegexOptions.CultureInvariant);
    static readonly Regex DeathrattlePayoff = new(
        @"Deathrattles trigger|Deathrattle minions|left-most Deathrattle|Deathrattles? you've triggered", RegexOptions.CultureInvariant);
    static readonly Regex BattlecryPayoff = new(@"[Tt]rigger.{0,25}Battlecr|Battlecries trigger twice", RegexOptions.CultureInvariant);
    static readonly Regex LeftMost = new(@"left-most(?!.{0,25}in your hand)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex RightMost = new(@"right-most(?!.{0,25}in your hand)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Adjacent = new(@"adjacent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ToTheLeft = new(@"to the left", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ToTheRight = new(@"to the right", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex AttacksAtOnce = new(@"attacks? immediately", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Lazy<Archetypes> Embedded = new(Archetypes.LoadEmbedded);

    sealed record Piece(MinionView View, CardInfo Card);

    public static BoardAdvice Advise(MatchSnapshot m, CardCatalog cards, IReadOnlySet<string> excludedTribes) =>
        Advise(m, cards, excludedTribes, Embedded.Value);

    public static BoardAdvice Advise(MatchSnapshot m, CardCatalog cards, IReadOnlySet<string> excludedTribes, Archetypes archetypes)
    {
        var limits = new List<AdviceLine>();

        var board = new List<Piece>();
        var unknown = new List<string>();
        foreach (var v in m.Board.OrderBy(v => v.Position))
        {
            var card = cards.Resolve(v.CardId);
            if (card is null) unknown.Add(string.IsNullOrWhiteSpace(v.Name) ? v.CardId : v.Name);
            else board.Add(new Piece(v, card));
        }

        if (unknown.Count > 0)
            limits.Add(new(AdviceSource.Limit,
                $"Нет в данных о картах и поэтому не учтены: {AdviceText.Names(unknown, 8)}. Возможно, данные карт устарели."));
        if (m.Board.Count == 0)
            limits.Add(new(AdviceSource.Limit, "Стол пуст: направления, связки и пробелы оценивать не по чему."));

        var banned = board.Where(p => OnlyExcludedTribes(p.Card, excludedTribes)).Select(p => p.Card.Name).ToList();
        if (banned.Count > 0)
            limits.Add(new(AdviceSource.Limit,
                $"На столе есть существа исключённых из партии племён: {AdviceText.Names(banned)}. Список племён лобби или данные карт, скорее всего, неточны."));

        var tribeCounts = CountTribes(board);
        var tier = m.TavernTier ?? board.Select(p => p.Card.Tier ?? 0).DefaultIfEmpty(0).Max();
        tier = Math.Max(tier, 1);
        if (m.TavernTier is null && board.Count > 0)
            limits.Add(new(AdviceSource.Limit, $"Уровень таверны лог пока не назвал: цели подобраны по самому высокому существу на столе ({tier}-й уровень)."));

        var directions = board.Count == 0
            ? []
            : Directions(m, cards, excludedTribes, archetypes, board, tier);

        if (directions.Count > 0 && directions[0].Fit < Archetypes.ClearDirection)
            limits.Add(new(AdviceSource.Limit,
                $"Явного направления пока нет: лучшее покрытие {AdviceText.Num(directions[0].Fit * 100, "0")}%. Показаны ближайшие варианты, это не прогноз."));
        else if (board.Count > 0 && directions.Count == 0)
            limits.Add(new(AdviceSource.Limit, "Ни одно известное направление не пересекается с вашим столом."));

        var unseen = directions.Where(d => d.Tribe is not null && !m.ConfirmedTribes.Contains(d.Tribe))
            .Select(d => $"«{TribeNames.Ru(d.Tribe!)}»").Distinct().ToList();
        if (unseen.Count > 0)
            limits.Add(new(AdviceSource.Limit,
                $"Не подтверждены в лобби: {string.Join(", ", unseen)}. Направления оставлены, но их племени может не быть в этой партии."));
        if (m.ConfirmedTribes.Count == 0 && board.Count > 0)
            limits.Add(new(AdviceSource.Limit, "В лобби пока не замечено ни одного племени: какие из них в игре, ещё неизвестно."));

        return new BoardAdvice(
            tribeCounts,
            directions,
            Synergies(board),
            Gaps(m, board, directions, tier),
            Positioning(board),
            limits);
    }

    // ---- Tribes ---------------------------------------------------------------------------------

    static IReadOnlyDictionary<string, int> CountTribes(List<Piece> board)
    {
        var counts = new Dictionary<string, int>();
        foreach (var p in board)
        {
            var tribes = p.View.Tribes.Count > 0 ? p.View.Tribes : p.Card.Tribes;
            // A minion of every type counts for each type, as it does in the game.
            var each = tribes.Contains("ALL") ? TribeNames.Real : tribes;
            foreach (var t in each.Distinct()) counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        return counts;
    }

    // ---- Directions -----------------------------------------------------------------------------

    static List<ArchetypeFit> Directions(
        MatchSnapshot m, CardCatalog cards, IReadOnlySet<string> excluded, Archetypes archetypes,
        List<Piece> board, int tier)
    {
        var onBoard = board.Select(p => p.Card.Id).ToHashSet();
        var inShop = m.Shop.Select(v => cards.Resolve(v.CardId)?.Id).OfType<string>().ToHashSet();

        // Pool cards worth suggesting: solo only, and not a card whose every tribe is out of this game.
        var pool = cards.PoolMinions.Where(c => !c.IsDuosExclusive && !OnlyExcludedTribes(c, excluded)).ToList();

        var scored = archetypes.Families
            .Where(f => f.Tribe is null || !excluded.Contains(f.Tribe))
            .Select(f => (Family: f, Cover: archetypes.Cover(f, board.Select(p => p.Card))))
            .Where(x => x.Cover.Fit > 0)
            .OrderByDescending(x => x.Cover.Fit).ThenByDescending(x => x.Cover.CoreCards.Count).ThenBy(x => x.Family.Id)
            .Take(3)
            .ToList();

        var result = new List<ArchetypeFit>();
        foreach (var (family, cover) in scored)
        {
            int Roles(CardInfo c) => family.Core.Count(r => Archetypes.Matches(family, r, c));
            CardRef Ref(CardInfo c, bool board) => new(c.Id, c.Name, c.Tier ?? 0, c.Tribes, board, inShop.Contains(c.Id));

            var wanted = pool.Where(c => Archetypes.IsCore(family, c) && !onBoard.Contains(c.Id)).ToList();
            var missingNow = wanted.Where(c => (c.Tier ?? 0) <= tier)
                .OrderByDescending(c => inShop.Contains(c.Id)).ThenByDescending(Roles).ThenByDescending(c => c.Tier).ThenBy(c => c.Name)
                .Take(ListCap).Select(c => Ref(c, false)).ToList();
            var next = wanted.Where(c => (c.Tier ?? 0) == tier + 1 || (c.Tier ?? 0) == tier + 2)
                .OrderBy(c => c.Tier).ThenByDescending(Roles).ThenBy(c => c.Name)
                .Take(ListCap).Select(c => Ref(c, false)).ToList();

            var lines = new List<AdviceLine>
            {
                new(AdviceSource.Fact,
                    $"Покрытие плана {AdviceText.Num(cover.Fit * 100, "0")}%: так стол закрывает нужные роли. Это не шанс на победу."),
                new(AdviceSource.Rule, $"План: {family.Plan} (эвристика, а не измерение; от {AdviceText.Date(family.Updated)})"),
            };
            if (cover.MissingCoreRoles.Count > 0)
                lines.Add(new(AdviceSource.Fact, $"На столе нет: {string.Join(", ", cover.MissingCoreRoles)}."));
            var shopHits = missingNow.Where(r => r.InShop).Select(r => r.Name).ToList();
            if (shopHits.Count > 0)
                lines.Add(new(AdviceSource.Fact, $"В магазине сейчас подходит: {AdviceText.Names(shopHits)}."));
            if (family.Tribe is not null && !m.ConfirmedTribes.Contains(family.Tribe))
                lines.Add(new(AdviceSource.Limit, $"Племя «{TribeNames.Ru(family.Tribe)}» ещё не замечено в лобби."));

            result.Add(new ArchetypeFit(family.Id, family.Name, family.Tribe, cover.Fit,
                cover.CoreCards.Select(c => Ref(c, true)).ToList(), missingNow, next, lines));
        }

        return AddTransitions(result);
    }

    static bool OnlyExcludedTribes(CardInfo c, IReadOnlySet<string> excluded) =>
        c.Tribes.Count > 0 && !c.Tribes.Contains("ALL") && c.Tribes.All(excluded.Contains);

    /// <summary>Opinion lines on the leading direction: pieces it shares with the runner-up, or a lead too thin to trust.</summary>
    static List<ArchetypeFit> AddTransitions(List<ArchetypeFit> dirs)
    {
        if (dirs.Count < 2) return dirs;
        var lead = dirs[0];
        var extra = new List<AdviceLine>();

        foreach (var other in dirs.Skip(1))
        {
            var shared = lead.HaveCore.Select(c => c.CardId).Intersect(other.HaveCore.Select(c => c.CardId)).ToList();
            if (shared.Count > 0)
            {
                var names = lead.HaveCore.Where(c => shared.Contains(c.CardId)).Select(c => c.Name);
                extra.Add(new(AdviceSource.Rule,
                    $"Карты, нужные и для «{lead.Name}», и для «{other.Name}»: {AdviceText.Names(names)}. "
                    + "Обычно между такими направлениями можно переключиться, не потеряв их."));
            }
        }

        var close = dirs.Skip(1).FirstOrDefault(d => Math.Round(lead.Fit - d.Fit, 2) < 0.1);
        if (close is not null)
            extra.Add(new(AdviceSource.Rule,
                $"Лидер выбран слабо: «{lead.Name}» и «{close.Name}» отличаются меньше чем на 10 пунктов покрытия. "
                + "Обычно с выбором ждут, пока придут ключевые существа одного из направлений."));

        if (extra.Count == 0) return dirs;
        dirs[0] = lead with { Lines = lead.Lines.Concat(extra).ToList() };
        return dirs;
    }

    // ---- Synergies ------------------------------------------------------------------------------

    static List<AdviceLine> Synergies(List<Piece> board)
    {
        var lines = new List<AdviceLine>();
        var shielded = board.Where(p => p.View.Keywords.HasFlag(MinionKeywords.DivineShield) || p.Card.Mechanics.Contains("DIVINE_SHIELD")).ToList();
        var rally = board.Where(p => p.Card.Mechanics.Contains("BACON_RALLY")).ToList();
        var deathrattle = board.Where(p => p.View.Keywords.HasFlag(MinionKeywords.Deathrattle) || p.Card.Mechanics.Contains("DEATHRATTLE")).ToList();
        var battlecry = board.Where(p => p.Card.Mechanics.Contains("BATTLECRY")).ToList();

        void Pair(Regex payoff, List<Piece> partners, string label, string whatPartners)
        {
            foreach (var p in board.Where(p => payoff.IsMatch(p.Card.TextEn)))
            {
                var others = partners.Where(x => x.View.EntityId != p.View.EntityId).Select(x => x.Card.Name).ToList();
                if (others.Count > 0)
                    lines.Add(new(AdviceSource.Fact,
                        $"«{p.Card.Name}» ({label}): «{AdviceText.Clip(p.Card.Text)}». {whatPartners}: {AdviceText.Names(others)}."));
            }
        }

        Pair(ShieldPayoff, shielded, "божественный щит", "Со щитом на столе");
        Pair(RallyPayoff, rally, "боевой раж", "С боевым ражем на столе");
        Pair(DeathrattlePayoff, deathrattle, "предсмертные хрипы", "С предсмертным хрипом на столе");
        Pair(BattlecryPayoff, battlecry, "боевые кличи", "С боевым кличем на столе");

        // A card whose text names a tribe, next to other minions of that tribe.
        foreach (var p in board)
            foreach (var tribe in TribeNames.Mentioned(p.Card.TextEn))
            {
                var mates = board.Where(x => x.View.EntityId != p.View.EntityId
                        && (x.Card.Tribes.Contains(tribe) || x.Card.Tribes.Contains("ALL")))
                    .Select(x => x.Card.Name).ToList();
                if (mates.Count > 0)
                    lines.Add(new(AdviceSource.Fact,
                        $"«{p.Card.Name}»: «{AdviceText.Clip(p.Card.Text)}». В тексте названо племя «{TribeNames.Ru(tribe)}», оно на столе у: {AdviceText.Names(mates)}."));
            }

        return lines.Distinct().Take(8).ToList();
    }

    // ---- Gaps -----------------------------------------------------------------------------------

    static List<AdviceLine> Gaps(MatchSnapshot m, List<Piece> board, List<ArchetypeFit> directions, int tier)
    {
        var gaps = new List<AdviceLine>();
        var round = m.Round;

        // Round 5 is roughly where a build has to show its first pieces; by round 8 it needs two.
        if (board.Count > 0 && directions.Count > 0)
        {
            var core = directions[0].HaveCore.Count;
            if (round >= 8 && core < 2)
                gaps.Add(new(AdviceSource.Rule, $"К {round}-му ходу обычно уже собраны минимум два ключевых существа направления, у вас {core}."));
            else if (round >= 5 && core == 0)
                gaps.Add(new(AdviceSource.Rule, $"К {round}-му ходу обычно уже есть хотя бы одно ключевое существо направления, у вас пока нет."));
        }

        if (round >= 8 && board.Count > 0)
        {
            var protectedMinion = board.Any(p => p.View.Keywords.HasFlag(MinionKeywords.Taunt)
                || p.View.Keywords.HasFlag(MinionKeywords.DivineShield)
                || p.View.Keywords.HasFlag(MinionKeywords.Reborn)
                || p.Card.Mechanics.Contains("TAUNT") || p.Card.Mechanics.Contains("DIVINE_SHIELD") || p.Card.Mechanics.Contains("REBORN"));
            if (!protectedMinion)
                gaps.Add(new(AdviceSource.Rule,
                    $"К {round}-му ходу на столе обычно есть провокация, божественный щит или перерождение: так армия переживает первые удары. У вас их нет."));
        }

        if (m.TavernTier is { } have && round > 0)
        {
            var expected = ExpectedTier(round);
            if (have <= expected - 2)
                gaps.Add(new(AdviceSource.Rule,
                    $"На {round}-м ходу таверна обычно уже {expected}-го уровня, у вас {have}-й. Часть планов намеренно задерживает повышение таверны."));
        }

        return gaps;
    }

    // ---- Positioning ----------------------------------------------------------------------------

    static List<AdviceLine> Positioning(List<Piece> board)
    {
        var lines = new List<AdviceLine>();
        if (board.Count == 0) return lines;

        for (var i = 0; i < board.Count; i++)
        {
            var c = board[i].Card;
            var slot = i + 1;
            var name = $"«{c.Name}»";
            if (LeftMost.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact,
                    $"{name}: по тексту эффект привязан к самому левому существу. Сейчас оно стоит {slot}-м слева"
                    + (i == 0 ? "" : ", не первым") + "."));
            if (RightMost.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact,
                    $"{name}: по тексту эффект привязан к самому правому существу. Сейчас оно стоит {slot}-м слева"
                    + (i == board.Count - 1 ? ", то есть правее всех" : ", не последним") + "."));
            if (Adjacent.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact,
                    $"{name}: по тексту эффект действует на соседей. Сейчас рядом: {Neighbours(board, i, left: true, right: true)}."));
            else if (ToTheLeft.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact, $"{name}: по тексту эффект направлен налево. Сейчас слева: {Neighbours(board, i, left: true, right: false)}."));
            else if (ToTheRight.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact, $"{name}: по тексту эффект направлен направо. Сейчас справа: {Neighbours(board, i, left: false, right: true)}."));
            if (AttacksAtOnce.IsMatch(c.TextEn))
                lines.Add(new(AdviceSource.Fact, $"{name}: в тексте есть «атакует сразу», такой удар идёт вне обычной очереди атаки."));
        }

        lines.Add(new(AdviceSource.Limit,
            "Лучший порядок зависит от армии противника, которой вы не видите. Это не расчёт, а то, что написано на картах."));
        return lines;
    }

    static string Neighbours(List<Piece> board, int i, bool left, bool right)
    {
        var names = new List<string>();
        if (left && i > 0) names.Add(board[i - 1].Card.Name);
        if (right && i < board.Count - 1) names.Add(board[i + 1].Card.Name);
        return names.Count == 0 ? "никого" : AdviceText.Names(names);
    }
}
