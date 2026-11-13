using System.Windows;
using System.Windows.Controls;
using StatefulBar.Core.Projects;

namespace StatefulBar.App.Flyouts;

internal sealed partial class ProjectFlyout : UserControl
{
    private readonly FlyoutList _list;

    public ProjectFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, Search);
        _list.Chosen += item =>
        {
            if (item.Value is string path)
            {
                ProjectChosen?.Invoke(path);
            }
        };
    }

    public event Action<string>? ProjectChosen;

    /// <summary>"Add project…": pick a folder and add it to <c>manualProjects</c>.</summary>
    public event Action? AddRequested;

    /// <summary>"Add root folder…": pick a folder, add it to <c>scanRoots</c> and scan it.</summary>
    public event Action? AddRootRequested;

    public IInputElement FocusTarget => _list.Focus;

    public void Reset() => _list.ClearSearch();

    /// <summary>Warning line at the top (why the flyout was opened for the user), or null to hide it.</summary>
    public void SetNotice(string? text)
    {
        Notice.Text = text ?? "";
        Notice.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetBusy(bool busy) => Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    public void SetProjects(IReadOnlyList<ProjectInfo> projects, string? current, bool scanning)
    {
        _list.SetItems([.. projects.Select(p => new FlyoutItem
        {
            Title = p.DisplayName,
            Detail = p.Available ? p.Path : p.Path + " (unavailable)",
            ToolTip = p.Path,
            IsEnabled = p.Available,
            IsCurrent = current is not null && string.Equals(p.Path, current, StringComparison.OrdinalIgnoreCase),
            Value = p.Path,
        })]);
        Empty.Text = scanning ? "Scanning for projects…" : "No projects yet. Add a project or a root folder.";
        Empty.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    private void OnAddRootClick(object sender, RoutedEventArgs e) => AddRootRequested?.Invoke();
}
