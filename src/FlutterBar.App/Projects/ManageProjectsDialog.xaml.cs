using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlutterBar.App.Flyouts;
using FlutterBar.Core.Projects;
using Microsoft.Win32;

namespace FlutterBar.App.Projects;

internal enum ManageProjectsSection
{
    Roots,
    Hidden,
}

/// <summary>
/// "Manage projects" (spec §7.4): scan roots, manual projects, hiding and unhiding, Rescan. Pure view: it raises events
/// with absolute folder paths and the app edits <c>settings.json</c> (normalized paths) and calls <see cref="SetData"/>.
/// </summary>
internal sealed partial class ManageProjectsDialog : Window
{
    public ManageProjectsDialog()
    {
        InitializeComponent();
    }

    public event Action<string>? AddRootRequested;

    public event Action<string>? RemoveRootRequested;

    public event Action<string>? AddManualRequested;

    public event Action<string>? RemoveManualRequested;

    public event Action<string>? HideRequested;

    public event Action<string>? UnhideRequested;

    public event Action? RescanRequested;

    public void SetData(IReadOnlyList<string> roots, IReadOnlyList<string> manual, IReadOnlyList<ProjectInfo> catalog, IReadOnlyList<string> hidden)
    {
        RootsList.ItemsSource = roots.Select(PathItem).ToList();
        ManualList.ItemsSource = manual.Select(PathItem).ToList();
        ProjectsList.ItemsSource = catalog.Select(p => new FlyoutItem
        {
            Title = p.DisplayName,
            Detail = p.Available ? p.Path : p.Path + " (unavailable)",
            ToolTip = p.Path,
            Value = p.Path,
        }).ToList();
        HiddenList.ItemsSource = hidden.Select(PathItem).ToList();
        UpdateButtons();
    }

    public void SetBusy(bool busy, string? status)
    {
        Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.IsEnabled = !busy;
        StatusText.Text = status ?? "";
    }

    /// <summary>Brings a section into view: "Root folders…" and "Hidden projects…" open the same dialog.</summary>
    public void Reveal(ManageProjectsSection section)
    {
        var (header, list) = section == ManageProjectsSection.Hidden ? (HiddenHeader, HiddenList) : (RootsHeader, RootsList);
        Dispatcher.BeginInvoke(() =>
        {
            header.BringIntoView();
            list.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static FlyoutItem PathItem(string path) => new()
    {
        Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path,
        Detail = path,
        ToolTip = path,
        Value = path,
    };

    private static string? Selected(ListBox list) => (list.SelectedItem as FlyoutItem)?.Value as string;

    private void UpdateButtons()
    {
        RemoveRootButton.IsEnabled = Selected(RootsList) is not null;
        RemoveManualButton.IsEnabled = Selected(ManualList) is not null;
        HideButton.IsEnabled = Selected(ProjectsList) is not null;
        UnhideButton.IsEnabled = Selected(HiddenList) is not null;
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Select a root folder (containing Flutter projects)") is { } folder)
        {
            AddRootRequested?.Invoke(folder);
        }
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (Selected(RootsList) is { } path)
        {
            RemoveRootRequested?.Invoke(path);
        }
    }

    private void OnAddManualClick(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Select a Flutter project folder") is { } folder)
        {
            AddManualRequested?.Invoke(folder);
        }
    }

    private void OnRemoveManualClick(object sender, RoutedEventArgs e)
    {
        if (Selected(ManualList) is { } path)
        {
            RemoveManualRequested?.Invoke(path);
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (Selected(ProjectsList) is { } path)
        {
            HideRequested?.Invoke(path);
        }
    }

    private void OnUnhideClick(object sender, RoutedEventArgs e)
    {
        if (Selected(HiddenList) is { } path)
        {
            UnhideRequested?.Invoke(path);
        }
    }

    private void OnRescanClick(object sender, RoutedEventArgs e) => RescanRequested?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
