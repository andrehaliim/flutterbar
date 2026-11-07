using FlutterBar.Core.Orchestration;

namespace FlutterBar.Core.Tests.Orchestration;

public sealed class ToastSlotTests
{
    [Fact]
    public void PlainToast_WithoutConfirmation_ShowsAtOnce()
    {
        var slot = new ToastSlot<string>();

        Assert.True(slot.OfferPlain("Rescan selesai"));
        Assert.Null(slot.ConfirmationResolved());
    }

    [Fact]
    public void PlainToast_DuringConfirmation_WaitsAndShowsAfterAnswer()
    {
        // A "Session failed" / settings-save toast must not silently answer "Stop the session and exit?" with no.
        var slot = new ToastSlot<string>();
        slot.ConfirmationShown();

        Assert.False(slot.OfferPlain("Session failed: boom"));
        Assert.True(slot.ConfirmationPending);

        Assert.Equal("Session failed: boom", slot.ConfirmationResolved());
        Assert.False(slot.ConfirmationPending);
        Assert.True(slot.OfferPlain("next"));
    }

    [Fact]
    public void SeveralPlainToasts_DuringConfirmation_NewestWins()
    {
        var slot = new ToastSlot<string>();
        slot.ConfirmationShown();

        slot.OfferPlain("first");
        slot.OfferPlain("second");

        Assert.Equal("second", slot.ConfirmationResolved());
        Assert.Null(slot.ConfirmationResolved());
    }

    [Fact]
    public void NewerConfirmation_KeepsTheWaitingToast()
    {
        var slot = new ToastSlot<string>();
        slot.ConfirmationShown();
        slot.OfferPlain("waiting");

        slot.ConfirmationShown(); // replaces the first question

        Assert.True(slot.ConfirmationPending);
        Assert.Equal("waiting", slot.ConfirmationResolved());
    }
}
