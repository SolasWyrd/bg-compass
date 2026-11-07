namespace BgCompass.Core;

// Shared contract between the log tracker, the advisor, the simulator and the UI.
// Everything here is immutable so the UI thread can hold a snapshot safely.

public enum MatchPhase { Idle, HeroPick, Recruit, Combat, GameOver }

[Flags]
public enum MinionKeywords
{
    None = 0,
    Taunt = 1,
    DivineShield = 2,
    Poisonous = 4,
    Venomous = 8,
    Reborn = 16,
    Windfury = 32,
    Stealth = 64,
    Deathrattle = 128,
}

public sealed record MinionView(
    int EntityId,
    string CardId,
    string Name,
    int Attack,
    int Health,
    int Position,
    bool Golden,
    MinionKeywords Keywords,
    IReadOnlyList<string> Tribes,
    int TechLevel);

public sealed record HeroOption(int EntityId, string CardId, string Name, int Position);

/// <summary>One lobby seat as the leaderboard exposes it. Null = the log has not said.</summary>
public sealed record PlayerView(
    int PlayerId,
    string HeroCardId,
    string HeroName,
    bool IsLocal,
    int? Place,
    int? TavernTier,
    int? Health,
    int? Triples,
    bool IsDead,
    bool IsNextOpponent,
    LastSeenBoard? LastBoard);

/// <summary>A board revealed in combat. It ages from <see cref="Round"/>; never present it as current.</summary>
public sealed record LastSeenBoard(int Round, IReadOnlyList<MinionView> Minions);

public enum EncounterResult { Unknown, Win, Tie, Loss }

public sealed record Encounter(
    int Round,
    int OpponentPlayerId,
    string OpponentHeroCardId,
    string OpponentHeroName,
    IReadOnlyList<MinionView> EnemyBoard,
    IReadOnlyList<MinionView> MyBoard,
    EncounterResult Result,
    int? DamageToMe,
    int? DamageToEnemy);

/// <summary>Both boards as logged before the first attack. Complete=false means do not simulate.</summary>
public sealed record CombatSnapshot(
    int Round,
    int OpponentPlayerId,
    string OpponentHeroCardId,
    IReadOnlyList<MinionView> Friendly,
    IReadOnlyList<MinionView> Enemy,
    int? FriendlyTier,
    int? EnemyTier,
    int? FriendlyHealth,
    int? EnemyHealth,
    bool Complete,
    string? IncompleteReason);

public sealed record MatchSnapshot(
    MatchPhase Phase,
    int? BuildNumber,
    int Round,
    string? LocalHeroCardId,
    string? LocalHeroName,
    int? TavernTier,
    int? Gold,
    IReadOnlyList<HeroOption> HeroOffers,
    IReadOnlyList<MinionView> Board,
    IReadOnlyList<MinionView> Shop,
    IReadOnlyList<PlayerView> Players,
    IReadOnlyList<Encounter> Encounters,
    /// <summary>Tribes proven present by a sighting in this lobby. An unseen tribe is only "not seen yet".</summary>
    IReadOnlySet<string> ConfirmedTribes,
    CombatSnapshot? Combat,
    int? FinalPlace,
    bool IsDuos = false)
{
    public static readonly MatchSnapshot Empty = new(
        MatchPhase.Idle, null, 0, null, null, null, null,
        [], [], [], [], [], new HashSet<string>(), null, null);
}

/// <summary>Written once when a game ends. MMR is deliberately absent: Power.log never states it.</summary>
public sealed record MatchRecord(
    string GameId,
    DateTimeOffset EndedAt,
    int? BuildNumber,
    string HeroCardId,
    string HeroName,
    int? Place,
    int Rounds,
    IReadOnlyList<string> FinalBoardCardIds,
    IReadOnlyList<string> ConfirmedTribes,
    string? ArchetypeId);

// ---- Advice ----------------------------------------------------------------

/// <summary>Where a line comes from. The UI must draw these differently; they are never blended.</summary>
public enum AdviceSource
{
    Fact,       // read from the log or the card database
    Personal,   // the player's own recorded games, with a sample size
    Rule,       // hand-written expert rule, dated; an opinion, not a measurement
    Limit,      // what we cannot know right now and why
}

public sealed record AdviceLine(AdviceSource Source, string Text);

public sealed record HeroAdvice(
    HeroOption Option,
    string HeroPowerText,
    int? Armor,
    int? PersonalGames,
    double? PersonalAveragePlace,
    IReadOnlyList<AdviceLine> Lines);

public sealed record HeroPickAdvice(
    IReadOnlyList<HeroAdvice> Heroes,
    /// <summary>Null when there is no honest basis to prefer one.</summary>
    string? RecommendedCardId,
    AdviceLine? RecommendationReason);

public sealed record CardRef(string CardId, string Name, int Tier, IReadOnlyList<string> Tribes, bool OnBoard, bool InShop);

public sealed record ArchetypeFit(
    string Id,
    string Name,
    string? Tribe,
    /// <summary>0..1, share of the archetype's needs the board covers. Not a win probability.</summary>
    double Fit,
    IReadOnlyList<CardRef> HaveCore,
    IReadOnlyList<CardRef> MissingCoreNow,
    IReadOnlyList<CardRef> NextTierTargets,
    IReadOnlyList<AdviceLine> Lines);

public sealed record BoardAdvice(
    IReadOnlyDictionary<string, int> TribeCounts,
    IReadOnlyList<ArchetypeFit> Directions,
    IReadOnlyList<AdviceLine> Synergies,
    IReadOnlyList<AdviceLine> Gaps,
    IReadOnlyList<AdviceLine> Positioning,
    IReadOnlyList<AdviceLine> Limits);

// ---- Simulation -------------------------------------------------------------

public sealed record SimResult(
    bool Available,
    string? UnavailableReason,
    double Win,
    double Tie,
    double Loss,
    /// <summary>Damage to the player in lost fights, percentiles; null when no fight was lost.</summary>
    int? DamageTakenP10,
    int? DamageTakenP50,
    int? DamageTakenP90,
    int? DamageDealtP50,
    double LethalRisk,
    int Iterations,
    /// <summary>Card names whose combat effects are not modelled; the odds were widened because of them.</summary>
    IReadOnlyList<string> Unmodelled)
{
    public static SimResult Unavailable(string reason) =>
        new(false, reason, 0, 0, 0, null, null, null, null, 0, 0, []);
}
