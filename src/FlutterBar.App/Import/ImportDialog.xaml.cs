using System.ComponentModel;
using System.IO;
using System.Windows;
using FlutterBar.Core.Config;

namespace FlutterBar.App.Import;

/// <summary>
/// Checklist of <see cref="ImportCandidate"/>s (spec §4.2). Pure view: discovery and saving are done by
/// <see cref="ImportFlow"/> off the UI thread. Rows already in launch.json are unchecked, disabled and labelled "sudah ada".
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
        FootText.Text = "Mencari config di .idea/workspace.xml, .run/*.xml, dan .vscode/launch.json…";
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
            ? "File IDE tidak pernah diubah."
            : "Config yang dicentang ditambahkan ke .flutterbar/launch.json. File IDE tidak diubah."
                + (discovery.LaunchJsonExists ? " Komentar di launch.json yang sudah ada akan hilang saat disimpan." : "");
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

        public string NoteText => Row.Note is null ? "" : $"({Row.Note})";
    }
}
