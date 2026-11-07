using BgCompass.Core.Logs;

namespace BgCompass.Core.Tracking;

/// <summary>Which Power.log writer drives the game state.</summary>
public enum PowerStream
{
    /// <summary>PowerTaskList.DebugPrintPower: in sync with the screen. Used by the app (2026 logs).</summary>
    PowerTaskList,
    /// <summary>GameState.DebugPrintPower: written ahead of the screen. The only stream old (2022) logs have.</summary>
    GameState,
}

/// <param name="SessionId">Identifies the log file/session; with the game start time it makes <see cref="MatchRecord.GameId"/> stable.</param>
/// <param name="BaseDate">Date of the first line of the log (the log has only times of day). Defaults to today.</param>
public sealed record MatchTrackerOptions(
    string SessionId = "session", DateOnly? BaseDate = null, PowerStream Stream = PowerStream.PowerTaskList);

/// <summary>
/// Turns Power.log lines into a <see cref="MatchSnapshot"/>. Log-only: nothing here reads game memory.
/// Feed lines from one thread; <see cref="Snapshot"/> may be read from any thread (it takes the same lock).
/// In PowerTaskList mode GameState power lines are ignored, so a combat result the game logged early never reaches the
/// state; GameState supplies only the build, the game type and the hero choices (and the game boundary).
/// </summary>
public sealed partial class MatchTracker
{
    private static readonly TimeSpan HalfDay = TimeSpan.FromHours(12);

    private readonly object _gate = new();
    private readonly string _sessionId;
    private readonly DateOnly _baseDate;
    private readonly PowerSource _wanted;

    private Game _g = new();
    private bool _poolTagSeen;
    private TimeSpan _lastTime;
    private bool _haveTime;
    private int _dayOffset;
    private int _eventDay;
    private TimeSpan _burstTime;
    private bool _changedPending;
    private bool _suppress;
    private bool _emitChanged;
    private volatile bool _hasWork;
    private InfoStash? _stash;
    private MatchSnapshot? _cache;
    private readonly List<MatchRecord> _endedOut = [];

    public MatchTracker(MatchTrackerOptions? options = null)
    {
        options ??= new MatchTrackerOptions();
        _sessionId = options.SessionId;
        _baseDate = options.BaseDate ?? DateOnly.FromDateTime(DateTime.Today);
        _wanted = options.Stream == PowerStream.GameState ? PowerSource.GameState : PowerSource.PowerTaskList;
    }

    /// <summary>Raised after a burst of lines (same timestamp) changed the state, and by <see cref="Flush"/>.</summary>
    public event Action? Changed;

    /// <summary>Raised once per game when it ends, after the <see cref="Changed"/> that shows the final state.</summary>
    public event Action<MatchRecord>? GameEnded;

