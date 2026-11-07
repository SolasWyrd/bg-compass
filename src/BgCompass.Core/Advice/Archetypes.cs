using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BgCompass.Core.Cards;

namespace BgCompass.Core.Advice;

/// <summary>
/// A role is a property a card can have ("gives Venomous", "triggers Battlecries twice"), not a card list,
/// so families follow the live card pool when a patch swaps cards. A card has a role when one of its
/// mechanics is listed or its English text matches the regex. Tribe families only take cards of their tribe,
/// cards of every tribe (ALL) and, for <see cref="Neutral"/> roles, tribeless cards.
/// </summary>
public sealed record Role(string Id, string Label, IReadOnlyList<string> Mechanics, Regex? Text, bool Neutral);

public sealed record Family(
    string Id, string Name, string Plan, string? Tribe, string Updated,
    IReadOnlyList<Role> Core, IReadOnlyList<Role> Support);

/// <summary>How much of a family one set of cards covers. Never a win chance.</summary>
public sealed record Coverage(double Fit, IReadOnlyList<CardInfo> CoreCards, IReadOnlyList<string> MissingCoreRoles);

public sealed class Archetypes
{
    /// <summary>
    /// Core roles weigh double a support role. Two cards fill a role: one card is a seed, two are a build.
    /// So with two core roles and one support role: two cards in one core role = 0.4, one card in each core
    /// role = 0.4, a full engine with support = 1.0.
    /// </summary>
    const int CardsToFillRole = 2;
    const int CoreWeight = 2;

    /// <summary>Below this the board has no clear direction; the UI must say so instead of naming a winner.</summary>
    public const double ClearDirection = 0.39;

    public string Updated { get; }
    public IReadOnlyList<Family> Families { get; }

    Archetypes(string updated, IReadOnlyList<Family> families) { Updated = updated; Families = families; }

    public Family? Get(string? id) => Families.FirstOrDefault(f => f.Id == id);

    public static Archetypes LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("archetypes.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        return Parse(JsonDocument.Parse(s).RootElement);
    }

    public static Archetypes Parse(JsonElement root)
    {
        var families = root.GetProperty("families").EnumerateArray().Select(f => new Family(
            f.GetProperty("id").GetString()!,
            f.GetProperty("name").GetString()!,
            f.GetProperty("plan").GetString()!,
            f.TryGetProperty("tribe", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            f.GetProperty("updated").GetString()!,
            Roles(f.GetProperty("core")),
            Roles(f.GetProperty("support")))).ToList();
        return new Archetypes(root.GetProperty("updated").GetString()!, families);
    }

    static List<Role> Roles(JsonElement arr) => arr.EnumerateArray().Select(r => new Role(
        r.GetProperty("id").GetString()!,
        r.GetProperty("label").GetString()!,
        r.TryGetProperty("mechanics", out var m) ? m.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
        r.TryGetProperty("text", out var t) ? new Regex(t.GetString()!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null,
        r.TryGetProperty("neutral", out var n) && n.GetBoolean())).ToList();

    public static bool Matches(Family f, Role r, CardInfo c)
    {
        if (f.Tribe is not null
            && !c.Tribes.Contains(f.Tribe) && !c.Tribes.Contains("ALL")
            && !(r.Neutral && c.Tribes.Count == 0))
            return false;
        return r.Mechanics.Any(m => c.Mechanics.Contains(m)) || (r.Text is not null && r.Text.IsMatch(c.TextEn));
    }

    public static bool IsCore(Family f, CardInfo c) => f.Core.Any(r => Matches(f, r, c));

    public Coverage Cover(Family f, IEnumerable<CardInfo> cards)
    {
        var list = cards.ToList();
        double score = 0;
        var missing = new List<string>();
        foreach (var r in f.Core)
        {
            var n = list.Count(c => Matches(f, r, c));
            score += CoreWeight * Math.Min(n, CardsToFillRole) / (double)CardsToFillRole;
            if (n == 0) missing.Add(r.Label);
        }
        foreach (var r in f.Support)
            score += Math.Min(list.Count(c => Matches(f, r, c)), CardsToFillRole) / (double)CardsToFillRole;
        var max = CoreWeight * f.Core.Count + f.Support.Count;
        var core = list.Where(c => IsCore(f, c)).DistinctBy(c => c.Id).OrderBy(c => c.Tier).ThenBy(c => c.Name).ToList();
        return new Coverage(max == 0 ? 0 : score / max, core, missing);
    }

    /// <summary>The label to store with a finished game: only when one family clearly leads, else null.</summary>
    public string? Classify(IEnumerable<CardInfo> finalBoard, IReadOnlySet<string>? excludedTribes = null)
    {
        var list = finalBoard.ToList();
        var ranked = Families
            .Where(f => f.Tribe is null || excludedTribes is null || !excludedTribes.Contains(f.Tribe))
            .Select(f => (f.Id, Fit: Cover(f, list).Fit))
            .OrderByDescending(x => x.Fit).ToList();
        // A stored label should be harder to earn than an on-screen hint: half the build covered and
        // a clear lead over the runner-up, otherwise "не определена".
        if (ranked.Count == 0 || ranked[0].Fit < 0.5) return null;
        if (ranked.Count > 1 && ranked[0].Fit - ranked[1].Fit < 0.1) return null;
        return ranked[0].Id;
    }
}

/// <summary>Tribe names as the card data spells them, and how they read in Russian and in English card text.</summary>
public static class TribeNames
{
    public static readonly IReadOnlyList<string> Real =
        ["BEAST", "DEMON", "DRAGON", "ELEMENTAL", "MECHANICAL", "MURLOC", "NAGA", "PIRATE", "QUILBOAR", "UNDEAD", "ABERRATION"];

    static readonly Dictionary<string, string> Russian = new()
    {
        ["BEAST"] = "звери", ["DEMON"] = "демоны", ["DRAGON"] = "драконы", ["ELEMENTAL"] = "элементали",
        ["MECHANICAL"] = "механизмы", ["MURLOC"] = "мурлоки", ["NAGA"] = "наги", ["PIRATE"] = "пираты",
        ["QUILBOAR"] = "свинобразы", ["UNDEAD"] = "нежить", ["ABERRATION"] = "аберрации", ["ALL"] = "все типы",
    };

    // The English card text names a tribe in singular or plural ("Mechs", "Murloc", "Undead").
    static readonly Dictionary<string, Regex> InText = new()
    {
        ["BEAST"] = new(@"\bBeasts?\b"), ["DEMON"] = new(@"\bDemons?\b"), ["DRAGON"] = new(@"\bDragons?\b"),
        ["ELEMENTAL"] = new(@"\bElementals?\b"), ["MECHANICAL"] = new(@"\bMechs?\b"), ["MURLOC"] = new(@"\bMurlocs?\b"),
        ["NAGA"] = new(@"\bNagas?\b"), ["PIRATE"] = new(@"\bPirates?\b"), ["QUILBOAR"] = new(@"\bQuilboar\b"),
        ["UNDEAD"] = new(@"\bUndead\b"), ["ABERRATION"] = new(@"\bAberrations?\b"),
    };

    public static string Ru(string tribe) => Russian.GetValueOrDefault(tribe, tribe.ToLowerInvariant());

    /// <summary>Tribes a card's English text names, in a stable order.</summary>
    public static IReadOnlyList<string> Mentioned(string textEn) =>
        Real.Where(t => InText[t].IsMatch(textEn)).ToList();
}
