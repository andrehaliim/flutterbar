using System.Windows;
using System.Windows.Interop;

namespace StatefulBar.App.Projects;

/// <summary>
/// Hidden, never-shown owner for a modal system dialog opened from the widget or a flyout. Without an explicit owner
/// WPF falls back to the thread's active window, and the taskbar attaches its input queue to the widget's thread, so
/// that can be the taskbar itself (or the taskbar-owned widget): the modal dialog would disable it. This window has its
/// own HWND, no owner and sits at the centre of the work area, where the dialog then opens.
/// </summary>
internal sealed class DialogOwnerWindow : IDisposable
{
    private readonly Window _window;

    public DialogOwnerWindow()
    {
        var area = SystemParameters.WorkArea;
        _window = new Window
        {
            Title = "FlutterBar",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = area.Left + (area.Width / 2),
            Top = area.Top + (area.Height / 3),
        };
        new WindowInteropHelper(_window).EnsureHandle();
    }

    public Window Window => _window;

    public void Dispose() => _window.Close();
}
