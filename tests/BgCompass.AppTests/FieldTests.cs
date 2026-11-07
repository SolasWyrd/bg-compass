using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.Core;
using BgCompass.Core.Advice;
using Xunit;

namespace BgCompass.AppTests;

public class FieldTests
{
    const double W = 1920;

    static MinionView Card(string id, int pos, bool golden = false) =>
        new(pos, id, id + " name", 2, 3, pos, golden, MinionKeywords.None, ["MURLOC"], 1);

    static UiState At(TempHost th, MatchSnapshot m) => th.Host.State with { Match = m };

    static MatchSnapshot Recruit() => MatchSnapshot.Empty with { Phase = MatchPhase.Recruit, Round = 5 };

    [Fact]
    public void Shop_row_centers_on_every_card_including_spells()
    {
        // One card: dead center. Three cards plus a spell: the row is four wide.
        Assert.Equal(960, GameField.ShopSlot(W, 0, 1).CenterX);
        Assert.Equal(960 - 138 * 1.5, GameField.ShopSlot(W, 0, 4).CenterX);
        Assert.Equal(960 + 138 * 0.5, GameField.ShopSlot(W, 2, 4).CenterX);
    }

    [Fact]
    public void Layout_scales_with_height_and_keeps_to_the_middle_of_a_wide_window()
    {
        var wide = new PxRect(100, 50, 3440, 1440);
        var width = GameField.Width(wide);
        Assert.Equal(2580, width, 3);
        Assert.Equal(width / 2 - 720, GameField.SeatSlot(width, 1, false).X, 3);
        var (x, y) = GameField.ToField(wide, 100 + 1720, 50 + 720);
        Assert.Equal(width / 2, x, 3);
        Assert.Equal(540, y, 3);
    }

    [Fact]
    public void Heroes_are_placed_by_slot_and_next_seat_is_shifted()
    {
        Assert.Equal(960 + 7 - 170, GameField.HeroSlot(W, 0, 2).CenterX);
        Assert.Equal(960 + 7 + 170, GameField.HeroSlot(W, 1, 2).CenterX);
        Assert.Equal(162 + 2 * 93.15, GameField.SeatSlot(W, 3, false).Y, 3);
        Assert.True(GameField.SeatSlot(W, 3, true).X > GameField.SeatSlot(W, 3, false).X);
    }

    static TurnPlan Plan(IReadOnlyList<TurnPick>? picks = null, MinionView? sell = null, bool upgrade = false, bool refresh = false, bool freeze = false, ChoicePick? choice = null, int? gold = 7, int? cost = 6, int? taken = 11, int? health = 30, int? age = 0, int tier = 4, int? cap = 15) =>
        new(tier, 6, gold, cost, health, new DamageReach(14, 18, taken, taken, age, cap), "UNDEAD", 5, picks ?? [], sell, "Стол полон", upgrade, "Цена 6", refresh, freeze, choice);

    static TurnPick Pick(string id, int pos, PickKind kind = PickKind.Buy) => new(pos, id, id + " name", kind, 3, 3, pos, "Нежить на столе: 5.");

    [Fact]
    public void The_plan_becomes_marks_at_the_right_places()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var m = Recruit() with { Shop = [Card("A", 1), Card("B", 2), Card("C", 3)], ShopSize = 4, Board = [Card("D", 1), Card("E", 2)] };
        vm.Apply(At(th, m) with { Turn = Plan([Pick("A", 1, PickKind.Triple), Pick("B", 2), Pick("C", 3)], sell: Card("E", 2), upgrade: true, refresh: true, freeze: true) });

        var triple = Assert.Single(vm.Marks, k => k.Kind == FieldMarkKind.Triple);
        Assert.Equal("A name", triple.Title);
        Assert.False(triple.Guess);
        // The first of four: the spell at position 4 moves the whole row left.
        Assert.Equal(GameField.ShopSlot(W, 0, 4).CenterX, triple.Box.CenterX);

        var buys = vm.Marks.Where(k => k.Kind == FieldMarkKind.Buy).ToList();
        Assert.Equal(["Брать 1", "Брать 2"], buys.Select(k => k.Label));
        Assert.All(buys, k => Assert.True(k.Guess));

