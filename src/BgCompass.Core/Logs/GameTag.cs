namespace BgCompass.Core.Logs;

/// <summary>
/// The tags the tracker reads. Names in the log are authoritative: each tag has a negative internal id so it can never
/// collide with a numeric tag the client printed without a name. Numeric forms are aliased only for the core
/// tags whose Hearthstone ids are stable.
/// </summary>
public static class GameTag
{
    public const int Zone = -1, Controller = -2, ZonePosition = -3, CardType = -4, CardRace = -5, Atk = -6, Health = -7,
        Damage = -8, Armor = -9, Premium = -10, TechLevel = -11, Taunt = -12, DivineShield = -13, Poisonous = -14,
        Venomous = -15, Reborn = -16, Windfury = -17, Stealth = -18, Deathrattle = -19, Resources = -20,
        ResourcesUsed = -21, Turn = -22, Step = -23, State = -24, PlayState = -25, HeroEntity = -26, PlayerId = -27,
        NextOpponentPlayerId = -28, LeaderboardPlace = -29, PlayerTechLevel = -30, PlayerTriples = -31,
        IsBaconPoolMinion = -32;

    private static readonly Dictionary<string, int> ByName = new(StringComparer.Ordinal)
    {
        ["ZONE"] = Zone, ["CONTROLLER"] = Controller, ["ZONE_POSITION"] = ZonePosition, ["CARDTYPE"] = CardType,
        ["CARDRACE"] = CardRace, ["ATK"] = Atk, ["HEALTH"] = Health, ["DAMAGE"] = Damage, ["ARMOR"] = Armor,
        ["PREMIUM"] = Premium, ["TECH_LEVEL"] = TechLevel, ["TAUNT"] = Taunt, ["DIVINE_SHIELD"] = DivineShield,
        ["POISONOUS"] = Poisonous, ["VENOMOUS"] = Venomous, ["REBORN"] = Reborn, ["WINDFURY"] = Windfury,
        ["STEALTH"] = Stealth, ["DEATHRATTLE"] = Deathrattle, ["RESOURCES"] = Resources,
        ["RESOURCES_USED"] = ResourcesUsed, ["TURN"] = Turn, ["STEP"] = Step, ["STATE"] = State,
        ["PLAYSTATE"] = PlayState, ["HERO_ENTITY"] = HeroEntity, ["PLAYER_ID"] = PlayerId,
        ["NEXT_OPPONENT_PLAYER_ID"] = NextOpponentPlayerId, ["PLAYER_LEADERBOARD_PLACE"] = LeaderboardPlace,
        ["PLAYER_TECH_LEVEL"] = PlayerTechLevel, ["PLAYER_TRIPLES"] = PlayerTriples,
        ["IS_BACON_POOL_MINION"] = IsBaconPoolMinion,
    };

    // Stable Hearthstone ids for the core tags; BG-specific ones are matched by name only.
    private static readonly Dictionary<int, int> Numeric = new()
    {
        [49] = Zone, [50] = Controller, [263] = ZonePosition, [202] = CardType, [200] = CardRace, [47] = Atk,
        [45] = Health, [44] = Damage, [292] = Armor, [12] = Premium, [1440] = TechLevel, [26] = Resources,
        [25] = ResourcesUsed, [20] = Turn, [19] = Step, [204] = State, [17] = PlayState,
    };

    private static readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> NameLookup =
        ByName.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Internal id for a tag name or number, or 0 when the tracker does not care about it.</summary>
    public static int Resolve(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return 0;
        if (name[0] is >= '0' and <= '9')
            return int.TryParse(name, out var n) && Numeric.TryGetValue(n, out var id) ? id : 0;
        return NameLookup.TryGetValue(name, out var v) ? v : 0;
    }
}

/// <summary>Enum-valued tags are printed by name; this turns the names the tracker needs into numbers.</summary>
public static class TagValues
{
    public const int ZonePlay = 1, ZoneDeck = 2, ZoneHand = 3, ZoneGraveyard = 4, ZoneRemoved = 5, ZoneSetAside = 6, ZoneSecret = 7;
    public const int TypeGame = 1, TypePlayer = 2, TypeHero = 3, TypeMinion = 4, TypeSpell = 5, TypeEnchantment = 6, TypeHeroPower = 10;
    public const int PlayPlaying = 1, PlayWinning = 4, PlayLosing = 5, PlayWon = 6, PlayLost = 7, PlayTied = 8, PlayDisconnected = 9, PlayConceded = 10;
    public const int StateRunning = 1, StateComplete = 2;

    public static int Zone(ReadOnlySpan<char> s) => s switch
    {
        "PLAY" => ZonePlay, "DECK" => ZoneDeck, "HAND" => ZoneHand, "GRAVEYARD" => ZoneGraveyard,
        "REMOVEDFROMGAME" => ZoneRemoved, "SETASIDE" => ZoneSetAside, "SECRET" => ZoneSecret, _ => Number(s),
    };

    public static int CardType(ReadOnlySpan<char> s) => s switch
    {
        "GAME" => TypeGame, "PLAYER" => TypePlayer, "HERO" => TypeHero, "MINION" => TypeMinion, "SPELL" => TypeSpell,
        "ENCHANTMENT" => TypeEnchantment, "HERO_POWER" => TypeHeroPower, _ => Number(s),
    };

    public static int PlayState(ReadOnlySpan<char> s) => s switch
    {
        "PLAYING" => PlayPlaying, "WINNING" => PlayWinning, "LOSING" => PlayLosing, "WON" => PlayWon,
        "LOST" => PlayLost, "TIED" => PlayTied, "DISCONNECTED" => PlayDisconnected, "CONCEDED" => PlayConceded,
        _ => Number(s),
    };

    public static int State(ReadOnlySpan<char> s) => s switch
    {
        "RUNNING" => StateRunning, "COMPLETE" => StateComplete, _ => Number(s),
    };

    public static int Number(ReadOnlySpan<char> s) => int.TryParse(s, out var n) ? n : 0;

    /// <summary>Converts a tag value for the given internal tag id.</summary>
    public static int Convert(int tag, ReadOnlySpan<char> text) => tag switch
    {
        GameTag.Zone => Zone(text),
        GameTag.CardType => CardType(text),
        GameTag.PlayState => PlayState(text),
        GameTag.State => State(text),
        GameTag.CardRace or GameTag.Step => 0,
        _ => Number(text),
    };

    private static readonly Dictionary<int, string> RaceNames = new()
    {
        [14] = "MURLOC", [15] = "DEMON", [17] = "MECHANICAL", [18] = "ELEMENTAL", [20] = "BEAST", [21] = "TOTEM",
        [23] = "PIRATE", [24] = "DRAGON", [26] = "ALL", [43] = "QUILBOAST",
    };

    /// <summary>Normalises a CARDRACE value: names stay as printed (upper case), known numbers become names.</summary>
    public static string? Race(string text) =>
        text.Length > 0 && text[0] is >= '0' and <= '9'
            ? RaceNames.GetValueOrDefault(Number(text))
            : text.ToUpperInvariant();
}
