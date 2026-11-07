using BgCompass.Core;
using BgCompass.Core.Tracking;

namespace BgCompass.Tests;

internal static class TestData
{
    public static readonly DateOnly Day = new(2026, 11, 6);

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string[] Fixture(string name) => File.ReadAllLines(FixturePath(name));

    /// <summary>The 33 MB real log; tests that need it return early when BGC_FULL_LOG is not set.</summary>
    public static string? FullLog()
    {
        var p = Environment.GetEnvironmentVariable("BGC_FULL_LOG");
        return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
    }

    public static MatchTracker NewTracker(PowerStream stream, string session = "s") =>
        new(new MatchTrackerOptions(session, Day, stream));

    /// <summary>Feeds every line and collects the snapshot at each "#CHECK name" marker plus the finished games.</summary>
    public static (Dictionary<string, MatchSnapshot> Checks, List<MatchRecord> Records) RunChecks(
        IEnumerable<string> lines, PowerStream stream = PowerStream.PowerTaskList, string session = "s")
    {
        var t = NewTracker(stream, session);
        var records = new List<MatchRecord>();
        t.GameEnded += records.Add;
        var checks = new Dictionary<string, MatchSnapshot>();
        foreach (var l in lines)
        {
            t.Feed(l);
            if (l.StartsWith("#CHECK ", StringComparison.Ordinal)) checks[l[7..].Trim()] = t.Snapshot;
        }
        return (checks, records);
    }
}
