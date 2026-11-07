using System.Windows.Input;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.App.Docking;

/// <summary>
/// "Drag widget" mode (spec §7.3): horizontal move with mouse capture (never <c>Window.DragMove</c>, which activates the
/// window and moves both axes) and left-edge resize (<c>widget.widthOverride</c>). Both gestures only work while Drag
/// widget is checked, so normal clicks on chips and buttons never start a gesture. Layout is not recomputed meanwhile.
/// </summary>
internal sealed partial class TaskbarDocking
{
    private const double ResizeEdgeDip = 6;

    private bool _dragMode;
    private bool _dragged;
    private int? _dragX;
    private Gesture _gesture;
    private int _gestureStartCursor;
    private Box _gestureStartRect;
    private int? _resizeWidth;

    private enum Gesture
    {
        None,
        Move,
        Resize,
    }

    /// <summary>Drag mode on/off (the visual cue is drawn by the widget via <see cref="DragModeChanged"/>).</summary>
    public event Action<bool>? DragModeChanged;

    /// <summary>Raised after a left-edge resize: new width of the preferred size in DIPs, to save as <c>widget.widthOverride</c>.</summary>
    public event Action<double>? WidthOverrideChanged;

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
        var resizable = _preferred != WidgetSize.ButtonsOnly && _effectiveSize == _preferred;
        _gesture = resizable && p.X - win.Left <= ResizeEdgeDip * Scale() ? Gesture.Resize : Gesture.Move;
        _gestureStartCursor = p.X;
        _gestureStartRect = win;
        _resizeWidth = null;
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
        if (_gesture == Gesture.Move)
        {
            var nx = (int)ManualPlacement.ClampX(taskbar, r.Left + dx, width);
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, nx, (int)r.Top, width, height, flags);
            _dragX = nx;
            _dragged = true;
        }
        else
        {
            var minWidth = MinResizeWidth();
            var left = (int)Math.Clamp(r.Left + dx, taskbar.Left, Math.Max(taskbar.Left, r.Right - minWidth));
            var newWidth = (int)r.Right - left; // the right edge stays put
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, left, (int)r.Top, newWidth, height, flags);
            _dragX = left;
            _resizeWidth = newWidth;
        }
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

    /// <summary>Normal end of a gesture: a resize reports the new width (DIPs).</summary>
    private void FinishGesture()
    {
        var gesture = _gesture;
        EndGesture();
        if (gesture == Gesture.Resize && _resizeWidth is { } physical)
        {
            var dip = Math.Round(physical / Scale());
            AppLog.Write($"resize end inst={_id}: width={physical} px ({dip} DIP) -> widthOverride");
            WidthOverrideChanged?.Invoke(dip);
        }
        else if (gesture == Gesture.Move)
        {
            AppLog.Write($"drag end inst={_id} x={_dragX}");
        }
        _resizeWidth = null;
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

    /// <summary>The resized preferred size may not get narrower than the next smaller size.</summary>
    private double MinResizeWidth()
    {
        var widths = PhysicalWidths(Scale());
        return _preferred == WidgetSize.Full ? widths[WidgetSize.Compact] : widths[WidgetSize.ButtonsOnly];
    }
}
