using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;

namespace StatefulBar.Core.Tests.Docking;

public sealed class PlacementDebouncerTests
{
    private static Placement Full(double x) => new(WidgetSize.Full, x, false, null);

    private static Placement Compact(double x) => new(WidgetSize.Compact, x, false, null);

    [Fact]
    public void FirstCandidate_AppliedImmediately()
    {
        var d = new PlacementDebouncer();

        var r = d.Offer(Full(1202));

        Assert.Equal(DebounceAction.AppliedImmediate, r.Action);
        Assert.True(r.Applied);
        Assert.Equal(Full(1202), d.Applied);
    }

    [Fact]
    public void SameCandidateWithin3Px_Unchanged()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        Assert.Equal(DebounceAction.Unchanged, d.Offer(Full(1205)).Action);
        Assert.Equal(DebounceAction.Unchanged, d.Offer(Full(1199)).Action);
        Assert.Equal(Full(1202), d.Applied);
    }

    [Fact]
    public void DifferentCandidate_NeedsTwoConsecutiveQueries()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        var first = d.Offer(Full(750));
        var second = d.Offer(Full(752));

        Assert.Equal(DebounceAction.HeldBack, first.Action);
        Assert.Equal(1, first.HeldCount);
        Assert.Equal(DebounceAction.AppliedConfirmed, second.Action);
        Assert.Equal(Full(752), d.Applied);
    }

    [Fact]
    public void TransientCandidate_ThenBack_NotApplied()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        Assert.Equal(DebounceAction.HeldBack, d.Offer(Full(750)).Action);
        Assert.Equal(DebounceAction.Unchanged, d.Offer(Full(1202)).Action);
        // pending was cleared: a new different candidate starts over
        Assert.Equal(DebounceAction.HeldBack, d.Offer(Full(750)).Action);
        Assert.Equal(Full(1202), d.Applied);
    }

    [Fact]
    public void SizeChange_IsDifferentCandidate()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        Assert.Equal(DebounceAction.HeldBack, d.Offer(Compact(1202)).Action);
        Assert.Equal(DebounceAction.AppliedConfirmed, d.Offer(Compact(1202)).Action);
    }

    [Fact]
    public void NoFitFlag_IsPartOfIdentity()
    {
        var d = new PlacementDebouncer();
        d.Offer(new Placement(WidgetSize.ButtonsOnly, 1546, false, null));

        Assert.Equal(DebounceAction.HeldBack, d.Offer(new Placement(WidgetSize.ButtonsOnly, 1546, true, null)).Action);
    }

    [Fact]
    public void AlternatingCandidates_MaxHoldAfterFive_AppliesLatest()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        for (var i = 0; i < 5; i++)
        {
            var r = d.Offer(Full(i % 2 == 0 ? 700 : 900));
            Assert.Equal(DebounceAction.HeldBack, r.Action);
            Assert.Equal(i + 1, r.HeldCount);
        }

        var last = d.Offer(Full(800));

        Assert.Equal(DebounceAction.AppliedMaxHold, last.Action);
        Assert.Equal(Full(800), d.Applied);
    }

    [Fact]
    public void ArmImmediate_NextDifferentCandidateAppliedAtOnce()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));
        d.Offer(Full(700)); // held

        d.ArmImmediate();
        var r = d.Offer(Compact(400));

        Assert.Equal(DebounceAction.AppliedImmediate, r.Action);
        Assert.Equal(Compact(400), d.Applied);
        Assert.Equal(DebounceAction.HeldBack, d.Offer(Full(1202)).Action); // back to debouncing
    }

    [Fact]
    public void Reset_ClearsAppliedAndArmsImmediate()
    {
        var d = new PlacementDebouncer();
        d.Offer(Full(1202));

        d.Reset();

        Assert.Null(d.Applied);
        Assert.Equal(DebounceAction.AppliedImmediate, d.Offer(Full(500)).Action);
    }
}
