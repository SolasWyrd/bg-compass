using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.Logs;

namespace BgCompass.App.Runtime;

public enum SimStatus { None, Running, Done }

public sealed record SimView(SimStatus Status, SimResult? Result)
{
    public static readonly SimView None = new(SimStatus.None, null);
}

/// <param name="Ready">False until the embedded card snapshot is loaded.</param>
/// <param name="Refreshing">A download from HearthstoneJSON is in progress.</param>
public sealed record CardsStatus(bool Ready, bool Refreshing, CardDataVersion? Version, string? Warning, int? GameBuild);

/// <param name="PowerLogPath">Null: no Power.log found.</param>
/// <param name="Config">Null when log.config could not be examined.</param>
public sealed record LogStatus(string? PowerLogPath, LogConfigStatus? Config, string ConfigPath, string? Override);

/// <summary>Everything the panels show, computed off the UI thread. Immutable.</summary>
public sealed record UiState(
    MatchSnapshot Match,
    HeroPickAdvice? Hero,
    BoardAdvice? Board,
    SimView Sim,
    IReadOnlySet<string> AbsentTribes,
    CardsStatus Cards,
    LogStatus Log,
    CardCatalog? Catalog,
    bool IsReplay,
    int Revision)
{
    public static readonly UiState Initial = new(
        MatchSnapshot.Empty, null, null, SimView.None, new HashSet<string>(),
        new CardsStatus(false, false, null, null, null),
        new LogStatus(null, null, "", null), null, false, 0);

    /// <summary>Version (e.g. "0.2.0") downloaded and installed, waiting for a restart; null when there is nothing to restart for.</summary>
    public string? UpdateReadyVersion { get; init; }

    /// <summary>Recruit phase: the current board against the next opponent's last seen one.</summary>
    public SimView Preview { get; init; } = SimView.None;
    public CombatSnapshot? PreviewCombat { get; init; }

    /// <summary>Recruit phase: what this turn's gold should go to. Null outside it and while there is no card data.</summary>
    public TurnPlan? Turn { get; init; }

    /// <summary>Why the in-game layer was switched off for this run; null while it is fine.</summary>
    public string? FieldProblem { get; init; }

    public bool InMatch => Match.Phase != MatchPhase.Idle;
}
