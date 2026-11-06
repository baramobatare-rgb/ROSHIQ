using Roshiq.Core.Models;
using Roshiq.Draft;
using Roshiq.Vision;
using Xunit;

namespace Roshiq.Tests;

public class AutoDraftTests
{
    private static DraftRecognitionResult Frame(SlotStatus status, string? key = "phantom_lancer", Side side = Side.Dire) => new(
        new[] { new SlotResult(new SlotRegion(side, 0, 0, 0, 4, 4),
            new SlotMatch(status, key, .96, Array.Empty<MatchCandidate>()), new RgbImage(4, 4)) }, true, TimeSpan.Zero);

    [Fact]
    public void SingleFrameAndUncertainCandidates_AreNotPicks()
    {
        var tracker = new DraftPickTracker();
        tracker.Apply(Frame(SlotStatus.Recognized)); Assert.Empty(tracker.Heroes(Side.Dire));
        tracker.Apply(Frame(SlotStatus.Ambiguous)); Assert.Empty(tracker.Heroes(Side.Dire));
        tracker.Apply(Frame(SlotStatus.Recognized)); Assert.Empty(tracker.Heroes(Side.Dire));
        tracker.Apply(Frame(SlotStatus.Recognized)); Assert.Equal("phantom_lancer", Assert.Single(tracker.Heroes(Side.Dire)));
    }

    [Fact]
    public void BriefOcclusionKeepsPick_ThreeMissesRemoveIt()
    {
        var tracker = new DraftPickTracker();
        tracker.Apply(Frame(SlotStatus.Recognized)); tracker.Apply(Frame(SlotStatus.Recognized));
        tracker.Apply(Frame(SlotStatus.Unknown)); Assert.Single(tracker.Heroes(Side.Dire));
        tracker.Apply(Frame(SlotStatus.Empty)); Assert.Single(tracker.Heroes(Side.Dire));
        tracker.Apply(Frame(SlotStatus.Unknown)); Assert.Empty(tracker.Heroes(Side.Dire));
    }

    [Fact]
    public void NewHeroMustBeConfirmed_AndResetClearsPreviousMatch()
    {
        var tracker = new DraftPickTracker();
        tracker.Apply(Frame(SlotStatus.Recognized)); tracker.Apply(Frame(SlotStatus.Recognized));
        tracker.Apply(Frame(SlotStatus.Recognized, "axe")); Assert.Equal("phantom_lancer", Assert.Single(tracker.Heroes(Side.Dire)));
        tracker.Apply(Frame(SlotStatus.Recognized, "axe")); Assert.Equal("axe", Assert.Single(tracker.Heroes(Side.Dire)));
        tracker.Reset(); Assert.Empty(tracker.Heroes(Side.Dire));
    }

    [Fact]
    public void DuplicateHeroAcrossTeams_IsRejected()
    {
        var tracker = new DraftPickTracker();
        var a = Frame(SlotStatus.Recognized);
        var b = Frame(SlotStatus.Recognized, side: Side.Radiant);
        var frame = a with { Slots = a.Slots.Concat(b.Slots).ToList() };
        tracker.Apply(frame); tracker.Apply(frame);
        Assert.Empty(tracker.Heroes(Side.Dire)); Assert.Empty(tracker.Heroes(Side.Radiant));
    }

    [Fact]
    public void CounterpickMode_DoesNotInventCountersBeforeEnemyPicks()
    {
        var analyzer = new DraftAnalyzer(TestData.Db);
        Assert.Empty(analyzer.Analyze(DraftState.Empty(Position.Carry), countersOnly: true).Top);
    }

    [Fact]
    public void CounterpickMode_RespectsRolePickedHeroesAndCounterReasons()
    {
        var state = new DraftState(new[] { TestData.Hero("axe") },
            new[] { TestData.Hero("phantom_lancer"), TestData.Hero("naga_siren") }, Position.Offlane);
        var analysis = new DraftAnalyzer(TestData.Db).Analyze(state, countersOnly: true);
        Assert.Equal(3, analysis.Top.Count);
        Assert.All(analysis.Top, r => {
            Assert.True(r.Hero.PlaysPosition(Position.Offlane));
            Assert.DoesNotContain(r.Hero.Id, state.Allies.Concat(state.Enemies).Select(h => h.Id));
            Assert.Contains(r.Reasons, x => x.Weight > 0 && x.Kind is ReasonKind.Counter or ReasonKind.Stats);
        });
    }
}
