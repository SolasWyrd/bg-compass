using System.Text;
using BgCompass.Core.Logs;
using Xunit;

namespace BgCompass.Tests;

public class LogTailerTests
{
    private const string Create = "D 13:00:00.0000000 GameState.DebugPrintPower() - CREATE_GAME";
    private static string Line(int n) => $"D 13:00:{n:00}.0000000 GameState.DebugPrintPower() - BLOCK_END";

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public string? Located;
        public readonly List<LogSession> Sessions = [];
        public readonly List<(List<string> Lines, bool Replay)> Batches = [];
        public readonly LogTailer Tailer;

        public Rig() => Tailer = Attach();

        private LogTailer Attach()
        {
            var t = new LogTailer(() => Located);
            t.SessionStarted += Sessions.Add;
            t.LinesRead += (l, r) => Batches.Add(([.. l], r));
            return t;
        }

        public IEnumerable<string> AllLines => Batches.SelectMany(b => b.Lines);

        public string Make(string folder, string content)
        {
            var dir = Dir.Combine(folder);
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "Power.log");
            File.WriteAllText(p, content, new UTF8Encoding(false));
            return p;
        }

        public void Dispose() { Tailer.Dispose(); Dir.Dispose(); }
    }

    private static void Append(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Replays_only_from_the_last_game_and_derives_session_and_date_from_the_folder()
    {
        using var r = new Rig();
        r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", $"{Line(1)}\n{Create}\n{Line(2)}\n{Line(3)}\n");

        r.Tailer.PollOnce();

        var s = Assert.Single(r.Sessions);
        Assert.Equal("Hearthstone_2026_11_06_13_17_54", s.SessionId);
        Assert.Equal(new DateOnly(2026, 11, 6), s.BaseDate);
        var b = Assert.Single(r.Batches);
        Assert.True(b.Replay);
        Assert.Equal([Create, Line(2), Line(3)], b.Lines);
    }

    [Fact]
    public void Partial_line_is_held_back_until_its_newline_arrives()
    {
        using var r = new Rig();
        var p = r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", Create + "\n");
        r.Tailer.PollOnce();
        r.Batches.Clear();

        var half = Line(5);
        Append(p, half[..20]);
        r.Tailer.PollOnce();
        Assert.Empty(r.AllLines);

        Append(p, half[20..] + "\r\n" + Line(6)[..10]);
        r.Tailer.PollOnce();
        Assert.Equal([half], r.AllLines);                   // CRLF stripped, the next partial still waiting
        Assert.All(r.Batches, b => Assert.False(b.Replay));

        Append(p, Line(6)[10..] + "\n");
        r.Tailer.PollOnce();
        Assert.Equal([half, Line(6)], r.AllLines);
    }

    [Fact]
    public void Multibyte_text_split_across_polls_survives()
    {
        using var r = new Rig();
        var p = r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", Create + "\n");
        r.Tailer.PollOnce();
        r.Batches.Clear();
        var bytes = Encoding.UTF8.GetBytes("name=Ёж☃ end\n");
        using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(bytes, 0, 7);                          // cuts a two-byte character in half
        r.Tailer.PollOnce();
        using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(bytes, 7, bytes.Length - 7);
        r.Tailer.PollOnce();
        Assert.Equal(["name=Ёж☃ end"], r.AllLines);
    }

    [Fact]
    public void Truncation_restarts_the_session_with_a_new_id()
    {
        using var r = new Rig();
        var p = r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", $"{Create}\n{Line(1)}\n{Line(2)}\n{Line(3)}\n");
        r.Tailer.PollOnce();
        r.Batches.Clear();

        File.WriteAllText(p, $"{Create}\n{Line(9)}\n");     // game restarted the file: shorter than before
        r.Tailer.PollOnce();

        Assert.Equal(2, r.Sessions.Count);
        Assert.Equal("Hearthstone_2026_11_06_13_17_54", r.Sessions[0].SessionId);
        Assert.Equal("Hearthstone_2026_11_06_13_17_54+t1", r.Sessions[1].SessionId);
        Assert.Equal([Create, Line(9)], r.AllLines);
        Assert.True(r.Batches[0].Replay);
    }

    [Fact]
    public void A_newer_session_folder_takes_over_after_the_locator_is_asked_again()
    {
        using var r = new Rig();
        var oldLog = r.Located = r.Make("Hearthstone_2026_11_06_10_00_00", $"{Create}\n{Line(1)}\n");
        r.Tailer.PollOnce();
        r.Batches.Clear();

        var newLog = r.Make("Hearthstone_2026_11_06_14_00_00", $"{Create}\n{Line(7)}\n");
        r.Located = newLog;
        for (int i = 0; i < 10 && r.Sessions.Count < 2; i++) r.Tailer.PollOnce();   // the locator is re-run every few polls

        Assert.Equal("Hearthstone_2026_11_06_14_00_00", r.Sessions[^1].SessionId);
        Assert.Equal([Create, Line(7)], r.AllLines);
        Append(oldLog, Line(8) + "\n");                    // the old file is no longer followed
        r.Tailer.PollOnce();
        Assert.DoesNotContain(Line(8), r.AllLines);
    }

    [Fact]
    public void Waits_for_a_log_to_appear_and_survives_its_deletion()
    {
        using var r = new Rig();
        r.Tailer.PollOnce();                               // nothing located
        Assert.Empty(r.Sessions);

        var p = r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", Create + "\n");
        r.Tailer.PollOnce();
        Assert.Single(r.Sessions);

        File.Delete(p);
        r.Tailer.PollOnce();                               // no throw
        r.Tailer.PollOnce();
        Assert.Single(r.Sessions);
    }

    [Fact]
    public void Legacy_log_without_a_session_folder_gets_the_legacy_id()
    {
        using var r = new Rig();
        var p = Path.Combine(r.Dir.Path, "Power.log");
        File.WriteAllText(p, Create + "\n");
        r.Located = p;
        r.Tailer.PollOnce();
        Assert.Equal("legacy", Assert.Single(r.Sessions).SessionId);
    }

    [Fact]
    public void Timer_driven_start_delivers_lines()
    {
        using var r = new Rig();
        var done = new ManualResetEventSlim();
        var t = new LogTailer(() => r.Located, TimeSpan.FromMilliseconds(20));
        t.LinesRead += (l, _) => { if (l.Contains(Line(4))) done.Set(); };
        r.Located = r.Make("Hearthstone_2026_11_06_13_17_54", Create + "\n");
        t.Start();
        Append(r.Located, Line(4) + "\n");
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        t.Dispose();
    }
}
