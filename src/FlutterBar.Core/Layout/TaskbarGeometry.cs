namespace FlutterBar.Core.Layout;

public readonly record struct Box(double Left, double Top, double Right, double Bottom);

public static class TaskbarGeometry
{
    /// <summary>
    /// Returns the free horizontal strip between the occupied app-area boxes and the tray, or null when there is no room.
    /// <paramref name="appButtons"/> should contain every occupied box left of the tray.
    /// </summary>
    public static Box? FreeAreaLeftOfTray(Box taskbar, Box tray, IReadOnlyList<Box> appButtons, double margin = 8)
    {
        var occupiedRight = taskbar.Left;

        foreach (var button in appButtons)
        {
            if (button.Left >= tray.Left)
            {
                continue;
            }

            if (button.Top >= taskbar.Bottom || button.Bottom <= taskbar.Top)
            {
                continue;
            }

            occupiedRight = Math.Max(occupiedRight, button.Right);
        }

        var left = occupiedRight + margin;
        var right = tray.Left - margin;
        if (right - left <= 0)
        {
            return null;
        }

        return new Box(left, taskbar.Top, right, taskbar.Bottom);
    }
}
