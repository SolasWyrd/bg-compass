// Monte Carlo combat simulator ported from bgtracker sim/engine.py (MIT License); C# port for BG Compass.
using BgCompass.Core.Cards;

namespace BgCompass.Core.Sim;

/// <summary>Estimates a Battlegrounds combat by replaying it thousands of times from the logged boards.</summary>
public static class CombatSimulator
{
    /// <summary>What the simulation does not know about, to be shown next to every number it produces.</summary>
    public const string Limits =
        "Не учитываются героические способности, тринкеты, аномалии, награды за задания и тёмные дары: "
        + "в логе боя их влияние на доски не видно. Эффекты, которые сработали до начала боя, уже в характеристиках существ.";

    /// <summary>The odds stay honest only while few cards are guessed; past this the simulator refuses.</summary>
    internal const int MaxUnmodelled = 10;

    const double PriorWin = 0.455, PriorTie = 0.09, PriorLoss = 0.455;
    const double EpsBase = 0.04, EpsPerUnmodelled = 0.02, EpsMax = 0.25;

    public static SimResult Run(CombatSnapshot s, CardCatalog cards, int iterations = 3000, int? seed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        ct.ThrowIfCancellationRequested();

        if (!s.Complete)
            return SimResult.Unavailable("Состав досок в логе неполный"
                + (string.IsNullOrWhiteSpace(s.IncompleteReason) ? "" : $" ({s.IncompleteReason})") + ": бой не просчитать.");
        if (s.Friendly is null || s.Enemy is null)
            return SimResult.Unavailable("Состав одной из досок неизвестен: бой не просчитать.");
        if (s.Friendly.Count == 0 || s.Enemy.Count == 0)
            return SimResult.Unavailable("На одной из сторон нет существ: считать нечего.");
        if (s.Friendly.Count > Fighter.MaxBoard || s.Enemy.Count > Fighter.MaxBoard)
            return SimResult.Unavailable($"На доске больше {Fighter.MaxBoard} существ: данные боя повреждены.");

        var book = new ScriptBook(cards);
        var unmodelledIds = new HashSet<string>(StringComparer.Ordinal);
        var unmodelledNames = new List<string>();
        var ta = Build(s.Friendly, book, unmodelledIds, unmodelledNames, out var error);
        if (error is not null) return SimResult.Unavailable(error);
        var tb = Build(s.Enemy, book, unmodelledIds, unmodelledNames, out error);
        if (error is not null) return SimResult.Unavailable(error);
        if (unmodelledIds.Count > MaxUnmodelled)
            return SimResult.Unavailable(
                $"Слишком много существ с несмоделированными эффектами ({unmodelledIds.Count}): {string.Join(", ", unmodelledNames)}. Честных шансов не получится.");

        var rng = new Fighter(seed is int sd
            ? (ulong)sd * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL
            : (ulong)Random.Shared.NextInt64());
        var dealt = new int[iterations];
        var taken = new int[iterations];
        int wins = 0, ties = 0, losses = 0;
        for (var i = 0; i < iterations; i++)
        {
            if ((i & 15) == 0) ct.ThrowIfCancellationRequested();
            var r = rng.Fight(ta!, tb!);
            if (r.Result > 0) dealt[wins++] = r.Damage;
            else if (r.Result < 0) taken[losses++] = r.Damage;
            else ties++;
        }

        var n = (double)iterations;
        double rw = wins / n, rt = ties / n, rl = losses / n;
        var eps = Math.Min(EpsMax, EpsBase + EpsPerUnmodelled * unmodelledIds.Count);

        // Real BG face damage = the winner's tavern tier plus the survivors' tiers.
        var tierA = s.FriendlyTier is > 0 ? s.FriendlyTier : null;
        var tierB = s.EnemyTier is > 0 ? s.EnemyTier : null;
        var lethal = 0.0;
        if (tierB is int tb2 && s.FriendlyHealth is int hp and > 0)
        {
            var lethalFights = 0;
            for (var i = 0; i < losses; i++) if (taken[i] + tb2 >= hp) lethalFights++;
            lethal = (1 - eps) * (lethalFights / n) + eps * PriorLoss * 0.5;
        }

        int? p10 = null, p50 = null, p90 = null, dealtP50 = null;
        if (losses > 0)
        {
            Array.Sort(taken, 0, losses);
            var hero = tierB ?? 0;
            p10 = Rank(taken, losses, 0.10) + hero;
            p50 = Rank(taken, losses, 0.50) + hero;
            p90 = Rank(taken, losses, 0.90) + hero;
        }
        if (wins > 0)
        {
            Array.Sort(dealt, 0, wins);
            dealtP50 = Rank(dealt, wins, 0.50) + (tierA ?? 0);
        }

        return new SimResult(
            true, null,
            (1 - eps) * rw + eps * PriorWin,
            (1 - eps) * rt + eps * PriorTie,
            (1 - eps) * rl + eps * PriorLoss,
            p10, p50, p90, dealtP50, lethal, iterations, unmodelledNames);
    }

    /// <summary>Nearest-rank percentile of the first <paramref name="count"/> sorted values.</summary>
    static int Rank(int[] sorted, int count, double p) => sorted[Math.Max(1, (int)Math.Ceiling(p * count)) - 1];

    static Minion[]? Build(IReadOnlyList<MinionView> views, ScriptBook book, HashSet<string> ids, List<string> names, out string? error)
    {
        error = null;
        var result = new List<Minion>(views.Count);
        foreach (var v in views.OrderBy(x => x.Position))
        {
            if (string.IsNullOrWhiteSpace(v.CardId))
            {
                error = "На доске есть существо без опознанной карты: бой не просчитать.";
                return null;
            }
            if (v.Health <= 0)
            {
                error = $"У существа «{v.Name}» в логе нет здоровья: данные боя повреждены.";
                return null;
            }
            var m = book.Template(v, out var entry, out var unmodelled, out _)!;
            if (unmodelled && ids.Add(entry?.Base.Id ?? v.CardId)) names.Add(entry?.Base.Name ?? v.Name);
            result.Add(m);
        }
        return result.ToArray();
    }
}
