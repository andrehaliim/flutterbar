using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;

namespace FlutterBar.App.Docking;

/// <summary>
/// v3 rule: while any context menu, tooltip or flyout is open the topmost reassert is suspended (SWP_NOZORDER), and on
/// open the popup's own HWND is made topmost, otherwise the widget's reassert would paint over it. Context menus and
/// tooltips are tracked app-wide through class handlers; flyouts (<see cref="Popup"/>) register with <see cref="TrackPopup"/>.
/// </summary>
internal sealed partial class TaskbarDocking
{
    private static TaskbarDocking? s_active; // the live instance (UI thread only)
    private readonly HashSet<object> _openPopups = [];

    static TaskbarDocking()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => s_active?.OnPopupOpened(s)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, _) => s_active?.OnPopupClosed(s)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => s_active?.OnPopupOpened(s)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent, new RoutedEventHandler((s, _) => s_active?.OnPopupClosed(s)));
    }

    private bool TopmostSuspended => _openPopups.Count > 0;

    /// <summary>For flyouts (Task 14): suspends the topmost reassert while <paramref name="popup"/> is open and makes it topmost.</summary>
    public static void TrackPopup(Popup popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        popup.Opened += (_, _) => s_active?.OnPopupOpened(popup);
        popup.Closed += (_, _) => s_active?.OnPopupClosed(popup);
    }

    // Suspend already when the menu starts opening, so no reassert slips in before Opened.
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_dead && _window.ContextMenu is { } menu)
        {
            _openPopups.Add(menu);
        }
    }

    private void OnPopupOpened(object popup)
    {
        if (_dead)
        {
            return;
        }
        _openPopups.Add(popup);
        var visual = popup is Popup p ? p.Child : popup as Visual;
        var kind = popup.GetType().Name;
        if (visual is not null && PresentationSource.FromVisual(visual) is HwndSource source)
        {
            NativeMethods.SetWindowPos(source.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            AppLog.Limited("popup-open:" + kind, $"{kind} opened inst={_id}: popup hwnd=0x{source.Handle:X} set TOPMOST, widget reassert suspended", 10);
        }
        else
        {
            AppLog.Limited("popup-nosource:" + kind, $"{kind} opened inst={_id}: popup HwndSource not found (reassert suspended anyway)", 10);
        }
    }

    private void OnPopupClosed(object popup)
    {
        if (!_dead && _openPopups.Remove(popup) && _openPopups.Count == 0)
        {
            RequestReassert("popup-closed");
        }
    }

    /// <summary>Timer failsafe: a suspension whose popup is no longer open (missed Closed event) is cleared.</summary>
    private void PopupFailsafe()
    {
        if (_openPopups.Count == 0)
        {
            return;
        }
        foreach (var popup in _openPopups.Where(p => !IsOpen(p)).ToList())
        {
            _openPopups.Remove(popup);
            AppLog.Write($"failsafe inst={_id}: {popup.GetType().Name} suspension set but IsOpen=false -> cleared");
        }
    }

    private static bool IsOpen(object popup) => popup switch
    {
        ContextMenu menu => menu.IsOpen,
        ToolTip tip => tip.IsOpen,
        Popup p => p.IsOpen,
        _ => false,
    };
}
