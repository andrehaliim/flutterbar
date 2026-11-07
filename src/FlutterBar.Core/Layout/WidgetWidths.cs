namespace FlutterBar.Core.Layout;

/// <summary>Maps <c>widget.size</c> in settings.json to <see cref="WidgetSize"/>.</summary>
public static class WidgetSizeNames
{
    public static WidgetSize Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "compact" => WidgetSize.Compact,
        "buttonsonly" or "buttons" => WidgetSize.ButtonsOnly,
        _ => WidgetSize.Full,
    };

    public static string ToSettingName(WidgetSize size) => size switch
    {
        WidgetSize.Compact => "compact",
        WidgetSize.ButtonsOnly => "buttonsOnly",
        _ => "full",
    };
}

public static class WidgetWidths
{
    /// <summary>
    /// Required width per size: the measured widths, with <paramref name="widthOverride"/> (left-edge resize, spec §7.2)
    /// replacing the preferred size's width. The override never goes below the next smaller size's width (otherwise the
    /// size order breaks) and is ignored for ButtonsOnly. Units are whatever the caller uses consistently (DIPs in the app).
    /// </summary>
    public static IReadOnlyDictionary<WidgetSize, double> Resolve(
        IReadOnlyDictionary<WidgetSize, double> measured,
        WidgetSize preferred,
        double? widthOverride)
    {
        var widths = new Dictionary<WidgetSize, double>(measured);
        if (widthOverride is { } width && preferred != WidgetSize.ButtonsOnly)
        {
            var smaller = preferred == WidgetSize.Full ? WidgetSize.Compact : WidgetSize.ButtonsOnly;
            widths[preferred] = Math.Max(width, measured.TryGetValue(smaller, out var min) ? min : 0);
        }
        return widths;
    }
}
