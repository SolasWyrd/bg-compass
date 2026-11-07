using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using BgCompass.App.Dev;

namespace BgCompass.App;

/// <summary>
/// Command line (all optional; the normal app takes none):
/// <code>
///   --replay &lt;Power.log&gt; [--speed N] [--gamestate]   feed a recorded log instead of following the live one,
///                                                   N times faster than real time; badge "ПОВТОР ЛОГА" is shown.
///                                                   --gamestate: the log is a GameState stream, not PowerTaskList.
///   --render-ui &lt;outDir&gt;                            draw every panel and main-window tab to PNG files
///                                                   (headless, 1080p and 1440p scale, realistic scenario), then exit.
///   --smoke-exit &lt;seconds&gt;                          exit with code 0 after N seconds (automated runs).
///   --wait-pid &lt;pid&gt;                                 wait for that process to exit first (restart after an update:
///                                                   the old instance must release its global hotkeys).
/// </code>
/// Environment: BGC_DATA_DIR overrides the data folder (default %LOCALAPPDATA%\BgCompass).
/// </summary>
public sealed record CliOptions(string? Replay, double Speed, bool GameState, string? RenderUi, double? SmokeExit)
{
    public static readonly CliOptions Default = new(null, 1, false, null, null);

    public static CliOptions Parse(string[] args)
    {
        string? replay = null, render = null;
        double speed = 1;
        double? smoke = null;
        var gameState = false;
        static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--replay": replay = Next(); break;
                case "--speed": if (Next() is { } sp) speed = Math.Max(0.1, Num(sp)); break;
                case "--gamestate": gameState = true; break;
                case "--render-ui": render = Next(); break;
                case "--smoke-exit": if (Next() is { } se) smoke = Num(se); break;
            }
        }
        return new CliOptions(replay, speed, gameState, render, smoke);
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WaitForPreviousInstance(args);
        var cli = CliOptions.Parse(args);
        App.Cli = cli;
        if (cli.RenderUi is { } outDir)
            return RenderUi.Run(outDir, BuildApp().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }));
        return BuildApp().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(pid);
            old.WaitForExit(5000);
        }
        catch (ArgumentException) { /* already gone */ }
    }

    static AppBuilder BuildApp() => AppBuilder.Configure<App>();
}
