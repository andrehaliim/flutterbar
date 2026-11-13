using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Docking;
using StatefulBar.App.Native;

namespace StatefulBar.App.Flyouts;

/// <summary>
/// The one flyout popup above the widget (spec §7.1, Placement=Top). It registers with
/// <see cref="TaskbarDocking.TrackPopup"/> (widget topmost reassert suspended while open, popup HWND made topmost).
/// WPF popups never activate on click, so on open the popup HWND is brought to the foreground explicitly for the
/// search box to receive keys; losing activation closes it, as do Esc and a click outside (StaysOpen=false).
/// Each <see cref="Show"/> returns a token cancelled when that content is closed or replaced (in-flight refreshes stop).
/// Nothing opens while the docking hides the widget (fullscreen / auto-hide: <c>canShow</c>), and the window that had the
/// foreground before the flyout (e.g. the IDE) gets it back when the flyout closes while still holding it.
/// </summary>
internal sealed class FlyoutHost
{
    private readonly Func<FrameworkElement?> _anchor;
    private readonly Func<bool> _canShow;
    private readonly Popup _popup;
    private readonly Border _frame;
    private CancellationTokenSource? _contentCts;
    private FrameworkElement? _active;
    private IInputElement? _focus;
    private HwndSource? _source;
    private bool _activated;
    private IntPtr _previousForeground;

    /// <param name="canShow">False while the docking hides the widget (WPF's IsVisible does not see a native SW_HIDE).</param>
    public FlyoutHost(Func<FrameworkElement?> anchor, Func<bool> canShow)
    {
        _anchor = anchor;
        _canShow = canShow;
        _frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Width = DefaultWidth,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "Fb.Background");
        _frame.SetResourceReference(Border.BorderBrushProperty, "Fb.Separator");
        _frame.SetResourceReference(TextElement.ForegroundProperty, "Fb.Foreground");
        TextElement.SetFontFamily(_frame, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        TextElement.SetFontSize(_frame, 12);
        _frame.PreviewKeyDown += OnPreviewKeyDown;

        _popup = new Popup
        {
            Child = _frame,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        TaskbarDocking.TrackPopup(_popup); // registered first: its Opened handler makes the popup topmost
        _popup.Opened += OnOpened;
        _popup.Closed += OnClosed;
    }

    public const double DefaultWidth = 320;

    /// <summary>The branch flyout with its changes panel open (file list left, diff right).</summary>
    public const double WideWidth = 800;

    /// <summary>The popup closed (any reason).</summary>
    public event Action? Closed;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The flyout the current token belongs to: the popup's content, or the section nested in it.</summary>
    public object? Content => _active;

    /// <summary>
    /// Shows <paramref name="content"/> (replacing what is shown) and focuses <paramref name="focus"/>. When the content hosts
    /// another flyout (the combined flyout's section), <paramref name="active"/> names it: the returned token, <see cref="Content"/>
    /// and <see cref="Refocus"/> then refer to that flyout.
    /// </summary>
    public CancellationToken Show(FrameworkElement content, IInputElement? focus, FrameworkElement? active = null)
    {
        CancelContent();
        _contentCts = new CancellationTokenSource();
        var token = _contentCts.Token;
        _frame.Width = DefaultWidth; // only the content that asked for more width keeps it (SetWidth after Show)
        _frame.Child = content;
        _active = active ?? content;
        _focus = focus;
        if (_popup.IsOpen)
        {
            FocusContent();
            return token;
        }

        var anchor = _anchor();
        if (anchor is null || !_canShow())
        {
            AppLog.Write($"flyout {content.GetType().Name} not shown: widget hidden");
            CancelContent();
            return token;
        }
        _popup.PlacementTarget = anchor;
        _popup.IsOpen = true;
        return token;
    }

    /// <summary>Takes <paramref name="content"/> out of the popup frame when it is shown there (it is about to be nested in another flyout).</summary>
    public void Release(FrameworkElement content)
    {
        if (ReferenceEquals(_frame.Child, content))
        {
            _frame.Child = null;
        }
    }

    /// <summary>
    /// Widens (or restores) the open popup for its current content; <see cref="Show"/> and closing go back to
    /// <see cref="DefaultWidth"/>, so no other flyout inherits it. WPF keeps the popup on screen when it grows.
    /// </summary>
    public void SetWidth(double width) => _frame.Width = width;

    public void Close()
    {
        if (_popup.IsOpen)
        {
            RestoreForeground(); // before the popup HWND goes away, while it still holds the foreground
            _popup.IsOpen = false;
        }
    }

    /// <summary>Moves keyboard focus inside the open <paramref name="content"/> (e.g. after it was filled asynchronously).</summary>
    public void Refocus(FrameworkElement content, IInputElement focus)
    {
        if (_popup.IsOpen && ReferenceEquals(_active, content))
        {
            _focus = focus;
            FocusContent();
        }
    }

    /// <summary>The widget moved or resized: keep the popup attached to it.</summary>
    public void Reposition()
    {
        if (_popup.IsOpen)
        {
            var offset = _popup.HorizontalOffset; // any offset change makes WPF recompute the placement
            _popup.HorizontalOffset = offset + 1;
            _popup.HorizontalOffset = offset;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _activated = false;
        _source = PresentationSource.FromVisual(_frame) as HwndSource;
        if (_source is null)
        {
            AppLog.Write("flyout opened without HwndSource: keyboard input unavailable");
            return;
        }
        _source.AddHook(WndProc);
        _previousForeground = NativeMethods.GetForegroundWindow();
        _activated = NativeMethods.SetForegroundWindow(_source.Handle);
        if (!_activated)
        {
            AppLog.Limited("flyout-activate", $"flyout SetForegroundWindow(0x{_source.Handle:X}) refused: search box gets no keys until clicked", 30);
        }
        FocusContent();
    }

    private void FocusContent()
    {
        var focus = _focus;
        _frame.Dispatcher.BeginInvoke(() =>
        {
            if (_popup.IsOpen && focus is not null)
            {
                Keyboard.Focus(focus);
            }
        }, DispatcherPriority.Input);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RestoreForeground();
        _source?.RemoveHook(WndProc);
        _source = null;
        _activated = false;
        _frame.Width = DefaultWidth;
        CancelContent();
        Closed?.Invoke();
    }

    // Only while the flyout (or another window of ours) still holds the foreground: never take it from a window
    // the user activated meanwhile.
    private void RestoreForeground()
    {
        var previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        var wasActivated = _activated;
        _activated = false; // the WM_ACTIVATE(inactive) that follows must not schedule another Close
        if (!wasActivated || previous == IntPtr.Zero || !NativeMethods.IsWindow(previous))
        {
            return;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        if (foreground == IntPtr.Zero || pid == (uint)Environment.ProcessId)
        {
            if (!NativeMethods.SetForegroundWindow(previous))
            {
                AppLog.Limited("flyout-restore", $"flyout closed: foreground could not be returned to 0x{previous:X}", 30);
            }
        }
    }

    private void CancelContent()
    {
        if (_contentCts is { } cts)
        {
            _contentCts = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Activation moved elsewhere (Alt+Tab, click in another app): close like a Windows flyout.
        if (msg == NativeMethods.WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) == NativeMethods.WA_INACTIVE && _activated)
        {
            _frame.Dispatcher.BeginInvoke(Close);
        }
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
