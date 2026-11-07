using BgCompass.Core;
using BgCompass.Core.Sim;
using Xunit;

namespace BgCompass.Tests;

public class CombatPreviewTests
{
    static MinionView Card(int id) => new(id, "A", "A", 2, 3, id, false, MinionKeywords.None, [], 1);

    static PlayerView Enemy(bool next = true, bool dead = false, LastSeenBoard? board = null) =>
        new(2, "H", "Hero", false, 1, 4, 15, 0, dead, next, board ?? new LastSeenBoard(3, [Card(9)]));

    static MatchSnapshot Recruit(params PlayerView[] players) => MatchSnapshot.Empty with
    {
        Phase = MatchPhase.Recruit, Round = 6, TavernTier = 3, Board = [Card(1)], Players = players,
    };

    [Fact]
    public void Pairs_the_current_board_with_the_next_opponents_last_one()
    {
        var me = new PlayerView(1, "M", "Me", true, 2, 3, 25, 0, false, false, null);
        var m = Recruit(me, Enemy());
        var c = CombatPreview.For(m)!;
        Assert.True(c.Complete);
        Assert.Same(m.Board, c.Friendly);
        Assert.Equal(9, Assert.Single(c.Enemy).EntityId);
        Assert.Equal((25, 15, 3, 4), (c.FriendlyHealth, c.EnemyHealth, c.FriendlyTier, c.EnemyTier));
        Assert.Equal(3, CombatPreview.BoardAge(m, c));
    }

    [Fact]
    public void Nothing_to_preview_without_both_boards_or_outside_recruit()
    {
        Assert.Null(CombatPreview.For(Recruit(Enemy(next: false))));
        Assert.Null(CombatPreview.For(Recruit(Enemy(dead: true))));
        Assert.Null(CombatPreview.For(Recruit(Enemy(board: new LastSeenBoard(3, [])))));
        Assert.Null(CombatPreview.For(Recruit(Enemy()) with { Board = [] }));
        Assert.Null(CombatPreview.For(Recruit(Enemy()) with { Phase = MatchPhase.Combat }));
    }
}
