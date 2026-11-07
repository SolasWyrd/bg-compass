using BgCompass.App.Runtime;

namespace BgCompass.App.Overlay;

public readonly record struct PxRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Intersects(PxRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>
/// Pure geometry for the overlay, all in physical pixels (what Win32 reports) so it can be tested without a window.
/// A panel's saved position is the top-left corner as a fraction of the game rect, which survives a change of resolution.
/// </summary>
public static class PanelLayout
{
    public const double DesignHeight = 1080;

    /// <summary>Panel width at 1080p and scale 1, and the tallest it may grow before it scrolls.</summary>
    public const double DesignWidth = 300, DesignMaxHeight = 640;

    /// <summary>1.0 at a 1080p-tall game. Narrow (4:3) screens shrink the panels a little so they stay out of the board.</summary>
    public static double AutoScale(PxRect game)
    {
        if (game.H <= 0 || game.W <= 0) return 1;
        var narrow = Math.Clamp(game.W / game.H / (16.0 / 9), 0.85, 1.0);
        return game.H / DesignHeight * narrow;
    }

    /// <summary>The 16:9 area the game draws its interface in; wider screens only add margins left and right of it.</summary>
    public static PxRect Playfield(PxRect game)
    {
        var w = Math.Min(game.W, game.H * 16.0 / 9);
        return new PxRect(game.X + (game.W - w) / 2, game.Y, w, game.H);
    }

    /// <summary>
    /// Approximate areas of the Battlegrounds screen that must stay uncovered by default: the tavern row, the board and
    /// the hero with the hand (in fractions of <see cref="Playfield"/>). It is an estimate, so every panel can be dragged.
    /// </summary>
    public static IReadOnlyList<PxRect> ProtectedZones(PxRect game)
    {
        var p = Playfield(game);
        return
        [
            new PxRect(p.X + 0.21 * p.W, p.Y + 0.10 * p.H, 0.58 * p.W, 0.90 * p.H),
        ];
    }

    /// <summary>Where the panel goes when the player has not moved it.</summary>
    public static (double X, double Y) Default(PxRect game) => (game.X + 8 * AutoScale(game), game.Y + 0.10 * game.H);

    public static (double X, double Y) Resolve(PanelState saved, PxRect game, double w, double h)
    {
        var (x, y) = saved.X is double fx && saved.Y is double fy && double.IsFinite(fx) && double.IsFinite(fy)
            ? (game.X + fx * game.W, game.Y + fy * game.H)
            : Default(game);
        return Clamp(x, y, game, w, h);
    }

    /// <summary>Keeps the whole panel inside the game rect (a panel larger than the rect sticks to the top-left).</summary>
    public static (double X, double Y) Clamp(double x, double y, PxRect game, double w, double h) =>
        (Math.Max(game.X, Math.Min(x, game.Right - w)), Math.Max(game.Y, Math.Min(y, game.Bottom - h)));

    public static (double X, double Y) ToFraction(double x, double y, PxRect game) =>
        game.W <= 0 || game.H <= 0 ? (0, 0) : ((x - game.X) / game.W, (y - game.Y) / game.H);
}
