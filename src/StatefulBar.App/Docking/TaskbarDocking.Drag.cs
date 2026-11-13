using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;
using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;

namespace StatefulBar.App.Docking;

/// <summary>
/// "Drag widget" mode (spec §7.3): horizontal move with mouse capture (never <c>Window.DragMove</c>, which activates the
/// window and moves both axes). The gesture only works while Drag widget is checked, so normal clicks on chips and
/// buttons never start it. Even in drag mode a press is only a pending gesture: the move (and the capture) starts once
/// the cursor travelled the system drag distance, so a click without movement still reaches the chips and buttons.
/// Layout is not recomputed meanwhile. The width only comes from the Size menu (no manual resize).
/// </summary>
internal sealed partial class TaskbarDocking
{
    private bool _dragMode;
    private bool _dragged;
    private int? _dragX;
    private Gesture _gesture;
    private int _gestureStartCursor;
    private Box _gestureStartRect;

    private enum Gesture
    {
        None,

        /// <summary>Pressed in drag mode but not moved far enough yet: the press still belongs to the clicked control.</summary>
        Pending,
        Move,
    }

    /// <summary>Drag mode on/off (the visual cue is drawn by the widget via <see cref="DragModeChanged"/>).</summary>
    public event Action<bool>? DragModeChanged;

    /// <summary>
    /// Checking starts drag mode. Unchecking after a move locks the position: manual mode, <see cref="ManualOffsetChanged"/>
    /// with the offset from the taskbar's right edge. Unchecking without a move keeps the current mode.
    /// </summary>
    public bool DragMode
    {
        get => _dragMode;
        set => SetDragMode(value);
    }

    private void SetDragMode(bool on)
    {
        if (on == _dragMode || _dead)
        {
            return;
        }
        if (on)
        {
            _dragMode = true;
            _dragged = false;
            _dragX = NativeMethods.WindowBox(_hwnd) is { } r ? (int)r.Left : null;
            AppLog.Write($"drag mode ON inst={_id} (x={_dragX})");
        }
        else
        {
            EndGesture();
            _dragMode = false;
            if (_dragged && _dragX is { } x && NativeMethods.WindowBox(_tray) is { } taskbar && NativeMethods.WindowBox(_hwnd) is { } win)
            {
                var offset = ManualPlacement.OffsetFromX(taskbar, x, win.Right - win.Left);
                _manualOffset = offset;
                AppLog.Write($"drag mode OFF inst={_id} -> position locked, mode=manual offsetFromRight={offset}");
                ManualOffsetChanged?.Invoke(offset);
            }
            else
            {
                AppLog.Write($"drag mode OFF inst={_id} (no move, mode stays {(IsManual ? "manual" : "auto")})");
            }
            _dragX = null;
        }
        DragModeChanged?.Invoke(on);
        RequestReassert("drag-toggle");
    }

    private void AttachDragHandlers()
    {
        _window.PreviewMouseLeftButtonDown += OnDragDown;
        _window.PreviewMouseMove += OnDragMove;
        _window.PreviewMouseLeftButtonUp += OnDragUp;
        _window.LostMouseCapture += OnLostCapture;
    }

    private void DetachDragHandlers()
    {
        _window.PreviewMouseLeftButtonDown -= OnDragDown;
        _window.PreviewMouseMove -= OnDragMove;
        _window.PreviewMouseLeftButtonUp -= OnDragUp;
        _window.LostMouseCapture -= OnLostCapture;
    }

    private void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        // A Pending press whose release happened outside the window is simply replaced.
        if (!_dragMode || _dead || _gesture == Gesture.Move)
        {
            return;
        }
        if (!NativeMethods.GetCursorPos(out var p) || NativeMethods.WindowBox(_hwnd) is not { } win)
        {
            _gesture = Gesture.None;
            return;
        }
        // Not handled: until the cursor moves, the press belongs to the chip/button under it (a plain click works).
        _gesture = Gesture.Pending;
        _gestureStartCursor = p.X;
        _gestureStartRect = win;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.Pending && !TryStartMove(e))
        {
            return;
        }
        if (_gesture != Gesture.Move || !_window.IsMouseCaptured)
        {
            return;
        }
        if (!NativeMethods.GetCursorPos(out var p) || NativeMethods.WindowBox(_tray) is not { } taskbar)
        {
            return;
        }
        var r = _gestureStartRect;
        var dx = p.X - _gestureStartCursor;
        var width = (int)(r.Right - r.Left);
        var height = (int)(r.Bottom - r.Top);
        const uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var nx = (int)ManualPlacement.ClampX(taskbar, r.Left + dx, width);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, nx, (int)r.Top, width, height, flags);
        _dragX = nx;
        _dragged = true;
        e.Handled = true;
    }

    /// <summary>
    /// Pending → Move once the cursor travelled the system drag distance (physical px). Capturing the window takes the
    /// capture away from a pressed Button, which then releases without a Click.
    /// </summary>
    private bool TryStartMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _gesture = Gesture.None; // released outside the window: no up event reached us
            return false;
        }
        if (!NativeMethods.GetCursorPos(out var p))
        {
            return false;
        }
        var threshold = SystemParameters.MinimumHorizontalDragDistance * VisualTreeHelper.GetDpi(_window).DpiScaleX;
        if (Math.Abs(p.X - _gestureStartCursor) < threshold)
        {
            return false;
        }
        // Still Pending while capturing: the pressed Button's LostMouseCapture bubbles up to the window (OnLostCapture).
        if (!_window.CaptureMouse())
        {
            _gesture = Gesture.None;
            return false;
        }
        _gesture = Gesture.Move;
        AppLog.Write($"drag start inst={_id} x={_gestureStartRect.Left}");
        return true;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.Pending)
        {
            _gesture = Gesture.None; // no movement: the click goes through to the control
            return;
        }
        if (_gesture != Gesture.Move)
        {
            return;
        }
        e.Handled = true;
        FinishGesture();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        // Only the window losing its own capture ends a move; a child's LostMouseCapture also bubbles up here.
        if (_gesture != Gesture.Move || _window.IsMouseCaptured)
        {
            return;
        }
        AppLog.Write($"drag ended by LostMouseCapture inst={_id}, keeping x={_dragX}");
        FinishGesture();
    }

    /// <summary>Normal end of a gesture.</summary>
    private void FinishGesture()
    {
        EndGesture();
        AppLog.Write($"drag end inst={_id} x={_dragX}");
    }

    /// <summary>Ends a gesture without reporting anything (reset, teardown, drag off).</summary>
    private void EndGesture()
    {
        _gesture = Gesture.None; // before releasing: ReleaseMouseCapture raises LostMouseCapture synchronously
        if (_window.IsMouseCaptured)
        {
            _window.ReleaseMouseCapture();
        }
    }
}