    /// <summary>The current state. Immutable; safe to hand to the UI.</summary>
    public MatchSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _cache ??= BuildSnapshot();
        }
    }

    public void Feed(string? line)
    {
        lock (_gate) Process(line);
        Raise();
    }

    /// <summary>Feeds many lines, raising at most one <see cref="Changed"/> at the end. Used to replay a log tail.</summary>
    public void FeedBatch(IEnumerable<string> lines, bool raiseGameEnded = true)
    {
        lock (_gate)
        {
            _suppress = true;
            try { foreach (var l in lines) Process(l); }
            finally { _suppress = false; }
            if (_changedPending) { _changedPending = false; _emitChanged = true; _hasWork = true; }
            if (!raiseGameEnded) _endedOut.Clear();
        }
        Raise();
    }

    /// <summary>Publishes the last burst; the tailer calls this when it has read everything available.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_changedPending) { _changedPending = false; _emitChanged = true; _hasWork = true; }
        }
        Raise();
    }

    private void Raise()
    {
        if (!_hasWork) return;
        bool changed;
        MatchRecord[] ended;
        lock (_gate)
        {
            changed = _emitChanged;
            _emitChanged = false;
            ended = _endedOut.Count > 0 ? _endedOut.ToArray() : [];
            _endedOut.Clear();
            _hasWork = false;
        }
        if (changed) Changed?.Invoke();
        foreach (var r in ended) GameEnded?.Invoke(r);
    }

    // ---- line dispatch --------------------------------------------------------------------

    private void Process(string? line)
    {
        var ev = PowerParser.Parse(line);
        if (ev.Kind == PowerKind.None) return;
        _eventDay = DayFor(ev.Time);
        if (_changedPending && !_suppress && ev.Time != _burstTime)
        {
            _changedPending = false;
            _emitChanged = true;
            _hasWork = true;
        }

        switch (ev.Kind)
        {
            case PowerKind.GameInfo: OnGameInfo(ev); break;
            case PowerKind.ChoicesHeader or PowerKind.ChoicesEntity: OnChoices(ev); break;
            case PowerKind.ChosenHeader or PowerKind.ChosenEntity: break;
            default: OnPower(ev); break;
        }
    }

    private void Touch(TimeSpan time)
    {
        _cache = null;
        _changedPending = true;
        _burstTime = time;
    }

    /// <summary>Day offset for a line. Handles midnight, including the lagging PowerTaskList stream after it.</summary>
    private int DayFor(TimeSpan time)
    {
        if (!_haveTime) { _haveTime = true; _lastTime = time; return _dayOffset; }
        var d = time - _lastTime;
        if (d < -HalfDay) { _dayOffset++; _lastTime = time; return _dayOffset; }
        if (d > HalfDay) return Math.Max(0, _dayOffset - 1);
        _lastTime = time;
        return _dayOffset;
    }

    private static TimeSpan Diff(TimeSpan a, TimeSpan b)
    {
        var d = (a - b).Duration();
        return d > HalfDay ? TimeSpan.FromHours(24) - d : d;
    }

    // ---- game info (build, game type) -------------------------------------------------------

    private void OnGameInfo(PowerEvent ev)
    {
        var g = _g;
        var rel = ev.Time - g.StartTime;
        bool belongsToCurrent = g.HasStart && Diff(ev.Time, g.StartTime) <= TimeSpan.FromSeconds(5)
                                && rel > TimeSpan.FromSeconds(-2);
        if (belongsToCurrent)
        {
            ApplyInfo(g, ev);
            Touch(ev.Time);
            return;
        }
        if (_stash is null || Diff(_stash.Time, ev.Time) > TimeSpan.FromSeconds(2)) _stash = new InfoStash(ev.Time);
        _stash.Events.Add(ev);
    }

    private static void ApplyInfo(Game g, PowerEvent ev)
    {
        switch (ev.Text)
        {
            case "BuildNumber":
                g.Build = ev.Id;
                break;
            case "GameType":
                g.GameType = ev.Text2;
                g.IsDuos = ev.Text2?.Contains("DUO", StringComparison.Ordinal) == true;
                if (ev.Text2 is not null && !ev.Text2.StartsWith("GT_BATTLEGROUNDS", StringComparison.Ordinal))
                    g.Ignored = true;
                break;
            case "Player" when ev.Text2 is not null:
                g.Names[ev.PlayerId] = ev.Text2;
                ResolveLocalName(g);
                break;
        }
    }

    private static void ResolveLocalName(Game g)
    {
        if (g.LocalPid != 0 && g.Names.TryGetValue(g.LocalPid, out var n)) g.LocalName = n;
    }

    private void StartGame(TimeSpan time)
    {
        var g = new Game { HasStart = true, StartTime = time, StartDay = _eventDay, Started = true };
        _g = g;
        if (_stash is not null)
        {
            if (Diff(_stash.Time, time) <= TimeSpan.FromSeconds(2))
                foreach (var e in _stash.Events) ApplyInfo(g, e);
            _stash = null;
        }
        Touch(time);
    }

    // ---- hero choices -----------------------------------------------------------------------

    private void OnChoices(PowerEvent ev)
    {
        var g = _g;
        if (g.Ignored) return;
        if (ev.Kind == PowerKind.ChoicesHeader)
        {
            g.ChoiceIsMulligan = ev.Text == "MULLIGAN";
            if (!g.ChoiceIsMulligan) return;
            g.Offers.Clear();
            g.Started = true;
            if (g.LocalName is null && !string.IsNullOrEmpty(ev.Text2)) g.LocalName = ev.Text2;
            Touch(ev.Time);
            return;
        }
        if (!g.ChoiceIsMulligan || ev.Entity.Kind != EntityRefKind.Id || ev.Entity.CardId is null) return;
        var r = ev.Entity;
        if (r.Player > 0 && g.LocalPid != r.Player)
        {
            g.LocalPid = r.Player;
            ResolveLocalName(g);
        }
        SeedFromRef(g, r);
        g.Offers.Add(new HeroOffer(r.Id, r.CardId, r.Name ?? r.CardId));
        Touch(ev.Time);
    }

    // ---- power stream -----------------------------------------------------------------------

    private void OnPower(PowerEvent ev)
    {
        if (ev.Kind == PowerKind.CreateGame)
        {
            if (_wanted == PowerSource.PowerTaskList)
            {
                if (ev.Source == PowerSource.GameState)
                {
                    StartGame(ev.Time);
                    _g.AwaitPtlCreate = true;
                }
                else if (ev.Source == PowerSource.PowerTaskList)
                {
                    if (_g.AwaitPtlCreate) _g.AwaitPtlCreate = false;
                    else StartGame(ev.Time);
                }
            }
            else if (ev.Source == PowerSource.GameState)
            {
                StartGame(ev.Time);
            }
            return;
        }

        if (ev.Source != _wanted) return;
        var g = _g;
        if (g.Ignored) return;
        if (ev.Kind != PowerKind.TagValue) SetPending(g, null);
        g.Started = true;
        Touch(ev.Time);

        switch (ev.Kind)
        {
            case PowerKind.GameEntity:
            {
                var e = Create(g, ev.Id);
                e.IsGame = true;
                e.CardType = TagValues.TypeGame;
                g.GameEntity = e;
                SetPending(g, e);
                break;
            }
            case PowerKind.Player:
                OnPlayerLine(g, ev);
                break;
            case PowerKind.FullEntityCreate:
            {
                var e = Create(g, ev.Id);
                e.CardId = string.IsNullOrEmpty(ev.CardId) ? null : ev.CardId;
                e.BornInCombat = g.InCombat;
                SetPending(g, e);
                break;
            }
            case PowerKind.FullEntityUpdate or PowerKind.ShowEntity or PowerKind.ChangeEntity:
            {
                var e = Resolve(g, ev.Entity, create: true);
                if (e is null) break;
                if (!string.IsNullOrEmpty(ev.CardId)) e.CardId = ev.CardId;
                SetPending(g, e);
                break;
            }
            case PowerKind.TagChange or PowerKind.HideEntity:
            {
                var e = Resolve(g, ev.Entity, create: true);
                if (e is not null) ApplyTag(g, e, ev.Tag, ev.Value, ev.ValueText, immediate: true);
                break;
            }
            case PowerKind.TagValue:
                if (g.Pending is { } p) ApplyTag(g, p, ev.Tag, ev.Value, ev.ValueText, immediate: false);
                break;
            case PowerKind.BlockStart:
                OnBlockStart(g, ev);
                break;
            case PowerKind.BlockEnd:
                OnBlockEnd(g);
                break;
            case PowerKind.MetaData:
                if (ev.Text == "DAMAGE" && g.Combat is { PendingHeroAttack: { Damage: null } ha }) ha.Damage = ev.Value;
                break;
        }
    }

    private void OnPlayerLine(Game g, PowerEvent ev)
    {
        var e = Create(g, ev.Id);
        e.IsPlayer = true;
        e.CardType = TagValues.TypePlayer;
        e.PlayerId = ev.PlayerId;
        e.AccountHi = ev.AccountHi;
        e.AccountLo = ev.AccountLo;
        g.PlayerEntities[ev.PlayerId] = e;
        bool human = ev.AccountHi != 0 || ev.AccountLo != 0;
        if (human && g.LocalPid == 0) g.LocalPid = ev.PlayerId;
        if (!human && g.AiPid == 0) g.AiPid = ev.PlayerId;
        ResolveLocalName(g);
        SetPending(g, e);
    }

    private static TrackedEntity Create(Game g, int id)
    {
        var e = new TrackedEntity(id);
        g.Ents[id] = e;
        return e;
    }

    private static void SeedFromRef(Game g, EntityRef r)
    {
        if (r.Kind != EntityRefKind.Id) return;
        if (!g.Ents.TryGetValue(r.Id, out var e))
        {
            e = new TrackedEntity(r.Id) { Zone = r.Zone, ZonePos = r.ZonePos, Controller = r.Player };
            g.Ents[r.Id] = e;
        }
        if (r.Name is not null && !e.IsPlayer) e.Name = r.Name;
        e.CardId ??= r.CardId;
        if (e.Controller == 0) e.Controller = r.Player;
    }

    private TrackedEntity? Resolve(Game g, EntityRef r, bool create)
    {
        switch (r.Kind)
        {
            case EntityRefKind.Game:
                return g.GameEntity;
            case EntityRefKind.Id:
                if (r.Name is not null)
                {
                    if (create) SeedFromRef(g, r);
                    else if (g.Ents.TryGetValue(r.Id, out var known) && r.Name is not null && !known.IsPlayer) known.Name ??= r.Name;
                }
                if (g.Ents.TryGetValue(r.Id, out var e)) return e;
                if (!create) return null;
                e = new TrackedEntity(r.Id);
                g.Ents[r.Id] = e;
                return e;
            case EntityRefKind.Name:
                // The local player's battletag is the local Player entity; any other plain name is the AI player
                // entity, which Hearthstone renames to each opponent in turn.
                return ResolvePlayerByName(g, r.Name);
            default:
                return null;
        }
    }

    private void SetPending(Game g, TrackedEntity? next)
    {
        if (g.Pending is { } prev && !ReferenceEquals(prev, next)) NoteSighting(g, prev);
        g.Pending = next;
    }
}
