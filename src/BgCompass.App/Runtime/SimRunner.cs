using BgCompass.Core;
using BgCompass.Core.Cards;
using BgCompass.Core.Sim;

namespace BgCompass.App.Runtime;

/// <summary>Runs the combat simulator once per combat on a pool thread; a new combat or leaving combat cancels the old run.</summary>
public sealed class SimRunner(Action changed)
{
    const int Iterations = 3000;
    readonly object _gate = new();
    string? _key;
    CancellationTokenSource? _cts;
    SimView _view = SimView.None;

    public int Runs { get; private set; }

    /// <summary>The board pair the current view belongs to.</summary>
    public CombatSnapshot? Current { get; private set; }

    public SimView Update(MatchSnapshot m, CardCatalog? cards) =>
        Update(m.Phase == MatchPhase.Combat ? m.Combat : null, cards);

    /// <param name="c">What to simulate; null cancels and clears.</param>
    public SimView Update(CombatSnapshot? c, CardCatalog? cards)
    {
        lock (_gate)
        {
            if (c is null || cards is null)
            {
                Cancel();
                Current = null;
                return _view = SimView.None;
            }
            var key = Key(c);
            if (key == _key) return _view;

            Cancel();
            _key = key;
            Current = c;
            _view = new SimView(SimStatus.Running, null);
            var cts = _cts = new CancellationTokenSource();
            Runs++;
            _ = Task.Run(() => Work(c, cards, key, cts.Token));
            return _view;
        }
    }

    static string Key(CombatSnapshot c)
    {
        static string Side(IReadOnlyList<MinionView> b) => string.Join(',', b.Select(x => $"{x.EntityId}:{x.Attack}:{x.Health}:{x.Position}"));
        return $"{c.Round}|{c.OpponentPlayerId}|{c.Complete}|{Side(c.Friendly)}|{Side(c.Enemy)}";
    }

    void Work(CombatSnapshot c, CardCatalog cards, string key, CancellationToken ct)
    {
        SimResult result;
        try { result = CombatSimulator.Run(c, cards, Iterations, null, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            AppLog.Error("Симулятор боя упал", e);
            result = SimResult.Unavailable("Расчёт боя не удался из-за внутренней ошибки (см. журнал app.log).");
        }
        lock (_gate)
        {
            if (key != _key || ct.IsCancellationRequested) return;
            _view = new SimView(SimStatus.Done, result);
        }
        changed();
    }

    void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _key = null;
    }
}
