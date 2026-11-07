using BgCompass.Core;
using BgCompass.Core.Advice;

namespace BgCompass.App.ViewModels;

/// <summary>One number of the turn: on the field and in the panel. Facts from the log, never a rule's guess.</summary>
public sealed record TurnStat(string Value, string Caption, string Title, string Body, bool Accent = false, bool Alert = false, bool Muted = false)
{
    public string Tip => Body.Length > 0 ? $"{Title}\n{Body}" : Title;
}

public static class TurnStats
{
    const string Unknown = "–";

    public static IReadOnlyList<TurnStat> From(TurnPlan p)
    {
        var d = p.Damage;
        var enough = p.Gold is int g && p.UpgradeCost is int c && g >= c;
        var capNote = d.Cap is int cap && d.DealtUncapped > cap ? $" Лимит режет до {cap}." : "";
        var takenAge = d.TakenBoardAge is > 0 ? $"Доска соперника {Ru.RoundsAgo(d.TakenBoardAge.Value)}: к бою она изменится." : "Доска соперника этого раунда.";
        var lethal = d.Taken is int t && p.Health is int hp && t >= hp;
        return
        [
            new($"{p.Tier}/{p.MaxTier}", "таверна", $"Таверна {p.Tier} из {p.MaxTier}", ""),
            new(p.Gold?.ToString() ?? Unknown, "золото", "Золото сейчас", "", Accent: p.Gold > 0),
            p.UpgradeCost is int cost
                ? new(cost.ToString(), "прокачка", $"Прокачка до {p.Tier + 1}: {cost}",
                    p.UpgradeWhy.Length > 0 ? p.UpgradeWhy : enough ? "Золота хватает." : p.Gold is int have ? $"Не хватает {cost - have}." : "", Accent: enough)
                : new(p.Tier >= p.MaxTier ? "макс" : Unknown, "прокачка", p.Tier >= p.MaxTier ? "Таверна на последнем уровне" : "Цену прокачки ещё не видно в логе", ""),
            p.MainTribe is { } tribe
                ? new(p.MainTribeCount.ToString(), Ru.Tribe(tribe).ToLowerInvariant(), Ru.Tribe(tribe), "Столько существ этого типа у тебя на столе и в руке.")
                : new(Unknown, "тип", "Основного типа пока нет", "Нужны хотя бы два существа одного типа."),
            new(d.Dealt.ToString(), "бьёшь", "Максимум, что снимешь за победу",
                $"Таверна {p.Tier} + уровни твоих существ, если выживут все: {d.DealtUncapped}.{capNote}"),
            d.Taken is int taken
                ? new(taken.ToString(), "получишь", "Максимум, что снимут за поражение", $"Таверна соперника + уровни его существ. {takenAge}" + (lethal ? " Это смертельно." : ""), Alert: lethal, Muted: !lethal && d.TakenBoardAge > 0)
                : new(Unknown, "получишь", "Следующего соперника ещё не видели", ""),
            d.Cap is int limit
                ? new(limit.ToString(), "лимит", "Лимит урона за бой в этом раунде", "Больше за один бой снять нельзя. Лимит растёт с раундами.")
                : new(Unknown, "лимит", "Лимит урона не виден в логе", ""),
        ];
    }
}
