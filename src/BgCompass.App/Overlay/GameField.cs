namespace BgCompass.App.Overlay;

/// <summary>A rectangle in field units: the game window scaled so its height is 1080.</summary>
public readonly record struct FieldRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
    public FieldRect Shift(double dx, double dy) => new(X + dx, Y + dy, W, H);
    public FieldRect Inset(double dx, double dy) => new(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
}

/// <summary>
/// Where things are on the Battlegrounds table. The game scales its layout with the window height and centers it, so every
/// position is a constant in field units measured from the middle of the window. The window's size is the only input.
/// </summary>
public static class GameField
{
    public const double Height = 1080;

    const double CardW = 138, CardH = 190, ShopTop = 300, BoardTop = 490;
    const double UpgradeShift = 170, UpgradeBottom = 272, FreezeShift = 280, FreezeBottom = 245, ChoiceTop = 76;
    // On the blue cloth left of the hero: clear of the leaderboard tags, of the board and of the hero frame.
    const double StatsLeft = 436, StatsTop = 850, StatW = 80, StatH = 46, StatGap = 6;
    public const int StatColumns = 4;
    const double HeroW = 266, HeroH = 568, HeroPitch = 340, HeroTop = 284, HeroShift = 7;
    const double SeatTop = 162, SeatSize = 93.15, SeatsLeft = 720, NextShift = 33.1;
    const double OddsW = 380, OddsH = 64, OddsTop = 6;

    /// <summary>Width of the field for a game window, in field units.</summary>
    public static double Width(PxRect game) => game.H > 0 ? Height * game.W / game.H : 0;

    /// <summary>Physical screen point to field units.</summary>
    public static (double X, double Y) ToField(PxRect game, double px, double py)
    {
        var k = game.H > 0 ? Height / game.H : 1;
        return ((px - game.X) * k, (py - game.Y) * k);
    }

    /// <param name="index">0-based place in Bob's row.</param>
    /// <param name="count">Cards in the row, tavern spells included: the row is centered.</param>
    public static FieldRect ShopSlot(double width, int index, int count) =>
        new(width / 2 + (index - (count - 1) / 2.0) * CardW - CardW / 2, ShopTop, CardW, CardH);

    /// <param name="index">0-based place on the player's own board.</param>
    public static FieldRect BoardSlot(double width, int index, int count) =>
        ShopSlot(width, index, count) with { Y = BoardTop };

    /// <summary>The tag under the tavern upgrade button, left of Bob.</summary>
    public static FieldRect UpgradeTag(double width, double w, double h) => new(width / 2 - UpgradeShift - w / 2, UpgradeBottom, w, h);

    /// <summary>The tag under the refresh button, right of Bob.</summary>
    public static FieldRect RefreshTag(double width, double w, double h) => new(width / 2 + UpgradeShift - w / 2, UpgradeBottom, w, h);

    /// <summary>The tag under the freeze button.</summary>
    public static FieldRect FreezeTag(double width, double w, double h) => new(width / 2 + FreezeShift - w / 2, FreezeBottom, w, h);

    /// <summary>Which offered card to take: under the odds strip. The offer's own screen positions are not measured.</summary>
    public static FieldRect ChoiceTag(double width, double w, double h) => new((width - w) / 2, ChoiceTop, w, h);

    /// <summary>A tile of the turn's numbers, filling rows left to right.</summary>
    public static FieldRect StatCell(double width, int index) =>
        new(width / 2 - StatsLeft + index % StatColumns * (StatW + StatGap), StatsTop + index / StatColumns * (StatH + StatGap), StatW, StatH);

    /// <param name="index">0-based, left to right.</param>
    public static FieldRect HeroSlot(double width, int index, int count) =>
        new(width / 2 + HeroShift + (index - (count - 1) / 2.0) * HeroPitch - HeroW / 2, HeroTop, HeroW, HeroH);

    /// <param name="place">1-based leaderboard place.</param>
    /// <param name="isNext">The next opponent's tile sits further right.</param>
    public static FieldRect SeatSlot(double width, int place, bool isNext) =>
        new(width / 2 - SeatsLeft + (isNext ? NextShift : 0), SeatTop + (place - 1) * SeatSize, SeatSize, SeatSize);

    /// <summary>Where a seat's popup opens: right of the board preview the game draws next to the leaderboard.</summary>
    public static double SeatPopupX(double width) => width / 2 + 100;

    public static FieldRect OddsBar(double width) => new((width - OddsW) / 2, OddsTop, OddsW, OddsH);
}
