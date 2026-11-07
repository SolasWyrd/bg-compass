using Avalonia;
using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.Core;
using BgCompass.Core.Advice;

namespace BgCompass.App.ViewModels;

public enum FieldMarkKind { Buy, Triple, Hero, Seat, Odds, Guide }

/// <param name="Box">What is drawn.</param>
/// <param name="Hit">Where the cursor opens the popup.</param>
/// <param name="Strong">Recommended hero or next opponent.</param>
/// <param name="Muted">The data is older than this round.</param>
/// <param name="Guess">Comes from a rule rather than the player's own numbers.</param>
/// <param name="Measured">The tile holds a real number.</param>
public sealed record FieldMark(
    FieldMarkKind Kind, FieldRect Box, FieldRect Hit, string Label, string Sub,
    bool Strong, bool Muted, bool Guess, bool Measured, string Title, string Body)
{
    public Thickness Margin => new(Box.X, Box.Y, 0, 0);
    public double Width => Box.W;
    public double Height => Box.H;
    public bool IsBuy => Kind == FieldMarkKind.Buy;
    public bool IsTriple => Kind == FieldMarkKind.Triple;
    public bool IsCard => Kind is FieldMarkKind.Buy or FieldMarkKind.Triple;
    public bool IsHero => Kind == FieldMarkKind.Hero;
    public bool IsSeat => Kind == FieldMarkKind.Seat && Label.Length > 0;
    public bool IsGuide => Kind == FieldMarkKind.Guide;
    public bool HeroLime => Strong && !Guess;
    public bool NotMeasured => !Measured;
}

public sealed record FieldPopup(double X, double Y, string Title, string Body)
{
    public const double Width = 264;
    public Thickness Margin => new(X, Y, 0, 0);
    public bool HasBody => Body.Length > 0;

    /// <summary>Beside what the mark points at, inside the field, away from the game's own hover cards.</summary>
    public static FieldPopup For(FieldMark m, double fieldWidth)
    {
        var lines = m.Body.Length == 0 ? 0 : m.Body.Split('\n').Sum(l => 1 + l.Length / 38);
        var height = 36 + lines * 18;
        double x, y;
        if (m.Kind == FieldMarkKind.Seat)
        {
            // The game shows its own board preview to the right of the leaderboard.
            x = m.Hit.X - Width - 8;
            y = m.Hit.Y;
            if (x < 4) x = m.Hit.Right + 8;
        }
        else
        {
            x = m.Box.CenterX - Width / 2;
            y = m.Box.Bottom + 6;
        }
        return new(Math.Clamp(x, 4, Math.Max(4, fieldWidth - Width - 4)), Math.Clamp(y, 4, Math.Max(4, GameField.Height - height - 4)), m.Title, m.Body);
    }
}

/// <summary>When the in-game layer is on screen. Pure so it can be tested without any window.</summary>
public static class FieldRules
{
    public static bool Visible(UiState s, AppSettings settings) =>
        settings.PanelsVisible && (settings.FieldGuides || (settings.FieldMarks && s.Match.Phase is MatchPhase.HeroPick or MatchPhase.Recruit or MatchPhase.Combat));
}

/// <summary>
/// What is drawn on the game table: marks over the shop, numbers over the offered heroes, tags beside the leaderboard, odds
/// on top. Everything is computed from the log's state and the window size; the cursor only opens popups.
/// </summary>
public sealed class FieldVm(SettingsStore settings) : ViewModelBase
{
    const int MaxPopupMinions = 7, MaxPopupEncounters = 3, GuideSeats = 8, GuideShop = 7;
    const double SeatTagW = 150, SeatTagH = 28, SeatTagGap = 6;

    UiState _state = UiState.Initial;
    double _width;
    IReadOnlyList<FieldMark> _marks = [];
    FieldPopup? _popup;
    FieldMark? _hovered;
    bool _showOdds;
    Thickness _oddsMargin;

    public IReadOnlyList<FieldMark> Marks { get => _marks; private set => Set(ref _marks, value); }
    public FieldPopup? Popup { get => _popup; private set { if (Set(ref _popup, value)) Raise(nameof(HasPopup)); } }
    public bool HasPopup => _popup is not null;
    public SimVm Odds { get; } = new();
    public bool ShowOdds { get => _showOdds; private set => Set(ref _showOdds, value); }
    public Thickness OddsMargin { get => _oddsMargin; private set => Set(ref _oddsMargin, value); }
    public double OddsWidth => GameField.OddsBar(0).W;

