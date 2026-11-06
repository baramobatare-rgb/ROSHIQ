using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class RoleTests
{
    [Fact]
    public void UserConfirmedPosition_Wins()
    {
        var r = RoleDetector.Detect(Position.Mid, true, TestData.Hero("crystal_maiden"), null);
        Assert.Equal(Position.Mid, r.Position);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void WardsInInventory_SuggestSupport()
    {
        var r = RoleDetector.Detect(Position.Carry, false, TestData.Hero("lion"), new[] { "item_ward_observer", "item_tango" });
        Assert.True(r.Position is Position.HardSupport or Position.SoftSupport);
    }

    [Fact]
    public void UnusualPosition_FallsBackToPrimary()
    {
        var r = RoleDetector.Detect(Position.HardSupport, false, TestData.Hero("antimage"), Array.Empty<string>());
        Assert.Equal(Position.Carry, r.Position);
        Assert.Equal(Confidence.Low, r.Confidence);
    }

    [Fact]
    public void NoHero_UsesSelection()
    {
        var r = RoleDetector.Detect(Position.Offlane, false, null, null);
        Assert.Equal(Position.Offlane, r.Position);
    }

    [Fact]
    public void RoleTips_ExistForAllFivePositions()
    {
        var advisor = new RoleAdvisor(TestData.Db);
        foreach (var p in Enum.GetValues<Position>()) Assert.NotEmpty(advisor.TipsFor(p));
        Assert.Contains(advisor.HeroTips(TestData.Hero("antimage"), Position.HardSupport), t => t.Kind == ReasonKind.Risk);
    }
}
