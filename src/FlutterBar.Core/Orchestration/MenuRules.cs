using FlutterBar.Core.Layout;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Orchestration;

/// <summary>Size submenu of the right-click menu (spec §7.4).</summary>
public static class WidgetSizeMenu
{
    public static string Label(WidgetSize size) => size switch
    {
        WidgetSize.Compact => "Compact",
        WidgetSize.ButtonsOnly => "Buttons only",
        _ => "Full",
    };

    /// <summary>
    /// Writes <c>widget.size</c>. <c>widthOverride</c> is the width of the preferred size (left-edge resize, spec §7.2),
    /// so a different size drops it: the old width must not carry over. False (nothing changed) for the same size.
    /// </summary>
    public static bool Apply(WidgetSettings widget, WidgetSize size)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (WidgetSizeNames.Parse(widget.Size) == size)
        {
            return false;
        }
        widget.Size = WidgetSizeNames.ToSettingName(size);
        widget.WidthOverride = null;
        return true;
    }
}

/// <summary>"Exit" (spec §7.4): with an active session the user confirms <see cref="UiText.ConfirmExit"/> first, then the session is stopped.</summary>
public static class ExitFlow
{
    public static bool NeedsConfirmation(SessionState state) => SessionGuard.NeedsStopConfirmation(state);
}
