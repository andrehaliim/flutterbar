using StatefulBar.Core.Docking;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StatefulBar.Core.Projects;

namespace StatefulBar.Core.Settings;

public sealed class AppSettings
{
    private List<string> _scanRoots = [];
    private List<string> _manualProjects = [];
    private List<string> _hiddenProjects = [];
    private Dictionary<string, ProjectState> _projects = new(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings _widget = new();

    public int Version { get; set; } = 1;

    public List<string> ScanRoots
    {
        get => _scanRoots;
        set => _scanRoots = value ?? [];
    }

    public List<string> ManualProjects
    {
        get => _manualProjects;
        set => _manualProjects = value ?? [];
    }

    public List<string> HiddenProjects
    {
        get => _hiddenProjects;
        set => _hiddenProjects = value ?? [];
    }

    /// <summary>
    /// Keyed by normalized project path (case-insensitive). The setter normalizes keys and merges
    /// duplicates (last wins); blank keys and null values are dropped. Prefer
    /// <see cref="TryGetProject"/> / <see cref="GetOrAddProject"/>, which normalize the lookup path.
    /// </summary>
    public Dictionary<string, ProjectState> Projects
    {
        get => _projects;
        set
        {
            var merged = new Dictionary<string, ProjectState>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, state) in value ?? [])
            {
                if (!string.IsNullOrWhiteSpace(key) && state is not null)
                {
                    merged[PathKey.Normalize(key)] = state;
                }
            }
            _projects = merged;
        }
    }

    public bool TryGetProject(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProjectState? state) =>
        _projects.TryGetValue(PathKey.Normalize(path), out state);

    public ProjectState GetOrAddProject(string path)
    {
        var key = PathKey.Normalize(path);
        if (!_projects.TryGetValue(key, out var state))
        {
            state = new ProjectState();
            _projects[key] = state;
        }
        return state;
    }

    public string? CurrentProject { get; set; }

    public WidgetSettings Widget
    {
        get => _widget;
        set => _widget = value ?? new WidgetSettings();
    }

    public bool StartWithWindows { get; set; }
}

public sealed class ProjectState
{
    public DateTimeOffset? LastUsed { get; set; }
    public string? Config { get; set; }
    public string? DeviceId { get; set; }
}

public sealed class WidgetSettings
{
    private int? _offsetFromRight;

    public string Size { get; set; } = "full";

    /// <summary><c>auto</c> or <c>manual</c>; manual mode is in effect only while <see cref="OffsetFromRight"/> has a value.</summary>
    public string Position { get; set; } = "auto";

    /// <summary>
    /// Manual position: distance from the taskbar's right edge to the widget's right edge in physical px (spec §4.3, §7.3).
    /// Negative or absurd values (&gt; <see cref="ManualPlacement.MaxOffset"/>) become null (= auto) on load and on assignment.
    /// </summary>
    [JsonConverter(typeof(LenientOffsetConverter))]
    public int? OffsetFromRight
    {
        get => _offsetFromRight;
        set => _offsetFromRight = ManualPlacement.IsValidOffset(value) ? value : null;
    }

    public const int MinBackgroundOpacity = 0;
    public const int MaxBackgroundOpacity = 100;

    private int _backgroundOpacity = MaxBackgroundOpacity;

    /// <summary>
    /// Opacity of the widget background in percent (spec §7.6); chips, icons and text stay opaque. Clamped to 0–100 on load
    /// and on assignment. 0 is drawn with alpha ≈1.5/255 (<c>WidgetViewModel.BackgroundOpacity</c>): a fully transparent
    /// pixel of the layered window would pass clicks through to the taskbar.
    /// </summary>
    [JsonConverter(typeof(LenientPercentConverter))]
    public int BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => _backgroundOpacity = Math.Clamp(value, MinBackgroundOpacity, MaxBackgroundOpacity);
    }

    public const string ThemeSystem = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    private string _theme = ThemeSystem;

    /// <summary>
    /// Colours of every StatefulBar window (spec §7.6): <c>system</c> follows the taskbar theme, <c>light</c> / <c>dark</c>
    /// override it. Anything else (also a hand-edited non-string) reads as <c>system</c>.
    /// </summary>
    [JsonConverter(typeof(LenientThemeConverter))]
    public string Theme
    {
        get => _theme;
        set => _theme = NormalizeTheme(value);
    }

    internal static string NormalizeTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ThemeLight => ThemeLight,
        ThemeDark => ThemeDark,
        _ => ThemeSystem,
    };

    private string? _monitor;

    /// <summary>
    /// Monitor whose taskbar the widget docks to (spec §7.3): its device name (e.g. <c>\\.\DISPLAY2</c>), or null for the
    /// main taskbar. While that monitor has no taskbar the widget uses the main one and keeps this value. Blank or a
    /// hand-edited non-string reads as null.
    /// </summary>
    [JsonConverter(typeof(LenientOptionalStringConverter))]
    public string? Monitor
    {
        get => _monitor;
        set => _monitor = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Hand-edited optional string: a string is kept (the property trims it), any other token reads as null.</summary>
internal sealed class LenientOptionalStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>Hand-edited theme: a string is normalized by <see cref="WidgetSettings.Theme"/>; any other token reads as <c>system</c>.</summary>
internal sealed class LenientThemeConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return WidgetSettings.NormalizeTheme(reader.GetString());
        }
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
        return WidgetSettings.ThemeSystem;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>Hand-edited percent: read like <see cref="LenientOffsetConverter"/>; anything unreadable reads as fully opaque.</summary>
internal sealed class LenientPercentConverter : JsonConverter<int>
{
    private static readonly LenientOffsetConverter Inner = new();

    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Inner.Read(ref reader, typeof(int?), options) ?? WidgetSettings.MaxBackgroundOpacity;

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>
/// Hand-edited settings must not reset everything: a fractional offset (<c>500.5</c>) is rounded, a numeric string is
/// parsed, and anything else (text, bool, object, out-of-range number) reads as null (= auto position).
/// </summary>
internal sealed class LenientOffsetConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double? value = null;
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                value = reader.GetDouble();
                break;
            case JsonTokenType.String:
                if (double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                }
                break;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                break;
        }
        return value is { } v && double.IsFinite(v) && v >= int.MinValue && v <= int.MaxValue
            ? (int)Math.Round(v, MidpointRounding.AwayFromZero)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