    public void Apply(UiState s)
    {
        _state = s;
        Rebuild();
    }

    /// <summary>Width of the field in field units: follows the game window's shape. Also picks up changed settings.</summary>
    public void SetWidth(double width)
    {
        _width = width;
        Rebuild();
    }

    /// <summary>Cursor position in field units; null when it is not over the field.</summary>
    public void Hover(double? x, double? y)
    {
        FieldMark? hit = null;
        if (x is double px && y is double py)
            for (var i = _marks.Count - 1; i >= 0 && hit is null; i--)
                if (_marks[i].Kind != FieldMarkKind.Guide && _marks[i].Hit.Contains(px, py)) hit = _marks[i];
        if (hit == _hovered) return;
        _hovered = hit;
        Popup = hit is null ? null : FieldPopup.For(hit, _width);
    }

    void Rebuild()
    {
        var cfg = settings.Value;
        var m = _state.Match;
        Odds.Apply(_state);
        var marks = new List<FieldMark>();
        double dx = cfg.FieldOffsetX, dy = cfg.FieldOffsetY;
        if (_width > 0)
        {
            if (cfg.FieldMarks)
            {
                if (m.Phase == MatchPhase.Recruit) AddShop(marks, m, _state.Board);
                if (m.Phase == MatchPhase.HeroPick) AddHeroes(marks, m, _state.Hero);
                if (m.Phase is MatchPhase.Recruit or MatchPhase.Combat && !m.IsDuos) AddSeats(marks, m);
                if (m.Phase == MatchPhase.Combat && (Odds.HasResult || _state.Sim.Status == SimStatus.Running)) AddOdds(marks);
            }
            if (cfg.FieldGuides) AddGuides(marks, m);
        }
        var shifted = marks.ConvertAll(k => k.Kind == FieldMarkKind.Odds ? k : k with { Box = k.Box.Shift(dx, dy), Hit = k.Hit.Shift(dx, dy) });
        ShowOdds = shifted.Any(k => k.Kind == FieldMarkKind.Odds);
        var odds = GameField.OddsBar(_width);
        OddsMargin = new Thickness(odds.X, odds.Y, 0, 0);
        if (!shifted.SequenceEqual(_marks)) Marks = shifted;
        if (_hovered is not null && !shifted.Contains(_hovered)) { _hovered = null; Popup = null; }
    }

    void AddShop(List<FieldMark> marks, MatchSnapshot m, BoardAdvice? advice)
    {
        var count = Math.Max(m.ShopSize, m.Shop.Count == 0 ? 0 : m.Shop.Max(c => c.Position));
        var picks = advice is null ? [] : BuildPicks.Now(advice).ToDictionary(p => p.Card.CardId);
        foreach (var card in m.Shop)
        {
            if (card.Position < 1) continue;
            var slot = GameField.ShopSlot(_width, card.Position - 1, count);
            var copies = m.Board.Concat(m.Hand ?? []).Count(c => c.CardId == card.CardId && !c.Golden);
            // Fits the minion's oval. The tag hangs below it: the tier ribbon on top and the stats stay readable.
            var box = new FieldRect(slot.X + 6, slot.Y + 16, slot.W - 12, slot.H + 6);
            if (!card.Golden && copies >= 2)
                marks.Add(new(FieldMarkKind.Triple, box, slot, "Тройка", "", false, false, false, true, card.Name,
                    $"Уже есть копий: {copies}. Третья даст золотую."));
            else if (picks.TryGetValue(card.CardId, out var pick))
                marks.Add(new(FieldMarkKind.Buy, box, slot, "Брать", "", false, false, true, false, card.Name,
                    $"Билд «{pick.Direction.Name}», собран на {Ru.Pct(pick.Direction.Fit)}.\nПравило, а не расчёт лучшего хода."));
        }
    }

    void AddHeroes(List<FieldMark> marks, MatchSnapshot m, HeroPickAdvice? advice)
    {
        if (advice is null) return;
        foreach (var h in advice.Heroes)
        {
            var offer = new HeroOfferVm(h, h.Option.CardId == advice.RecommendedCardId, advice.RecommendationReason);
            var cell = GameField.HeroSlot(_width, h.Option.ScreenSlot, advice.Heroes.Count);
            var box = new FieldRect(cell.CenterX - 92, cell.Y, 184, 60);
            // The game shows the hero power and armor itself; the popup only adds what it does not know.
            var body = (offer.IsRecommended && advice.RecommendationReason is { } why ? [why.Text] : Array.Empty<string>())
                .Concat(h.Lines.Select(l => l.Text).Where(t => !t.StartsWith("Броня:", StringComparison.Ordinal)
                    && !(h.HeroPowerText.Length > 0 && t.Contains(h.HeroPowerText, StringComparison.Ordinal))));
            marks.Add(new(FieldMarkKind.Hero, box, cell, offer.Tile, offer.Sub, offer.IsRecommended, false, offer.IsGuess, offer.HasStats, offer.Name, string.Join("\n", body)));
        }
    }

