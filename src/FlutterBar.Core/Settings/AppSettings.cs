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
    public string Size { get; set; } = "full";
    public string Position { get; set; } = "auto";
    public double? LockedX { get; set; }
    public double? WidthOverride { get; set; }
}
