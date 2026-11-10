using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class MatchRoleStateTests
{
    [Fact]
    public void QueueRoleIsOnlyForCurrentMatch()
    {
        var state = new MatchRoleState(Position.Carry);
        Assert.True(state.Enter(null));
        Assert.True(state.ApplyAutomatic(Position.HardSupport, MatchRoleSource.Queue));
        Assert.False(state.Enter("123")); // GSI match ID arrived after draft started
        Assert.Equal(Position.HardSupport, state.CurrentPosition);
        Assert.Equal(Position.Carry, state.DefaultPosition);
        Assert.True(state.Leave());
        Assert.Equal(Position.Carry, state.CurrentPosition);
    }

    [Fact]
    public void ManualRoleBeatsEveryAutomaticSource()
    {
        var state = new MatchRoleState(Position.Carry);
        state.Enter("123");
        state.ApplyAutomatic(Position.Offlane, MatchRoleSource.Queue);
        state.SelectManual(Position.Mid);
        Assert.False(state.ApplyAutomatic(Position.HardSupport, MatchRoleSource.Screen));
        Assert.Equal(Position.Mid, state.CurrentPosition);
        Assert.Equal(Position.Carry, state.DefaultPosition);
    }

    [Fact]
    public void ScreenRoleBeatsQueueAndLane()
    {
        var state = new MatchRoleState(Position.Carry);
        state.Enter("123");
        state.ApplyAutomatic(Position.Offlane, MatchRoleSource.Queue);
        Assert.False(state.ApplyAutomatic(Position.Carry, MatchRoleSource.Lane));
        Assert.True(state.ApplyAutomatic(Position.Mid, MatchRoleSource.Screen));
        Assert.False(state.ApplyAutomatic(Position.Offlane, MatchRoleSource.Queue));
        Assert.Equal(Position.Mid, state.CurrentPosition);
        Assert.True(state.Enter("456"));
        Assert.Equal(Position.Carry, state.CurrentPosition);
    }

    [Fact]
    public void DefaultChangeDoesNotOverrideKnownMatchRole()
    {
        var state = new MatchRoleState(Position.Carry);
        state.Enter("123");
        state.SetDefault(Position.Mid);
        Assert.Equal(Position.Mid, state.CurrentPosition);
        state.ApplyAutomatic(Position.Offlane, MatchRoleSource.Queue);
        state.SetDefault(Position.HardSupport);
        Assert.Equal(Position.Offlane, state.CurrentPosition);
        state.Leave();
        Assert.Equal(Position.HardSupport, state.CurrentPosition);
    }
}
