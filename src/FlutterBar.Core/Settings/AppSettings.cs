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

    /// <summary>Keyed by project path; always case-insensitive, also after JSON deserialization.</summary>
    public Dictionary<string, ProjectState> Projects
    {
        get => _projects;
        set => _projects = new Dictionary<string, ProjectState>(
            value ?? [], StringComparer.OrdinalIgnoreCase);
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
    public string Size { get; set; } = "full";
    public string Position { get; set; } = "auto";
    public double? LockedX { get; set; }
    public double? WidthOverride { get; set; }
}
