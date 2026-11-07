using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;
using BgCompass.Core.History;
using BgCompass.Core.Logs;
using BgCompass.Core.Tracking;

namespace BgCompass.App.Runtime;

/// <param name="Post">Marshals a callback onto the UI thread (Dispatcher.UIThread.Post in the app; inline in tests).</param>
/// <param name="ReplayPath">Dev mode: feed this log instead of following the live one.</param>
/// <param name="Offline">No card or art downloads (replay and tests).</param>
public sealed record HostOptions(
    DataPaths Paths, SettingsStore Settings, Action<Action> Post,
    string? ReplayPath = null, double ReplaySpeed = 1, bool GameStateStream = false, bool Offline = false,
    TimeSpan? MinUiInterval = null);

/// <summary>
/// Everything that is not a window: card data, the log tailer and tracker, advice, the simulator and the history.
/// All heavy work runs on background threads; <see cref="StateChanged"/> reaches the UI at most about five times a
/// second and only when something the panels show has changed.
/// </summary>
public sealed class AppHost : IDisposable
{
    readonly HostOptions _o;
    readonly object _gate = new();
    readonly ThrottledPublisher<UiState> _publisher;
    readonly AdvicePipeline _pipeline;
    readonly SimRunner _sim, _preview;
    string? _fieldProblem;
    readonly Archetypes _archetypes = Archetypes.LoadEmbedded();
    readonly HttpClient _http;
    readonly Timer _logWatch;
    readonly CancellationTokenSource _cts = new();
    LogTailer? _tailer;
    ReplayFeeder? _replay;
    MatchTracker _tracker;
    UiState _state = UiState.Initial;
    MatchPhase _lastPhase = MatchPhase.Idle;
    LogStatus _log = new(null, null, LogConfig.DefaultPath, null);
    bool _refreshing;
    int? _refreshedFor = -1;
    int _historyRev;
    string? _updateReady;
    bool _disposed;

