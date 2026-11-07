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
        // A "Sesi gagal" / settings-save toast must not silently answer "Stop sesi dan keluar?" with no.
        var slot = new ToastSlot<string>();
        slot.ConfirmationShown();

        Assert.False(slot.OfferPlain("Sesi gagal: boom"));
        Assert.True(slot.ConfirmationPending);

        Assert.Equal("Sesi gagal: boom", slot.ConfirmationResolved());
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
