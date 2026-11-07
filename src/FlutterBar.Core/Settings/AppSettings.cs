using FlutterBar.Core.Docking;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlutterBar.Core.Projects;

namespace FlutterBar.Core.Settings;

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
    /// <summary>Upper bound for <see cref="WidthOverride"/> in DIPs; larger values are treated as corrupt.</summary>
    public const double MaxWidthOverride = 4096;

    private int? _offsetFromRight;
    private double? _widthOverride;

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

    /// <summary>Width of the preferred size after a left-edge resize, in DIPs; values outside (0, <see cref="MaxWidthOverride"/>] become null.</summary>
    public double? WidthOverride
    {
        get => _widthOverride;
        set => _widthOverride = value is > 0 and <= MaxWidthOverride ? value : null;
    }
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
