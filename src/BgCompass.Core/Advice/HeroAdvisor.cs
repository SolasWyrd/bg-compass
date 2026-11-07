using System.Reflection;
using System.Text.Json;
using BgCompass.Core.Cards;
using BgCompass.Core.History;

namespace BgCompass.Core.Advice;

public sealed record HeroTip(string When, IReadOnlyList<string> Bullets);

/// <summary>Hand-written hero tips (Russian). They are an editor's opinion, dated, and keyed by the base hero card id.</summary>
public sealed class HeroTips
{
    public string Updated { get; }
    readonly Dictionary<string, HeroTip> _tips;

    HeroTips(string updated, Dictionary<string, HeroTip> tips) { Updated = updated; _tips = tips; }

    public int Count => _tips.Count;
    public IEnumerable<string> Ids => _tips.Keys;
    public HeroTip? Get(string? id) => id is not null && _tips.TryGetValue(id, out var t) ? t : null;

    static readonly Lazy<HeroTips> Embedded = new(LoadEmbedded);
    public static HeroTips Default => Embedded.Value;

    static HeroTips LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("hero_tips.ru.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        var root = JsonDocument.Parse(s).RootElement;
        var tips = root.GetProperty("tips").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new HeroTip(
                p.Value.GetProperty("when").GetString()!,
                p.Value.GetProperty("bullets").EnumerateArray().Select(b => b.GetString()!).ToList()));
        return new HeroTips(root.GetProperty("updated").GetString()!, tips);
    }
}

public static class HeroAdvisor
{
    const int MinPersonalGames = 5;
    const double MinPersonalGap = 1.0;
    const int MinArmorGap = 3;

    public static HeroPickAdvice Advise(
        IReadOnlyList<HeroOption> offers, CardCatalog cards,
        IReadOnlySet<string> confirmedTribes, IReadOnlySet<string> excludedTribes,
        IReadOnlyDictionary<string, HeroStats> personal) =>
        Advise(offers, cards, confirmedTribes, excludedTribes, personal, HeroTips.Default);

    public static HeroPickAdvice Advise(
        IReadOnlyList<HeroOption> offers, CardCatalog cards,
        IReadOnlySet<string> confirmedTribes, IReadOnlySet<string> excludedTribes,
        IReadOnlyDictionary<string, HeroStats> personal, HeroTips tips)
    {
        var rows = offers.Select(o => Build(o, cards, confirmedTribes, excludedTribes, personal, tips)).ToList();
        var heroes = rows.Select(r => r.Advice).ToList();
        var (id, reason) = Recommend(rows, excludedTribes);
        return new HeroPickAdvice(heroes, id, reason);
    }

    sealed record Row(HeroAdvice Advice, string? BaseId, int? Armor, bool DependsOnExcluded, HeroStats? Stats);

    static Row Build(
        HeroOption option, CardCatalog cards, IReadOnlySet<string> confirmed, IReadOnlySet<string> excluded,
        IReadOnlyDictionary<string, HeroStats> personal, HeroTips tips)
    {
        var hero = cards.Resolve(option.CardId);
        if (hero is null)
        {
            var unknown = new HeroAdvice(option, "", null, null, null,
            [
                new(AdviceSource.Limit,
                    $"Героя «{option.Name}» ({option.CardId}) нет в данных о картах: сила, броня и советы неизвестны. Возможно, данные карт устарели."),
            ]);
            return new Row(unknown, null, null, false, null);
        }

        var lines = new List<AdviceLine>();
        var power = cards.HeroPowerOf(hero);
        var powerText = power?.Text ?? "";
        if (power is null || powerText.Length == 0)
            lines.Add(new(AdviceSource.Limit, "Описание силы героя в данных карт не найдено."));
        else
            lines.Add(new(AdviceSource.Fact,
                power.Cost is > 0 ? $"Сила героя (стоимость {power.Cost}): {powerText}" : $"Сила героя: {powerText}"));
        if (hero.Armor is { } armor) lines.Add(new(AdviceSource.Fact, $"Броня: {armor}"));

        // Tribe dependence is read from the hero power's own English text, so it follows the patch.
        var dependsOnExcluded = false;
        foreach (var tribe in TribeNames.Mentioned(power?.TextEn ?? ""))
        {
            var ru = TribeNames.Ru(tribe);
            if (excluded.Contains(tribe))
            {
                dependsOnExcluded = true;
                lines.Add(new(AdviceSource.Fact, $"Сила героя завязана на племя «{ru}», а оно исключено из этой партии."));
            }
            else if (confirmed.Contains(tribe))
                lines.Add(new(AdviceSource.Fact, $"Сила героя завязана на племя «{ru}»: в этом лобби оно уже замечено."));
            else
                lines.Add(new(AdviceSource.Limit,
                    $"Сила героя завязана на племя «{ru}»: пока не замечено в лобби. Это не значит, что его нет."));
        }

        if (tips.Get(hero.Id) is { } tip)
        {
            lines.Add(new(AdviceSource.Rule, $"Совет из bgtracker (перевод от {AdviceText.Date(tips.Updated)}): {tip.When}"));
            lines.AddRange(tip.Bullets.Select(b => new AdviceLine(AdviceSource.Rule, b)));
        }
        else
            lines.Add(new(AdviceSource.Limit, "Для этого героя советов пока нет."));

        personal.TryGetValue(hero.Id, out var stats);
        if (stats is { Games: > 0 })
        {
            var small = stats.Games < MinPersonalGames ? $" — выборка мала, меньше {MinPersonalGames} игр, вывод ненадёжен" : "";
            lines.Add(new(AdviceSource.Personal,
                $"Ваши игры: {AdviceText.Games(stats.Games)}, среднее место {AdviceText.Num(stats.AveragePlace)}{small}"));
        }
        else
            lines.Add(new(AdviceSource.Personal, "Ваших игр за этого героя пока нет."));

        var advice = new HeroAdvice(option, powerText, hero.Armor,
            stats?.Games, stats is { Games: > 0 } ? stats.AveragePlace : null, lines);
        return new Row(advice, hero.Id, hero.Armor, dependsOnExcluded, stats);
    }

