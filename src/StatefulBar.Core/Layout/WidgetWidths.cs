namespace StatefulBar.Core.Layout;

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
