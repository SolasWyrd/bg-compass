// Coverage report for the combat simulator; scripts ported from bgtracker sim/engine.py (MIT License).
using BgCompass.Core.Cards;

namespace BgCompass.Core.Sim;

/// <summary>How much of the current minion pool the simulator actually understands.</summary>
public static class SimCoverage
{
    /// <param name="Pool">Minions in the current Battlegrounds pool.</param>
    /// <param name="InCombat">Of those, minions with an effect that matters once combat has started.</param>
    /// <param name="Scripted">In-combat minions whose effects are all scripted.</param>
    /// <param name="Unscripted">In-combat minions with at least one unscripted effect; the odds widen when they are on a board.</param>
    /// <param name="WidenedAnyway">Minions on the hard list of the reference simulator: scripted as far as possible, still widening the odds.</param>
    public sealed record Report(
        int Pool, int InCombat, int Scripted, int Unscripted, int WidenedAnyway,
        IReadOnlyList<string> UnscriptedNames, IReadOnlyList<string> ScriptedNames);

    public static Report Compute(CardCatalog cards)
    {
        var book = new ScriptBook(cards);
        int inCombat = 0, scripted = 0, unscripted = 0, widened = 0;
        var un = new List<string>();
        var sc = new List<string>();
        foreach (var c in cards.PoolMinions)
        {
            var e = book.For(c);
            if (e.Hard) widened++;
            if (!e.InCombat) continue;
            inCombat++;
            if (e.Covered) { scripted++; sc.Add(c.NameEn); }
            else { unscripted++; un.Add(c.NameEn); }
        }
        return new Report(cards.PoolMinions.Count, inCombat, scripted, unscripted, widened, un, sc);
    }
}
