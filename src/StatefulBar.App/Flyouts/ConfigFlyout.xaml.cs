using System.Windows;
using System.Windows.Controls;
using StatefulBar.Core.Config;
using StatefulBar.Core.Orchestration;

namespace StatefulBar.App.Flyouts;

internal sealed partial class ConfigFlyout : UserControl
{
    private readonly FlyoutList _list;

    public ConfigFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, null);
        _list.Chosen += item =>
        {
            if (item.Value is string name)
            {
                ConfigChosen?.Invoke(name);
            }
        };
    }

    public event Action<string>? ConfigChosen;

    /// <summary>"Import from IDE": same flow as the menu item (always available).</summary>
    public event Action? ImportRequested;

    public event Action? CreateDefaultRequested;

    /// <summary>"Edit file": opens launch.json in a text editor.</summary>
    public event Action? EditFileRequested;

    /// <summary>The list when it has rows, else the first action button.</summary>
    public IInputElement FocusTarget => List.HasItems ? List : MissingPanel.Visibility == Visibility.Visible ? DefaultButton : ImportButton;

    /// <summary>Warning line at the top (why the flyout was opened for the user), or null to hide it.</summary>
    public void SetNotice(string? text)
    {
        Notice.Text = text ?? "";
        Notice.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Empties every section, so data of a previous project never shows while launch.json is re-read.</summary>
    public void Reset()
    {
        MissingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        Errors.ItemsSource = null;
        EditButton.Visibility = Visibility.Collapsed;
        ImportButton.Visibility = Visibility.Visible;
        _list.SetItems([]);
        List.Visibility = Visibility.Collapsed;
        SetMessage(null);
        SetNotice(null);
    }

    public void SetBusy(bool busy) => Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Renders a fresh <see cref="LaunchConfigStore.Load"/> result.</summary>
    public void SetResult(LaunchConfigLoadResult result, ConfigChipState chip)
    {
        MissingPanel.Visibility = result.FileExists ? Visibility.Collapsed : Visibility.Visible;
        ErrorPanel.Visibility = chip.ErrorLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Errors.ItemsSource = chip.ErrorLines;
        EditButton.Visibility = result.FileExists ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = result.FileExists ? Visibility.Visible : Visibility.Collapsed; // MissingImportButton takes over
        _list.SetItems([.. result.Configs.Select(c => new FlyoutItem
        {
            Title = c.Name,
            Detail = ConfigPresentation.Detail(c),
            ToolTip = ConfigPresentation.ToolTip(c),
            IsCurrent = chip.Selected?.Name == c.Name,
            Value = c.Name,
        })]);
        List.Visibility = result.Configs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetMessage(chip.IsError && chip.ErrorLines.Count == 0 ? chip.ToolTip : null);
    }

    public void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnImportClick(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    private void OnCreateDefaultClick(object sender, RoutedEventArgs e) => CreateDefaultRequested?.Invoke();

    private void OnEditClick(object sender, RoutedEventArgs e) => EditFileRequested?.Invoke();
}