    void AddSeats(List<FieldMark> marks, MatchSnapshot m)
    {
        foreach (var p in m.Players)
        {
            if (p.IsLocal || p.Place is not int place || place is < 1 or > GuideSeats) continue;
            var tile = GameField.SeatSlot(_width, place, p.IsNextOpponent);
            var tag = new FieldRect(tile.Right + SeatTagGap, tile.Y + (tile.H - SeatTagH) / 2, SeatTagW, SeatTagH);
            var board = p.IsDead ? null : p.LastBoard;
            var age = board is null ? 0 : Math.Max(0, m.Round - board.Round);
            var label = board is null || board.Minions.Count == 0 ? "" : TribeTag(board.Minions) + (age > 0 ? $" · {age}р" : "");
            var hit = label.Length > 0 ? new FieldRect(tile.X, tile.Y, tag.Right - tile.X, tile.H) : tile;
            marks.Add(new(FieldMarkKind.Seat, tag, hit, label, "", p.IsNextOpponent, age > 0, false, true,
                p.HeroName.Length > 0 ? p.HeroName : p.HeroCardId, SeatBody(m, p, age)));
        }
    }

    /// <summary>The tribe most of the board belongs to, with its count.</summary>
    public static string TribeTag(IReadOnlyList<MinionView> minions)
    {
        var top = minions.SelectMany(c => c.Tribes).Where(TribeNames.Real.Contains).GroupBy(t => t)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        return top is null || top.Count() < 2 ? $"Смесь {minions.Count}" : $"{Ru.Tribe(top.Key)} {top.Count()}";
    }

    static string SeatBody(MatchSnapshot m, PlayerView p, int age)
    {
        var lines = new List<string>
        {
            string.Join(" · ", new[]
            {
                p.TavernTier is int t ? $"Таверна {t}" : null,
                p.IsDead ? "выбыл" : p.Health is int h ? $"{h} HP" : null,
                p.IsNextOpponent ? "следующий" : null,
            }.Where(x => x is not null)),
        };
        if (p.LastBoard is { Minions.Count: > 0 } board)
        {
            lines.Add(age > 0 ? $"Доска {Ru.RoundsAgo(age)}:" : "Доска этого раунда:");
            lines.AddRange(board.Minions.OrderBy(c => c.Position).Take(MaxPopupMinions).Select(c => $"{c.Attack}/{c.Health} {c.Name}{(c.Golden ? " ★" : "")}"));
        }
        else lines.Add("Доску ещё не видели");
        lines.AddRange(m.Encounters.Where(e => e.OpponentPlayerId == p.PlayerId).OrderByDescending(e => e.Round).Take(MaxPopupEncounters).Select(e => new EncounterVm(e).Text));
        return string.Join("\n", lines);
    }

    void AddOdds(List<FieldMark> marks)
    {
        var bar = GameField.OddsBar(_width);
        var body = Odds.Facts.Select(f => $"{f.Label}: {f.Value}").Append(Odds.Note).Where(l => l.Length > 0);
        marks.Add(new(FieldMarkKind.Odds, bar, bar, "", "", false, false, true, false, Odds.Headline, string.Join("\n", body)));
    }

    void AddGuides(List<FieldMark> marks, MatchSnapshot m)
    {
        void Guide(FieldRect r, string label) => marks.Add(new(FieldMarkKind.Guide, r, r, label, "", false, false, false, false, "", ""));
        if (m.Phase == MatchPhase.HeroPick)
        {
            var n = Math.Max(m.HeroOffers.Count, 1);
            for (var i = 0; i < n; i++) Guide(GameField.HeroSlot(_width, i, n), (i + 1).ToString());
            return;
        }
        for (var i = 0; i < GuideShop; i++) Guide(GameField.ShopSlot(_width, i, GuideShop), (i + 1).ToString());
        for (var p = 1; p <= GuideSeats; p++) Guide(GameField.SeatSlot(_width, p, false), p.ToString());
    }
}
