using BgCompass.Core.Logs;
using Xunit;

namespace BgCompass.Tests;

public class LogConfigTests
{
    private const string Other = "[Zone]\r\nLogLevel=1\r\nFilePrinting=False\r\n\r\n[Net]\r\nLogLevel=3\r\n";

    [Fact]
    public void Missing_file_is_reported_and_created_without_backup()
    {
        using var d = new TempDir();
        var cfg = new LogConfig(d.Combine("sub", "log.config"));
        Assert.False(cfg.Check().FileExists);

        var r = cfg.EnablePower();
        Assert.True(r.Changed);
        Assert.Null(r.BackupPath);
        Assert.True(r.RestartRequired);
        Assert.True(cfg.Check().PowerEnabled);
    }

    [Fact]
    public void Enabling_backs_up_and_keeps_other_sections_untouched()
    {
        using var d = new TempDir();
        var path = d.Combine("log.config");
        File.WriteAllText(path, Other);
        var cfg = new LogConfig(path);
        Assert.False(cfg.Check().PowerEnabled);

        var r = cfg.EnablePower();

        Assert.True(r.Changed);
        Assert.Equal(path + ".bak", r.BackupPath);
        Assert.Equal(Other, File.ReadAllText(r.BackupPath!));
        var text = File.ReadAllText(path);
        Assert.StartsWith(Other, text);                 // existing sections byte for byte, CRLF kept
        Assert.Contains("[Power]", text);
        Assert.Contains("FilePrinting=True", text);
        Assert.Contains("[Zone]\r\nLogLevel=1\r\nFilePrinting=False", text);
        Assert.True(cfg.Check().PowerEnabled);
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);       // no BOM
    }

    [Fact]
    public void Existing_power_section_is_corrected_in_place()
    {
        using var d = new TempDir();
        var path = d.Combine("log.config");
        File.WriteAllText(path, "[Power]\nLogLevel=0\nFilePrinting=False\nMyOwn=1\n[Zone]\nLogLevel=1\n");
        var cfg = new LogConfig(path);
        Assert.Contains("FilePrinting", cfg.Check().Problems);

        cfg.EnablePower();

        var lines = File.ReadAllText(path).Split('\n');
        Assert.DoesNotContain("\r", File.ReadAllText(path));       // LF file stays LF
        Assert.Contains("MyOwn=1", lines);                          // a foreign key in [Power] survives
        Assert.Equal("[Zone]", lines[Array.FindIndex(lines, l => l == "[Zone]")]);
        Assert.Equal(1, lines.Count(l => l == "[Power]"));
        Assert.Contains("LogLevel=1", lines);
        Assert.True(cfg.Check().PowerEnabled);
    }

    [Fact]
    public void Already_enabled_is_a_no_op()
    {
        using var d = new TempDir();
        var path = d.Combine("log.config");
        var cfg = new LogConfig(path);
        cfg.EnablePower();
        var before = File.ReadAllText(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var r = cfg.EnablePower();

        Assert.False(r.Changed);
        Assert.Null(r.BackupPath);
        Assert.False(r.RestartRequired);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void A_second_change_does_not_overwrite_the_first_backup()
    {
        using var d = new TempDir();
        var path = d.Combine("log.config");
        File.WriteAllText(path, Other);
        var cfg = new LogConfig(path);
        cfg.EnablePower();
        File.WriteAllText(path, Other + "[Extra]\r\nX=1\r\n");   // user breaks it again

        var r = cfg.EnablePower();

        Assert.True(r.Changed);
        Assert.NotEqual(path + ".bak", r.BackupPath);
        Assert.Equal(Other, File.ReadAllText(path + ".bak"));
        Assert.Contains("[Extra]", File.ReadAllText(r.BackupPath!));
    }
}
