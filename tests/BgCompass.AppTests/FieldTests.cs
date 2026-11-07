using BgCompass.App.Overlay;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.Core;
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

    [Fact]
    public void A_third_copy_is_marked_only_when_two_plain_copies_are_held()
    {
        using var th = new TempHost();
        var vm = new FieldVm(th.Host.Settings);
        vm.SetWidth(W);
        var m = Recruit() with
        {
            Board = [Card("A", 1), Card("B", 2, golden: true)],
            Hand = [Card("A", 1), Card("B", 2)],
            Shop = [Card("A", 1), Card("B", 2), Card("C", 3)],
            ShopSize = 4,
        };
        vm.Apply(At(th, m));
        var triple = Assert.Single(vm.Marks, k => k.Kind == FieldMarkKind.Triple);
        Assert.Equal("A name", triple.Title);
        // The first of four: the spell at position 4 moves the whole row left.
        Assert.Equal(GameField.ShopSlot(W, 0, 4).CenterX, triple.Box.CenterX);
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
}
