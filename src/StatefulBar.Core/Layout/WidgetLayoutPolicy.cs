namespace StatefulBar.Core.Layout;

public enum WidgetSize
{
    Full,
    Compact,
    ButtonsOnly,
}

public static class WidgetLayoutPolicy
{
    // Ordered from largest to smallest; a step down moves one index forward.
    private static readonly WidgetSize[] Order = { WidgetSize.Full, WidgetSize.Compact, WidgetSize.ButtonsOnly };

    /// <summary>
    /// Picks the largest size that fits <paramref name="available"/>, starting at <paramref name="preferred"/>
    /// and stepping down one size at a time. Returns null when nothing fits (the widget is hidden).
    /// </summary>
    public static WidgetSize? Choose(WidgetSize preferred, double available, IReadOnlyDictionary<WidgetSize, double> requiredWidth)
    {
        for (var i = Array.IndexOf(Order, preferred); i < Order.Length; i++)
        {
            var size = Order[i];
            if (requiredWidth.TryGetValue(size, out var width) && width <= available)
            {
                return size;
            }
        }

        return null;
    }
}
