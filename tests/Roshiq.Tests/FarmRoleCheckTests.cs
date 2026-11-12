using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>
/// 0.20.3: роль по фарму на 10:00. 11.11.2026 Omniknight «поз. 5» собрал Radiance и 230 добиваний,
/// а Invoker «поз. 1» сыграл 1/5/37 с 43 добиваниями — подсказки матча шли для чужой роли.
/// </summary>
public class FarmRoleCheckTests
{
    private static FarmRoleCheck Walk(int x, int y)
    {
        var check = new FarmRoleCheck();
        for (int clock = 420; clock < 600; clock += 5) check.Observe(clock, x, y);
        return check;
    }

    [Fact]
    public void SupportWithCoreFarm_BecomesCoreOnItsLane()
    {
        // Radiant, лёгкая (нижняя) линия
        var guess = Walk(6000, -6000).TryDecide(600, 41, Position.HardSupport, "radiant", null);
        Assert.Equal(Position.Carry, guess?.Position);
        Assert.Contains("41", guess!.ReasonRu);
        Assert.Equal(Position.Mid, Walk(0, 0).TryDecide(600, 40, Position.SoftSupport, "dire", null)?.Position);
    }

    [Fact]
    public void CoreWithoutFarm_BecomesSupport()
    {
        // Dire: лёгкая линия — верхняя
        Assert.Equal(Position.HardSupport, Walk(-6000, 6000).TryDecide(600, 9, Position.Carry, "dire", null)?.Position);
        Assert.Equal(Position.SoftSupport, Walk(6000, -6000).TryDecide(600, 9, Position.Offlane, "dire", null)?.Position);
    }

    [Fact]
    public void MatchingFarm_ChangesNothing_AndDecidesOnce()
    {
        var check = Walk(0, 0);
        Assert.Null(check.TryDecide(599, 70, Position.HardSupport, "radiant", null));   // ещё рано
        Assert.Null(check.TryDecide(600, 20, Position.HardSupport, "radiant", null));   // фарм саппорта
        Assert.True(check.Done);
        Assert.Null(check.TryDecide(610, 80, Position.HardSupport, "radiant", null));   // решение уже принято
        Assert.Null(Walk(0, 0).TryDecide(600, 60, Position.Mid, "radiant", null));      // кор с фармом кора
    }

    [Fact]
    public void LateConnection_DoesNotGuess()
    {
        // ROSHIQ подключился на 25:00 — 40 добиваний у саппорта к этому времени не повод менять роль
        Assert.Null(new FarmRoleCheck().TryDecide(1500, 40, Position.HardSupport, "radiant", null));
    }

    [Fact]
    public void FarmBeatsScreenRole_ButNotManual()
    {
        var state = new MatchRoleState(Position.HardSupport);
        state.Enter("1");
        state.ApplyAutomatic(Position.HardSupport, MatchRoleSource.Screen);
        Assert.True(state.ApplyAutomatic(Position.Carry, MatchRoleSource.Farm));
        Assert.Equal(Position.Carry, state.CurrentPosition);
        state.SelectManual(Position.SoftSupport);
        Assert.False(state.ApplyAutomatic(Position.Mid, MatchRoleSource.Farm));
        Assert.Equal(Position.SoftSupport, state.CurrentPosition);
    }
}
