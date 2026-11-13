namespace StatefulBar.Core.Orchestration;

/// <summary>
/// Decides what the single toast slot shows. A plain toast never replaces a pending confirmation (that would answer
/// the user's question "no" behind their back): it waits, the newest one wins, and it is shown once the confirmation
/// is answered or dismissed. A newer confirmation still replaces an older one (the host answers that one "no").
/// Not thread-safe: the host uses it from the UI thread only.
/// </summary>
public sealed class ToastSlot<T>
    where T : class
{
    private T? _waiting;

    public bool ConfirmationPending { get; private set; }

    /// <summary>True: show <paramref name="toast"/> now. False: it waits for the pending confirmation.</summary>
    public bool OfferPlain(T toast)
    {
        if (!ConfirmationPending)
        {
            return true;
        }
        _waiting = toast;
        return false;
    }

    public void ConfirmationShown() => ConfirmationPending = true;

    /// <summary>The confirmation was answered or dismissed: returns the toast that waited for it, if any.</summary>
    public T? ConfirmationResolved()
    {
        ConfirmationPending = false;
        var waiting = _waiting;
        _waiting = null;
        return waiting;
    }
}
