using System.Globalization;

namespace BgCompass.App.Runtime;

/// <summary>
/// Dev mode: feeds a saved Power.log into the tracker as if it were being written live, keeping the pauses between
/// the log's own timestamps divided by <c>speed</c> (a pause is capped at two seconds of game time so a long
/// idle stretch does not stall the replay).
/// </summary>
public sealed class ReplayFeeder : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;

    public ReplayFeeder(string path, double speed, Action<string> feed, Action done)
    {
        var s = Math.Max(0.01, speed);
        _thread = new Thread(() => Run(path, s, feed, done)) { IsBackground = true, Name = "BgCompass replay" };
    }

    public void Start() => _thread.Start();

    void Run(string path, double speed, Action<string> feed, Action done)
    {
        try
        {
            TimeSpan? prev = null;
            var owed = 0.0;
            foreach (var line in File.ReadLines(path))
            {
                if (_cts.IsCancellationRequested) return;
                if (TryTime(line, out var t))
                {
                    if (prev is { } p && t > p)
                    {
                        owed += Math.Min((t - p).TotalMilliseconds, 2000) / speed;
                        if (owed >= 1)
                        {
                            if (_cts.Token.WaitHandle.WaitOne((int)owed)) return;
                            owed -= Math.Floor(owed);
                        }
                    }
                    prev = t;
                }
                feed(line);
            }
            done();
            AppLog.Info("Повтор лога завершён");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Повтор лога не удался", e);
        }
    }

    // "D 13:00:00.0000000 ..." -> 13:00:00.0000000
    static bool TryTime(string line, out TimeSpan t)
    {
        t = default;
        return line.Length > 18 && line[1] == ' ' && line[10] == '.'
            && TimeSpan.TryParseExact(line.AsSpan(2, 16), @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture, out t);
    }

    public void Dispose() => _cts.Cancel();
}