    public AppHost(HostOptions options)
    {
        _o = options;
        Paths = options.Paths;
        Settings = options.Settings;
        History = new HistoryStore(Paths.Root);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BgCompass/0.1");
        Cards = new CardDataService(Paths.CardCache, _http);
        Art = new ArtCache(Paths.ArtCache, options.Offline ? null : _http);
        _pipeline = new AdvicePipeline(() => History.HeroStatsByHero(Cards.Current));
        _sim = new SimRunner(Recompute);
        _preview = new SimRunner(Recompute);
        _publisher = new ThrottledPublisher<UiState>(
            s => StateChanged?.Invoke(s), options.Post, options.MinUiInterval ?? TimeSpan.FromMilliseconds(200));
        _tracker = NewTracker("session", null);
        _logWatch = new Timer(_ => CheckLog(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public DataPaths Paths { get; }
    public SettingsStore Settings { get; }
    public HistoryStore History { get; }
    public CardDataService Cards { get; }
    public ArtCache Art { get; }
    public TribeMarks Marks { get; } = new();
    public bool IsReplay => _o.ReplayPath is not null;
    public UiState State { get { lock (_gate) return _state; } }
    public int AdvisorRuns => _pipeline.HeroRuns + _pipeline.BoardRuns;
    public int SimRuns => _sim.Runs;
    /// <summary>Set by the shell when self-update applies to this run; the UI calls <c>Updater?.Restart()</c>.</summary>
    public Updater? Updater { get; set; }
    public int UiDeliveries => _publisher.Delivered;

    /// <summary>Raised on the UI thread with the newest state.</summary>
    public event Action<UiState>? StateChanged;
    /// <summary>Raised on a background thread after a finished game was written to the history.</summary>
    public event Action<MatchRecord>? GameRecorded;

    public void Start()
    {
        AppLog.Info($"Старт. Данные: {Paths.Root}. Режим: {(IsReplay ? "повтор лога" : "живой")}.");
        Cards.LoadCached();
        Cards.Changed += OnCardsChanged;
        CheckLog(force: true);
        if (_o.ReplayPath is { } path)
        {
            _replay = new ReplayFeeder(path, _o.ReplaySpeed, l => _tracker.Feed(l), () => _tracker.Flush());
            _replay.Start();
        }
        else
        {
            _tailer = new LogTailer(() => LogLocator.FindPowerLog(Settings.Value.LogFolder));
            _tailer.SessionStarted += OnSession;
            _tailer.LinesRead += OnLines;
            _tailer.Start();
            _logWatch.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        Recompute();
        if (!_o.Offline) _ = RefreshCardsAsync(null);
    }

    MatchTracker NewTracker(string session, DateOnly? baseDate)
    {
        var t = new MatchTracker(new MatchTrackerOptions(
            session, baseDate, _o.GameStateStream ? PowerStream.GameState : PowerStream.PowerTaskList));
        t.Changed += OnTrackerChanged;
        t.GameEnded += OnGameEnded;
        return t;
    }

    void OnSession(LogSession s)
    {
        AppLog.Info($"Лог: {s.Path}");
        MatchTracker old;
        lock (_gate)
        {
            old = _tracker;
            _tracker = NewTracker(s.SessionId, s.BaseDate);
        }
        old.Changed -= OnTrackerChanged;
        old.GameEnded -= OnGameEnded;
        Marks.Clear();
        CheckLog(force: true);
    }

    void OnLines(IReadOnlyList<string> lines, bool initialReplay)
    {
        MatchTracker t;
        lock (_gate) t = _tracker;
        t.FeedBatch(lines);
        t.Flush();
        if (initialReplay) AppLog.Info($"Подхвачена игра в процессе: {lines.Count} строк лога");
    }

    void OnTrackerChanged()
    {
        var snap = _tracker.Snapshot;
        if (snap.BuildNumber is int build && !_o.Offline) _ = RefreshCardsAsync(build);
        Recompute();
    }

    void OnGameEnded(MatchRecord r)
    {
        try
        {
            var cards = Cards.Current;
            var archetype = HistoryStore.ClassifyBoard(r.FinalBoardCardIds, cards, _archetypes);
            var stored = r with { ArchetypeId = archetype };
            var added = History.Append(stored);
            AppLog.Info($"Игра окончена: {r.HeroName}, место {r.Place?.ToString() ?? "?"}, раундов {r.Rounds}; " + (added ? "записана в историю" : "уже была в истории"));
            if (added)
            {
                Interlocked.Increment(ref _historyRev);
                GameRecorded?.Invoke(stored);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Не удалось записать игру в историю", e);
        }
        Recompute();
    }

    /// <summary>The overlay switched the in-game layer off; settings say why.</summary>
    public void SetFieldProblem(string problem)
    {
        lock (_gate) _fieldProblem = problem;
        Recompute();
    }

    /// <summary>Called by the updater (any thread) once a new version is installed.</summary>
    public void SetUpdateReady(string? version)
    {
        lock (_gate) _updateReady = version;
        Recompute();
    }

    void OnCardsChanged() => Recompute();

    async Task RefreshCardsAsync(int? build)
    {
        lock (_gate)
        {
            if (_disposed || _refreshing || _refreshedFor == build) return;
            _refreshing = true;
            _refreshedFor = build;
        }
        Recompute();
        try { await Cards.RefreshAsync(build, _cts.Token); }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Warn($"Обновление карт: {e.Message}"); }
        finally
        {
            lock (_gate) _refreshing = false;
            AppLog.Info($"Карты: сборка {Cards.Current.Version.Build?.ToString() ?? "?"}, {Cards.Current.Version.Source}" + (Cards.Warning is { } w ? $" ({w})" : ""));
            Recompute();
        }
    }

    /// <summary>The Обновить button: asks HearthstoneJSON for the build the log reported (or the latest).</summary>
    public void RefreshCardsNow()
    {
        if (_o.Offline) return;
        lock (_gate) _refreshedFor = -1;
        _ = RefreshCardsAsync(State.Match.BuildNumber);
    }

    void CheckLog(bool force = false)
    {
        try
        {
            var path = IsReplay ? _o.ReplayPath : LogLocator.FindPowerLog(Settings.Value.LogFolder);
            LogConfigStatus? cfg = null;
            try { cfg = new LogConfig().Check(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var next = new LogStatus(path, cfg, LogConfig.DefaultPath, Settings.Value.LogFolder);
            lock (_gate)
            {
                if (!force && next.PowerLogPath == _log.PowerLogPath && next.Override == _log.Override
                    && next.Config?.PowerEnabled == _log.Config?.PowerEnabled && next.Config?.FileExists == _log.Config?.FileExists) return;
                _log = next;
            }
            Recompute();
        }
        catch (Exception e) { AppLog.Error("Проверка лога", e); }
    }

    public void RecheckLog() => CheckLog(force: true);

    public void SetLogFolder(string? folder)
    {
        Settings.Value.LogFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Settings.Save();
        CheckLog(force: true);
    }

    /// <summary>Writes the [Power] section of log.config (a backup is made first). Only ever called from a button.</summary>
    public LogConfigResult EnableLogs()
    {
        var r = new LogConfig().EnablePower();
        AppLog.Info($"log.config: изменён={r.Changed}, копия={r.BackupPath ?? "-"}");
        CheckLog(force: true);
        return r;
    }

    /// <summary>Chip click. Returns false when the tribe is proven present and cannot be marked.</summary>
    public bool ToggleTribe(string tribe)
    {
        var changed = Marks.Toggle(tribe, State.Match.ConfirmedTribes);
        if (changed) Recompute();
        return changed;
    }

    void Recompute()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var m = _tracker.Snapshot;
            var cat = Cards.Current;
            if (_lastPhase is MatchPhase.Idle or MatchPhase.GameOver && m.Phase is not (MatchPhase.Idle or MatchPhase.GameOver)) Marks.Clear();
            _lastPhase = m.Phase;
            var marked = Marks.Current;
            var (hero, board) = _pipeline.Advise(m, cat, marked, _historyRev);
            var sim = _sim.Update(m, cat);
            var preview = _preview.Update(Core.Sim.CombatPreview.For(m), cat);
            var cards = new CardsStatus(true, _refreshing, cat.Version, Cards.Warning, m.BuildNumber);
            var prev = _state;
            var turn = m.Phase != MatchPhase.Recruit ? null
                : ReferenceEquals(prev.Match, m) && ReferenceEquals(prev.Board, board) && ReferenceEquals(prev.Catalog, cat) && prev.Turn is not null ? prev.Turn
                : Core.Advice.TurnAdvisor.Plan(m, cat, board);
            if (ReferenceEquals(prev.Match, m) && ReferenceEquals(prev.Hero, hero) && ReferenceEquals(prev.Board, board)
                && prev.Sim == sim && prev.Preview == preview && prev.FieldProblem == _fieldProblem && prev.AbsentTribes.SetEquals(marked) && prev.Cards == cards
                && prev.Log == _log && prev.UpdateReadyVersion == _updateReady && ReferenceEquals(prev.Catalog, cat) && prev.IsReplay == IsReplay)
                return;
            _state = new UiState(m, hero, board, sim, marked, cards, _log, cat, IsReplay, prev.Revision + 1) { UpdateReadyVersion = _updateReady, Preview = preview, PreviewCombat = _preview.Current, Turn = turn, FieldProblem = _fieldProblem };
            _publisher.Publish(_state);
        }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _cts.Cancel();
        Updater?.Dispose();
        _logWatch.Dispose();
        _replay?.Dispose();
        _tailer?.Dispose();
        _publisher.Dispose();
        _http.Dispose();
        AppLog.Info("Выход");
    }
}
