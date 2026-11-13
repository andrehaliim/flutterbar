using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Docking;
using StatefulBar.Core.Orchestration;

namespace StatefulBar.App.Widget;

/// <summary>Content of a toast: message, up to two buttons and a close button.</summary>
internal sealed partial class Toast : UserControl
{
    public Toast()
    {
        InitializeComponent();
    }

    public event Action? PrimaryClicked;

    public event Action? SecondaryClicked;

    public event Action? CloseClicked;

    public void Set(string message, string? primary, string? secondary)
    {
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        Actions.Visibility = primary is null && secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => PrimaryClicked?.Invoke();

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => SecondaryClicked?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
}

/// <summary>
/// Shows one toast at a time in a non-activating topmost popup above the widget (Placement=Top), registered with
/// <see cref="TaskbarDocking.TrackPopup"/>. Plain toasts close after 5 s (the timer pauses while the mouse is over them);
/// a confirmation stays until answered. A plain toast arriving meanwhile waits and is shown after the answer (newest
/// wins, <see cref="ToastSlot{T}"/>); a newer confirmation replaces the older one, which answers "no". While the docking
/// hides the widget (fullscreen / auto-hide) toasts are only written to diag.log and a confirmation answers "no".
/// </summary>
internal sealed class ToastHost
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly Func<FrameworkElement?> _anchor;
    private readonly Func<bool> _canShow;
    private readonly Popup _popup;
    private readonly Toast _toast = new();
    private readonly DispatcherTimer _timer;
    private readonly ToastSlot<PlainToast> _slot = new();
    private Func<Task>? _action;
    private TaskCompletionSource<bool>? _confirm;

    public ToastHost(Func<FrameworkElement?> anchor, Func<bool> canShow, Dispatcher dispatcher)
    {
        _anchor = anchor;
        _canShow = canShow;
        _popup = new Popup
        {
            Child = _toast,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        TaskbarDocking.TrackPopup(_popup);
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Lifetime };
        _timer.Tick += (_, _) =>
        {
            if (!_toast.IsMouseOver)
            {
                Close();
            }
        };
        _toast.PrimaryClicked += OnPrimary;
        _toast.SecondaryClicked += () => Close();
        _toast.CloseClicked += () => Close();
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Shows <paramref name="message"/>; <paramref name="action"/> runs when its button is pressed.</summary>
    public void Show(string message, string? actionLabel = null, Func<Task>? action = null)
    {
        if (!_slot.OfferPlain(new PlainToast(message, actionLabel, action)))
        {
            AppLog.Write($"toast (waits for the open confirmation): {message}");
            return;
        }
        if (!CanOpen("toast", message))
        {
            return;
        }
        _action = actionLabel is null ? null : action;
        _toast.Set(message, _action is null ? null : actionLabel, null);
        Open();
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Asks a yes/no question; completes with false when dismissed or replaced by another confirmation.</summary>
    public Task<bool> ConfirmAsync(string message, string yes, string no)
    {
        Answer(false); // an older question, if any, is replaced
        if (!CanOpen("confirm", message))
        {
            return Task.FromResult(false);
        }
        _action = null;
        _timer.Stop();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirm = tcs;
        _slot.ConfirmationShown();
        _toast.Set(message, yes, no);
        Open();
        return tcs.Task;
    }

    public void Close()
    {
        _timer.Stop();
        Answer(false);
        _popup.IsOpen = false;
        if (_slot.ConfirmationPending && _slot.ConfirmationResolved() is { } waiting)
        {
            Show(waiting.Message, waiting.ActionLabel, waiting.Action);
        }
    }

    /// <summary>The widget moved or resized: keep the toast attached to it.</summary>
    public void Reposition()
    {
        if (_popup.IsOpen)
        {
            var offset = _popup.HorizontalOffset;
            _popup.HorizontalOffset = offset + 1;
            _popup.HorizontalOffset = offset;
        }
    }

    private bool CanOpen(string kind, string message)
    {
        if (_anchor() is null || !_canShow())
        {
            Close();
            AppLog.Write($"{kind} (widget hidden, not shown): {message}");
            return false;
        }
        AppLog.Write($"{kind}: {message}");
        return true;
    }

    private void Open()
    {
        _popup.PlacementTarget = _anchor();
        _popup.IsOpen = true;
    }

    private void OnPrimary()
    {
        if (_confirm is not null)
        {
            Answer(true);
            Close();
            return;
        }
        var action = _action;
        Close();
        if (action is not null)
        {
            _ = RunActionAsync(action);
        }
    }

    private void Answer(bool value)
    {
        var tcs = _confirm;
        _confirm = null;
        tcs?.TrySetResult(value);
    }

    private sealed record PlainToast(string Message, string? ActionLabel, Func<Task>? Action);

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) // fire-and-forget button: report here, never let it go unobserved
        {
            AppLog.Write("toast action threw", ex);
            Show($"Action failed: {ex.Message}");
        }
    }
}
