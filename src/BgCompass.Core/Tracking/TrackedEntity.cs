using BgCompass.Core.Logs;

namespace BgCompass.Core.Tracking;

/// <summary>Everything the tracker keeps about one log entity. Fields are plain values; 0 means "not seen".</summary>
internal sealed class TrackedEntity
{
    public TrackedEntity(int id) => Id = id;

    public int Id;
    public string? CardId;
    public string? Name;
    public int Controller, Zone, ZonePos, CardType;
    public int Atk, Health, Damage, Armor, Premium, TechLevel, IsPool, Cost = -1, MaxTech;
    public string? Race;
    public MinionKeywords Keywords;

    // Player entities
    public bool IsPlayer, IsGame;
    public long AccountHi, AccountLo;
    public int Resources, ResourcesUsed;
    public bool HasResources;

    // Hero entities (leaderboard seats) and player entities share these
    public int PlayerId;
    public int? Place, PlayerTech, Triples;

    /// <summary>Order of the last leaderboard place written here: the game keeps a seat's tier, armor and damage on that copy.</summary>
    public int SeatTick;

    /// <summary>Created while a combat round was running: a combat copy, not a shop or seat entity.</summary>
    public bool BornInCombat;

    public bool IsHero => CardType == TagValues.TypeHero;
    public bool IsMinion => CardType == TagValues.TypeMinion;
}
