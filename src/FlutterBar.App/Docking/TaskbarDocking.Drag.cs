using System.Windows.Input;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.App.Docking;

/// <summary>
/// "Drag widget" mode (spec §7.3): horizontal move with mouse capture (never <c>Window.DragMove</c>, which activates the
/// window and moves both axes). The gesture only works while Drag widget is checked, so normal clicks on chips and
/// buttons never start it. Layout is not recomputed meanwhile. The width only comes from the Size menu (no manual resize).
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
        if (!_dragMode || _dead || _gesture != Gesture.None)
        {
            return;
        }
        if (!NativeMethods.GetCursorPos(out var p) || NativeMethods.WindowBox(_hwnd) is not { } win)
        {
            return;
        }
        _gesture = Gesture.Move;
        _gestureStartCursor = p.X;
        _gestureStartRect = win;
        _window.CaptureMouse();
        e.Handled = true; // in drag mode a press never reaches the chips/buttons
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None || !_window.IsMouseCaptured)
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

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }
        e.Handled = true;
        FinishGesture();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None)
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
