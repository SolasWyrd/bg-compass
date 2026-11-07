using BgCompass.Core.Logs;
using Xunit;

namespace BgCompass.Tests;

public class LogLocatorTests
{
    [Fact]
    public void Newest_power_log_wins_across_session_folders_and_the_legacy_file()
    {
        using var d = new TempDir();
        var a = Make(d, "Hearthstone_2026_11_05_10_00_00", new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc));
        var b = Make(d, "Hearthstone_2026_11_06_10_00_00", new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(b, LogLocator.NewestPowerLog(d.Path));

        var legacy = d.Combine("Power.log");
        File.WriteAllText(legacy, "x");
        File.SetLastWriteTimeUtc(legacy, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(legacy, LogLocator.NewestPowerLog(d.Path));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Missing_directory_or_empty_directory_gives_null()
    {
        using var d = new TempDir();
        Assert.Null(LogLocator.NewestPowerLog(d.Combine("nope")));
        Assert.Null(LogLocator.NewestPowerLog(d.Path));
    }

    [Fact]
    public void User_override_pointing_at_a_file_or_a_logs_folder_is_honoured()
    {
        using var d = new TempDir();
        var log = Make(d, "Hearthstone_2026_11_06_10_00_00", DateTime.UtcNow);
        Assert.Equal(log, LogLocator.FindPowerLog(log));
        Assert.Equal(log, LogLocator.FindPowerLog(d.Path));
    }

    private static string Make(TempDir d, string folder, DateTime utc)
    {
        Directory.CreateDirectory(d.Combine(folder));
        var p = d.Combine(folder, "Power.log");
        File.WriteAllText(p, "x");
        File.SetLastWriteTimeUtc(p, utc);
        return p;
    }
}
