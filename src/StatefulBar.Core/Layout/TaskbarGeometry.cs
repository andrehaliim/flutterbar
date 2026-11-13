namespace StatefulBar.Core.Layout;

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

    /// <summary>
    /// Every free strip of the taskbar, left to right: between merged occupied boxes and between the last box and the tray,
    /// each edge inset by <paramref name="margin"/>. Boxes starting at or right of <paramref name="trayLeft"/> are ignored.
    /// The last entry, when it ends at the tray, equals <see cref="FreeAreaLeftOfTray"/>.
    /// </summary>
    public static IReadOnlyList<Box> FreeGaps(Box taskbar, double trayLeft, IReadOnlyList<Box> occupied, double margin = 8)
    {
        var sorted = occupied.Where(b => b.Left < trayLeft).OrderBy(b => b.Left).ToList();
        var gaps = new List<Box>();
        var cursor = taskbar.Left;
        foreach (var box in sorted)
        {
            if (box.Left - margin > cursor + margin)
            {
                gaps.Add(new Box(cursor + margin, taskbar.Top, box.Left - margin, taskbar.Bottom));
            }
            cursor = Math.Max(cursor, box.Right);
        }
        if (trayLeft - margin > cursor + margin)
        {
            gaps.Add(new Box(cursor + margin, taskbar.Top, trayLeft - margin, taskbar.Bottom));
        }
        return gaps;
    }
}
