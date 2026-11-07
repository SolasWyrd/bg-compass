using BgCompass.Core.Logs;
using BgCompass.Core.Tracking;
using Xunit;

namespace BgCompass.Tests;

public class PowerParserTests
{
    [Fact]
    public void Parses_tag_change_with_bracketed_entity()
    {
        var e = PowerParser.Parse("D 13:19:31.5976432 GameState.DebugPrintPower() -         TAG_CHANGE Entity=[entityName=Pyrospike Millhouse id=93 zone=PLAY zonePos=0 cardId=TB_BaconShop_HERO_49_SKIN_A player=5] tag=DAMAGE value=4 ");
        Assert.Equal(PowerKind.TagChange, e.Kind);
        Assert.Equal(PowerSource.GameState, e.Source);
        Assert.Equal(new TimeSpan(0, 13, 19, 31, 597).Add(TimeSpan.FromTicks(6432)), e.Time);
        Assert.Equal(93, e.Entity.Id);
        Assert.Equal("TB_BaconShop_HERO_49_SKIN_A", e.Entity.CardId);
        Assert.Equal(5, e.Entity.Player);
        Assert.Equal(GameTag.Damage, e.Tag);
        Assert.Equal(4, e.Value);
    }

    [Fact]
    public void Time_of_line_is_read()
    {
        Assert.True(PowerParser.TryParseTime("D 23:59:59.9999999 PowerTaskList.DebugPrintPower() - BLOCK_END", out var t));
        Assert.Equal(new TimeSpan(0, 23, 59, 59).Add(TimeSpan.FromTicks(9999999)), t);
        Assert.False(PowerParser.TryParseTime("not a log line", out _));
    }

    public static TheoryData<string?> Garbage() =>
    [
        null, "", " ", "D", "D 13:", "D 99:99:99.0000000 GameState.DebugPrintPower() - TAG_CHANGE",
        "D 13:19:31.5976432 GameState.DebugPrintPower() -         TAG_CHANGE Entity=[entityName=Pyro id=93 zone=PL",
        "D 13:19:31.5976432 GameState.DebugPrintPower() -         TAG_CHANGE Entity=[entityName=Pyro id=notanumber zone=PLAY] tag=ZONE value=",
        "D 13:19:31.5976432 GameState.DebugPrintPower() -         TAG_CHANGE Entity=999999 tag=NO_SUCH_TAG value=xyz",
        "D 13:19:31.5976432 GameState.DebugPrintPower() -         TAG_CHANGE Entity=] tag= value=",
        "D 13:19:31.5976432 GameState.DebugPrintPower() - BLOCK_START BlockType=",
        "D 13:19:31.5976432 GameState.DebugPrintPower() - BLOCK_END",
        "D 13:19:31.5976432 GameState.DebugPrintPower() - FULL_ENTITY - Creating ID=abc CardID=",
        "D 13:19:31.5976432 GameState.DebugPrintPower() - SHOW_ENTITY - Updating Entity=[id=",
        "D 13:19:31.5976432 GameState.DebugPrintPower() -     Player EntityID=14 PlayerID=5 GameAccountId=[hi=zz lo=",
        "D 13:19:31.5976432 GameState.DebugPrintEntityChoices() - id=1 Player=X TaskList=2 ChoiceType=GENERAL",
        "D 13:19:31.5976432 GameState.DebugPrintEntityChoices() -   Entities[0]=[entityName=? id=",
        "D 13:19:31.5976432 GameState.DebugPrintEntitiesChosen() -   Entities[0]=",
        "D 13:19:31.5976432 GameState.DebugPrintGame() - PlayerID=, PlayerName=",
        "D 13:19:31.5976432 GameState.DebugPrintGame() - BuildNumber=notanumber",
        "\0\u0001ÿ\ud800 binary ☃ junk",
    ];

    [Theory]
    [MemberData(nameof(Garbage))]
    public void Garbage_lines_do_not_throw_in_parser_or_tracker(string? line)
    {
        PowerParser.Parse(line);
        foreach (var stream in Enum.GetValues<PowerStream>())
        {
            var t = TestData.NewTracker(stream);
            t.Feed(line!);
            _ = t.Snapshot;
        }
    }

    [Fact]
    public void Every_prefix_of_every_fixture_line_is_survivable()
    {
        foreach (var line in TestData.Fixture("synthetic_game.power.log").Concat(TestData.Fixture("real_2026_heroselect.power.log")).Take(900))
            for (int n = 0; n < line.Length; n += 3)
                PowerParser.Parse(line[..n]);
    }

    [Fact]
    public void Truncated_lines_and_unknown_entities_in_a_running_game_do_not_throw()
    {
        var lines = TestData.Fixture("synthetic_game.power.log");
        foreach (var stream in Enum.GetValues<PowerStream>())
        {
            var t = TestData.NewTracker(stream);
            for (int i = 0; i < lines.Length; i++)
            {
                // Every third line is cut in half, and unknown-entity noise is interleaved.
                t.Feed(i % 3 == 0 ? lines[i][..(lines[i].Length / 2)] : lines[i]);
                if (i % 5 == 0)
                {
                    t.Feed("D 13:01:30.0000000 GameState.DebugPrintPower() -     TAG_CHANGE Entity=[entityName=Ghost id=987654 zone=PLAY zonePos=1 cardId=NOPE player=13] tag=ZONE value=GRAVEYARD ");
                    t.Feed("D 13:01:30.0000000 GameState.DebugPrintPower() -     TAG_CHANGE Entity=NobodyKnowsMe tag=RESOURCES value=3 ");
                    t.Feed("D 13:01:30.0000000 PowerTaskList.DebugPrintPower() -     SHOW_ENTITY - Updating Entity=424242 CardID=X");
                    t.Feed("D 13:01:30.0000000 PowerTaskList.DebugPrintPower() -     BLOCK_END");
                }
                _ = t.Snapshot;
            }
            t.Flush();
        }
    }
}
