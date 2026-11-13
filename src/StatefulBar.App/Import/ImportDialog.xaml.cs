using System.ComponentModel;
using System.IO;
using System.Windows;
using StatefulBar.Core.Config;

namespace StatefulBar.App.Import;

/// <summary>
/// Checklist of <see cref="ImportCandidate"/>s (spec §4.2). Pure view: discovery and saving are done by
/// <see cref="ImportFlow"/> off the UI thread. Rows already in launch.json are unchecked, disabled and labelled "already exists".
/// </summary>
internal sealed partial class ImportDialog : Window
{
    private List<RowView> _rows = [];
    private bool _busy;

    public ImportDialog(string projectRoot)
    {
        ProjectRoot = projectRoot;
        InitializeComponent();
        ProjectText.Text = $"Project: {Path.GetFileName(projectRoot.TrimEnd('\\', '/'))}";
        ProjectText.ToolTip = projectRoot;
        FootText.Text = "Looking for configs in .idea/workspace.xml, .run/*.xml and .vscode/launch.json…";
    }

    public string ProjectRoot { get; }

    /// <summary>"Import" with the checked candidates.</summary>
    public event Action<IReadOnlyList<ImportCandidate>>? ImportRequested;

    public void SetBusy(bool busy)
    {
        _busy = busy;
        Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateImportButton();
    }

    public void SetDiscovery(ImportDiscovery discovery)
    {
        _rows = [.. discovery.Rows.Select(r => new RowView(r))];
        Rows.ItemsSource = _rows;
        SetError(discovery.Error);
        FootText.Text = discovery.Rows.Count == 0
            ? "IDE files are never changed."
            : "Checked configs are added to .statefulbar/launch.json. IDE files aren't changed."
                + (discovery.LaunchJsonExists ? " Comments in the existing launch.json are lost when it is saved." : "");
        UpdateImportButton();
    }

    public void SetError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateImportButton() => ImportButton.IsEnabled = !_busy && _rows.Any(r => r.IsChecked && r.IsEnabled);

    private void OnRowClick(object sender, RoutedEventArgs e) => UpdateImportButton();

    private void OnImportClick(object sender, RoutedEventArgs e) =>
        ImportRequested?.Invoke([.. _rows.Where(r => r.IsChecked && r.IsEnabled).Select(r => r.Row.Candidate)]);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Binding source of one checkbox row; <see cref="IsChecked"/> is written back by the checkbox. Implements a no-op
    /// <see cref="INotifyPropertyChanged"/> (values never change from code) so WPF does not keep rows alive through its
    /// static property-descriptor table, as with the LogWindow rows.
    /// </summary>
    private sealed class RowView(ImportRow row) : INotifyPropertyChanged
    {
        event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
        {
            add { }
            remove { }
        }

        public ImportRow Row { get; } = row;

        public bool IsChecked { get; set; } = row.IsChecked;

        public bool IsEnabled => Row.IsEnabled;

        public string Title => Row.Title;

        public string Detail => Row.Detail;

        public string ToolTip => Row.ToolTip;

        public bool HasNote => Row.Note is not null;

        public string NoteText => Row.Note is null ? "" : Row.IsEnabled ? "⚠ " + Row.Note : Row.Note; // ⚠ only for the checkable warning
    }
}
