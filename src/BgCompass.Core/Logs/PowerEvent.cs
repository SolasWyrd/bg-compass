namespace BgCompass.Core.Logs;

/// <summary>Which Power.log writer produced a line. PowerTaskList is in sync with the screen; GameState runs ahead of it.</summary>
public enum PowerSource : byte { Other, GameState, PowerTaskList }

public enum PowerKind : byte
{
    None,
    CreateGame,
    GameEntity,
    Player,
    FullEntityCreate,
    FullEntityUpdate,
    ShowEntity,
    ChangeEntity,
    HideEntity,
    TagChange,
    /// <summary>An indented "tag=X value=Y" line belonging to the entity announced just before it.</summary>
    TagValue,
    BlockStart,
    BlockEnd,
    MetaData,
    /// <summary>GameState.DebugPrintGame: BuildNumber, GameType or a PlayerID/PlayerName pair.</summary>
    GameInfo,
    ChoicesHeader,
    ChoicesEntity,
    ChosenHeader,
    ChosenEntity,
}

public enum EntityRefKind : byte { None, Id, Name, Game }

/// <summary>How a log line points at an entity: a bracket block, a bare number, "GameEntity" or a player name.</summary>
public readonly record struct EntityRef(
    EntityRefKind Kind, int Id, string? Name, string? CardId, int Zone, int ZonePos, int Player)
{
    public static readonly EntityRef None = default;
    public bool IsEmpty => Kind == EntityRefKind.None;
}

/// <summary>One parsed log line. Fields beyond Kind/Source/Time/Indent are filled only where the kind uses them.</summary>
public sealed class PowerEvent
{
    public static readonly PowerEvent None = new();

    public PowerKind Kind { get; init; }
    public PowerSource Source { get; init; }
    /// <summary>Time of day of the line; the date is not in the log.</summary>
    public TimeSpan Time { get; init; }
    public int Indent { get; init; }
    public EntityRef Entity { get; init; }
    /// <summary>EntityID for GameEntity/Player/FULL_ENTITY, choice id, or a number for GameInfo/MetaData/ChoicesEntity index.</summary>
    public int Id { get; init; }
    /// <summary>PlayerID for Player and GameInfo player rows.</summary>
    public int PlayerId { get; init; }
    public string? CardId { get; init; }
    /// <summary>Tag id as <see cref="GameTag"/> knows it; unknown names are dropped by the parser (Kind None).</summary>
    public int Tag { get; init; }
    public int Value { get; init; }
    public string? ValueText { get; init; }
    /// <summary>BlockType, META name, GameInfo key, ChoiceType or the player name of a choices header.</summary>
    public string? Text { get; init; }
    public string? Text2 { get; init; }
    public long AccountHi { get; init; }
    public long AccountLo { get; init; }
}
