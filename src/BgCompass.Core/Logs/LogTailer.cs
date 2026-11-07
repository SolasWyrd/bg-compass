using System.Globalization;
using System.Text;

namespace BgCompass.Core.Logs;

/// <summary>A Power.log file being followed. <see cref="SessionId"/> plus a game's start time identify that game across restarts.</summary>
public sealed record LogSession(string SessionId, string Path, DateOnly BaseDate);

/// <summary>
/// Follows Power.log by polling its length (no FileSystemWatcher). On attach it replays only from the last CREATE_GAME;
/// afterwards it delivers newly appended complete lines. Raises <see cref="SessionStarted"/> whenever the stream restarts
/// (new file, newer session folder, truncation): the consumer should then start a fresh MatchTracker.
/// Events come from a timer thread.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private static readonly byte[] GameStateCreate = "GameState.DebugPrintPower() - CREATE_GAME"u8.ToArray();
    private static readonly byte[] PtlCreate = "PowerTaskList.DebugPrintPower() - CREATE_GAME"u8.ToArray();
    private const int LocateEvery = 8;

    private readonly Func<string?> _locate;
    private readonly TimeSpan _poll;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    private string? _path;
    private string _baseSessionId = "";
    private long _offset;
    private byte[] _partial = [];
    private int _truncations;
    private int _untilLocate;

    public LogTailer(Func<string?> locate, TimeSpan? pollInterval = null)
    {
        _locate = locate;
        _poll = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public event Action<LogSession>? SessionStarted;

    /// <summary>Lines read; the flag is true for the initial replay (do not announce game ends from it).</summary>
    public event Action<IReadOnlyList<string>, bool>? LinesRead;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        try { PollOnce(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* file busy or gone: retry next poll */ }
        finally
        {
            lock (_gate)
                if (!_disposed) _timer?.Change(_poll, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>One poll step. Public so tests (and callers that own the loop) can drive it synchronously.</summary>
    public void PollOnce()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_path is null || --_untilLocate <= 0)
            {
                _untilLocate = LocateEvery;
                var found = _locate();
                if (found is not null && File.Exists(found) && !string.Equals(found, _path, StringComparison.OrdinalIgnoreCase))
                {
                    Attach(found);
                    return;
                }
            }
            if (_path is null) return;

            var info = new FileInfo(_path);
            if (!info.Exists) { _path = null; return; }
            var length = info.Length;
            if (length == _offset) return;
            if (length < _offset)
            {
                _truncations++;
                Attach(_path);
                return;
            }
            ReadNew(length);
        }
    }

    private void Attach(string path)
    {
        _path = path;
        var (id, date) = SessionOf(path);
        if (_truncations > 0 && string.Equals(_baseSessionId, id, StringComparison.Ordinal) is false) _truncations = 0;
        _baseSessionId = id;
        var sessionId = _truncations > 0 ? $"{id}+t{_truncations}" : id;
        _offset = 0;
        _partial = [];
        SessionStarted?.Invoke(new LogSession(sessionId, path, date));

        var bytes = ReadAll(path);
        if (bytes is null) return;
        var from = LastCreateGame(bytes);
        _offset = bytes.Length;
        var lines = SplitLines(bytes.AsSpan(from), out _partial);
        if (lines.Count > 0) LinesRead?.Invoke(lines, true);
    }

    private void ReadNew(long length)
    {
        byte[] buf;
        try
        {
            using var fs = Open(_path!);
            fs.Seek(_offset, SeekOrigin.Begin);
            var want = (int)Math.Min(length - _offset, int.MaxValue / 2);
            buf = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(buf, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < want) Array.Resize(ref buf, got);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        _offset += buf.Length;
        var all = _partial.Length == 0 ? buf : [.. _partial, .. buf];
        var lines = SplitLines(all, out _partial);
        if (lines.Count > 0) LinesRead?.Invoke(lines, false);
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    private static byte[]? ReadAll(string path)
    {
        try
        {
            using var fs = Open(path);
            var buf = new byte[fs.Length];
            int got = 0;
            while (got < buf.Length)
            {
                int n = fs.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < buf.Length) Array.Resize(ref buf, got);
            return buf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Start of the line holding the last CREATE_GAME (GameState writer first; it logs ahead of the PowerTaskList one).</summary>
    private static int LastCreateGame(ReadOnlySpan<byte> bytes)
    {
        int at = bytes.LastIndexOf(GameStateCreate);
        if (at < 0) at = bytes.LastIndexOf(PtlCreate);
        if (at < 0) return 0;
        int nl = bytes[..at].LastIndexOf((byte)'\n');
        return nl + 1;
    }

    private static List<string> SplitLines(ReadOnlySpan<byte> bytes, out byte[] partial)
    {
        var lines = new List<string>();
        int start = 0;
        while (true)
        {
            int rel = bytes[start..].IndexOf((byte)'\n');
            if (rel < 0) break;
            var line = bytes.Slice(start, rel);
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            lines.Add(Encoding.UTF8.GetString(line));
            start += rel + 1;
        }
        partial = bytes[start..].ToArray();
        return lines;
    }

    private static (string Id, DateOnly Date) SessionOf(string path)
    {
        var dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        if (dir.StartsWith("Hearthstone_", StringComparison.Ordinal))
        {
            // Hearthstone_2026_11_06_13_17_54
            var parts = dir.Split('_');
            if (parts.Length >= 4
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
                && y is >= 2000 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
                return (dir, new DateOnly(y, m, d));
        }
        // Legacy Logs/Power.log: the file's creation date is the best guess for the date of its first line.
        return ("legacy", DateOnly.FromDateTime(File.GetCreationTime(path)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
