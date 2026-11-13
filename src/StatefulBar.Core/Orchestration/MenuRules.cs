using StatefulBar.Core.Layout;
using StatefulBar.Core.Sessions;
using StatefulBar.Core.Settings;

namespace StatefulBar.Core.Orchestration;

/// <summary>Size submenu of the right-click menu (spec §7.4).</summary>
public static class WidgetSizeMenu
{
    public static string Label(WidgetSize size) => size switch
    {
        WidgetSize.Compact => "Compact",
        WidgetSize.ButtonsOnly => "Buttons only",
        _ => "Full",
    };

    /// <summary>Tooltip of a Size entry the taskbar has no room for.</summary>
    public const string NoRoomToolTip = "Not enough free space on the taskbar for this size";

    /// <summary>Note under the Size entries naming the sizes the taskbar has no room for; null when everything fits.</summary>
    public static string? NoRoomNote(IReadOnlyCollection<WidgetSize> sizesThatFit)
    {
        ArgumentNullException.ThrowIfNull(sizesThatFit);
        var missing = new[] { WidgetSize.Full, WidgetSize.Compact }.Where(s => !sizesThatFit.Contains(s)).Select(Label).ToList();
        return missing.Count == 0 ? null : $"Not enough taskbar space for {string.Join(" and ", missing)}";
    }

    /// <summary>Writes <c>widget.size</c>. False (nothing changed) for the same size.</summary>
    public static bool Apply(WidgetSettings widget, WidgetSize size)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (WidgetSizeNames.Parse(widget.Size) == size)
        {
            return false;
        }
        widget.Size = WidgetSizeNames.ToSettingName(size);
        return true;
    }
}

/// <summary>"Exit" (spec §7.4): with an active session the user confirms <see cref="UiText.ConfirmExit"/> first, then the session is stopped.</summary>
public static class ExitFlow
{
    public static bool NeedsConfirmation(SessionState state) => SessionGuard.NeedsStopConfirmation(state);
}