    static (string? Id, AdviceLine Reason) Recommend(IReadOnlyList<Row> rows, IReadOnlySet<string> excluded)
    {
        // (a) Your own results, but only when every offered hero has enough games and the gap is real.
        if (rows.Count >= 2 && rows.All(r => r.Stats is { Games: >= MinPersonalGames }))
        {
            var ranked = rows.OrderBy(r => r.Stats!.AveragePlace).ToList();
            var gap = ranked[1].Stats!.AveragePlace - ranked[0].Stats!.AveragePlace;
            if (gap >= MinPersonalGap)
                return (ranked[0].Advice.Option.CardId, new(AdviceSource.Personal,
                    $"По вашим играм «{ranked[0].Advice.Option.Name}» идёт лучше: среднее место "
                    + $"{AdviceText.Num(ranked[0].Stats!.AveragePlace)} за {AdviceText.Games(ranked[0].Stats!.Games)} против "
                    + $"{AdviceText.Num(ranked[1].Stats!.AveragePlace)} за {AdviceText.Games(ranked[1].Stats!.Games)}. "
                    + "Это ваша личная статистика на небольшой выборке, а не общая."));
        }

        // (b) Rule: a hero whose power needs a tribe that is not in this game is out.
        var left = rows.Where(r => !r.DependsOnExcluded).ToList();
        if (left.Count == 1 && rows.Count > 1)
        {
            var dropped = rows.Where(r => r.DependsOnExcluded).Select(r => $"«{r.Advice.Option.Name}»");
            return (left[0].Advice.Option.CardId, new(AdviceSource.Rule,
                $"У {string.Join(", ", dropped)} сила героя завязана на племя, которого нет в этой партии, поэтому остаётся «{left[0].Advice.Option.Name}». Это правило, а не статистика."));
        }

        // Blizzard hands weaker heroes more armor, so a clearly lower armor hints at a stronger hero.
        if (left.Count >= 2 && left.All(r => r.Armor is not null))
        {
            var byArmor = left.OrderBy(r => r.Armor).ToList();
            if (byArmor[1].Armor - byArmor[0].Armor >= MinArmorGap)
                return (byArmor[0].Advice.Option.CardId, new(AdviceSource.Rule,
                    $"У «{byArmor[0].Advice.Option.Name}» брони {byArmor[0].Armor}, у остальных не меньше {byArmor[1].Armor}. "
                    + "Blizzard подбирает броню под силу героя: чем она ниже, тем сильнее герой по замыслу. "
                    + "Это эвристика, а не статистика."));
        }

        // (c) Nothing trustworthy: say so.
        return (null, new(AdviceSource.Limit,
            "Надёжной бесплатной статистики по героям нет, поэтому рекомендовать честно нечего. "
            + "Ориентируйтесь на силу героя, советы выше и свой план на игру."));
    }
}