        var sell = Assert.Single(vm.Marks, k => k.Kind == FieldMarkKind.Sell);
        Assert.Equal(GameField.BoardSlot(W, 1, 2).CenterX, sell.Box.CenterX);
        Assert.Contains(vm.Marks, k => k.Kind == FieldMarkKind.Upgrade);
        Assert.Contains(vm.Marks, k => k.Kind == FieldMarkKind.Refresh);
        Assert.Contains(vm.Marks, k => k.Kind == FieldMarkKind.Freeze);
    }

    [Fact]
    public void A_single_pick_has_no_number_and_no_plan_means_no_marks()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var m = Recruit() with { Shop = [Card("A", 1)], ShopSize = 1 };
        vm.Apply(At(th, m) with { Turn = Plan([Pick("A", 1)]) });
        Assert.Equal("Брать", Assert.Single(vm.Marks, k => k.Kind == FieldMarkKind.Buy).Label);
        vm.Apply(At(th, m));
        Assert.Empty(vm.Marks);
    }

    [Fact]
    public void The_turn_numbers_are_facts_inside_the_field_and_do_not_overlap_seat_tags()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        vm.Apply(At(th, Recruit()) with { Turn = Plan() });
        var stats = vm.Marks.Where(k => k.Kind == FieldMarkKind.Stat).ToList();
        Assert.Equal(["4/6", "7", "6", "5", "14", "11", "15"], stats.Select(k => k.Label));
        Assert.Equal(["таверна", "золото", "прокачка", "нежить", "бьёшь", "получишь", "лимит"], stats.Select(k => k.Sub));
        Assert.All(stats, k => { Assert.False(k.Guess); Assert.True(k.Box.Bottom <= GameField.Height); });
        Assert.Contains("18", stats[4].Body);
        Assert.Contains("15", stats[4].Body);
        var lastTag = GameField.SeatSlot(W, 8, false).Right + 6 + 150;
        Assert.All(stats, k => Assert.True(k.Box.X >= lastTag));
    }

    [Fact]
    public void Unknown_numbers_show_a_dash_lethal_damage_alerts_and_old_boards_mute()
    {
        var unknown = TurnStats.From(Plan(gold: null, cost: null, taken: null, age: null, cap: null));
        Assert.Equal("–", unknown[1].Value);
        Assert.Equal("–", unknown[2].Value);
        Assert.Equal("–", unknown[5].Value);
        Assert.Equal("–", unknown[6].Value);
        Assert.Equal("макс", TurnStats.From(Plan(tier: 6, cost: null))[2].Value);

        Assert.True(TurnStats.From(Plan(taken: 30, health: 30))[5].Alert);
        var old = TurnStats.From(Plan(taken: 11, health: 30, age: 3))[5];
        Assert.False(old.Alert);
        Assert.True(old.Muted);
        Assert.Contains("3 раунда назад", old.Body);
    }

    [Fact]
    public void An_offer_names_the_best_card_at_the_top_and_says_it_is_a_rule()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        vm.Apply(At(th, Recruit()) with { Turn = Plan(choice: new ChoicePick("A", "Король", "Нежить на столе: 5.")) });
        var tag = Assert.Single(vm.Marks, k => k.Kind == FieldMarkKind.Choice);
        Assert.Equal("Выбор: Король", tag.Label);
        Assert.Equal(W / 2, tag.Box.CenterX, 3);
        Assert.Contains("Правило", tag.Body);
    }

    [Fact]
    public void Hover_opens_a_popup_inside_the_field_and_leaving_closes_it()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        vm.Apply(At(th, Recruit() with { Players = [new PlayerView(2, "H", "Hero", false, 1, 3, 20, 0, false, true, new LastSeenBoard(3, [Card("A", 1), Card("A", 2)]))] }));
        var seat = Assert.Single(vm.Marks);
        Assert.StartsWith("Мурлоки 2", seat.Label);
        Assert.True(seat.Muted);

        vm.Hover(seat.Hit.X + 1, seat.Hit.Y + 1);
        Assert.NotNull(vm.Popup);
        Assert.True(vm.Popup!.X >= 4);
        Assert.Contains("Таверна 3", vm.Popup.Body);
        Assert.Contains("2 раунда назад", vm.Popup.Body);

        vm.Hover(5, 1000);
        Assert.Null(vm.Popup);
    }

    [Fact]
    public void Local_seat_and_duos_leaderboard_get_no_tags()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var seat = new PlayerView(1, "H", "Hero", true, 1, 3, 20, 0, false, false, null);
        vm.Apply(At(th, Recruit() with { Players = [seat] }));
        Assert.Empty(vm.Marks);
        vm.Apply(At(th, Recruit() with { Players = [seat with { IsLocal = false }], IsDuos = true }));
        Assert.Empty(vm.Marks);
    }

    [Fact]
    public void Calibration_moves_marks_and_guides_show_outside_a_match()
    {
        using var th = new TempHost();
        var cfg = th.Host.Settings.Value;
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var s = At(th, Recruit() with { Players = [new PlayerView(2, "H", "Hero", false, 1, 3, 20, 0, false, false, null)] });
        vm.Apply(s);
        var x = vm.Marks[0].Hit.X;
        cfg.FieldOffsetX = 10;
        vm.Apply(s);
        Assert.Equal(x + 10, vm.Marks[0].Hit.X);

        var idle = At(th, MatchSnapshot.Empty);
        Assert.False(FieldRules.Visible(idle, cfg));
        cfg.FieldGuides = true;
        Assert.True(FieldRules.Visible(idle, cfg));
        vm.Apply(idle);
        Assert.Equal(15, vm.Marks.Count);
        cfg.PanelsVisible = false;
        Assert.False(FieldRules.Visible(idle, cfg));
    }

    [Fact]
    public void Seat_popup_draws_the_last_seen_board_in_order()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        vm.Apply(At(th, Recruit() with { Players = [new PlayerView(2, "H", "Hero", false, 1, 3, 20, 0, false, false, new LastSeenBoard(4, [Card("A", 2), Card("B", 1, golden: true)]))] }));
        var seat = Assert.Single(vm.Marks);
        vm.Hover(seat.Hit.X + 1, seat.Hit.Y + 1);
        Assert.Equal(["B", "A"], vm.Popup!.Minions.Select(c => c.CardId));
        Assert.True(vm.Popup.Minions[0].Golden);
        Assert.Equal("2/3", vm.Popup.Minions[1].Stats);
        Assert.True(vm.Popup.X + vm.Popup.BoxWidth <= W);
    }

    [Fact]
    public void Preview_strip_is_labelled_as_a_what_if_and_hides_without_a_result()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var enemy = new PlayerView(2, "H", "Hero", false, 1, 3, 20, 0, false, true, new LastSeenBoard(3, [Card("A", 1)]));
        var m = Recruit() with { Board = [Card("B", 1)], Players = [enemy] };
        var c = BgCompass.Core.Sim.CombatPreview.For(m)!;
        var done = new SimResult(true, null, 0.5, 0.1, 0.4, null, null, null, null, 0, 100, []);
        vm.Apply(At(th, m) with { PreviewCombat = c, Preview = new SimView(SimStatus.Done, done) });
        Assert.True(vm.ShowOdds);
        Assert.Contains("Если бой сейчас", vm.OddsCaption);
        Assert.Contains("2р назад", vm.OddsCaption);

        vm.Apply(At(th, m) with { PreviewCombat = c, Preview = new SimView(SimStatus.Running, null) });
        Assert.False(vm.ShowOdds);
        vm.Apply(At(th, m));
        Assert.False(vm.ShowOdds);
        Assert.Empty(vm.OddsCaption);
    }

    [Fact]
    public void Guard_needs_a_problem_on_several_polls_in_a_row()
    {
        var g = new FieldGuard(3);
        Assert.False(g.Report("x"));
        Assert.False(g.Report("x"));
        Assert.False(g.Report(null));
        Assert.False(g.Report("x"));
        Assert.False(g.Report("x"));
        Assert.True(g.Report("x"));
    }
}
