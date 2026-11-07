using BgCompass.App.Dev;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.Core;
using Xunit;

namespace BgCompass.AppTests;

public class CompassTests
{
    static UiState At(TempHost th, MatchPhase phase) =>
        th.Host.State with { Match = th.Host.State.Match with { Phase = phase }, Log = DemoScenario.LogOk };

    [Fact]
    public void A_hand_picked_tab_holds_until_the_phase_changes()
    {
        using var th = new TempHost();
        var vm = new CompassVm(th.Host, () => { });
        vm.Apply(At(th, MatchPhase.Recruit));
        Assert.Same(vm.Build, vm.Body);
        Assert.Equal("Билд", vm.Tabs[0].Title);

        vm.Tabs[1].Select.Execute(null);
        vm.Apply(At(th, MatchPhase.Recruit));
        Assert.Same(vm.Opponent, vm.Body);

        vm.Apply(At(th, MatchPhase.Combat));
        Assert.Same(vm.Sim, vm.Body);
        Assert.Equal("Бой", vm.Tabs[0].Title);
        Assert.True(vm.Tabs[0].IsSelected);
    }

    [Fact]
    public void Game_over_and_status_have_no_tabs()
    {
        using var th = new TempHost();
        var vm = new CompassVm(th.Host, () => { });
        vm.Apply(At(th, MatchPhase.Recruit));
        Assert.True(vm.ShowTabs);
        vm.Apply(At(th, MatchPhase.GameOver));
        Assert.False(vm.ShowTabs);
        Assert.Same(vm.GameOver, vm.Body);
    }

    [Fact]
    public void The_panel_shows_for_a_game_or_a_missing_log_and_never_when_hidden()
    {
        using var th = new TempHost();
        var settings = new AppSettings();
        var idleOk = At(th, MatchPhase.Idle);
        Assert.False(PanelRules.Wanted(idleOk, settings));
        Assert.True(PanelRules.Wanted(idleOk with { Log = DemoScenario.LogMissing }, settings));
        Assert.True(PanelRules.Wanted(At(th, MatchPhase.Recruit), settings));
        settings.PanelsVisible = false;
        Assert.False(PanelRules.Wanted(At(th, MatchPhase.Recruit), settings));
    }

    [Fact]
    public void A_ready_update_appears_on_the_panel()
    {
        using var th = new TempHost();
        var vm = new CompassVm(th.Host, () => { });
        vm.Apply(At(th, MatchPhase.Recruit));
        Assert.False(vm.HasUpdate);
        vm.Apply(At(th, MatchPhase.Recruit) with { UpdateReadyVersion = "0.3.0" });
        Assert.True(vm.HasUpdate);
        Assert.Contains("0.3.0", vm.UpdateTip);
    }
}
