using System.Diagnostics;

namespace BgCompass.App.Runtime;

/// <summary>
/// Hands the newest value to <paramref name="deliver"/> (through <paramref name="post"/>, normally the UI thread)
/// at most once per <paramref name="minInterval"/>. Values published in between are dropped except the last one.
/// Nothing runs while nothing is published, so an idle game costs no CPU.
/// </summary>
public sealed class ThrottledPublisher<T> : IDisposable where T : class
{
    readonly Action<T> _deliver;
    readonly Action<Action> _post;
    readonly TimeSpan _minInterval;
    readonly Timer _timer;
    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    T? _latest;
    bool _scheduled, _disposed;
    TimeSpan _lastDelivery = TimeSpan.FromDays(-1);

    public ThrottledPublisher(Action<T> deliver, Action<Action> post, TimeSpan? minInterval = null)
    {
        _deliver = deliver;
        _post = post;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(200);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Delivered { get; private set; }

    public void Publish(T value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = value;
            if (_scheduled) return;
            _scheduled = true;
            var wait = _lastDelivery + _minInterval - _clock.Elapsed;
            _timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        T? value;
        lock (_gate)
        {
            if (_disposed) return;
            value = _latest;
            _latest = null;
            _scheduled = false;
            _lastDelivery = _clock.Elapsed;
        }
        if (value is null) return;
        Delivered++;
        _post(() =>
        {
            try { _deliver(value); }
            catch (Exception e) { AppLog.Error("Ошибка при обновлении интерфейса", e); }
        });
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}
