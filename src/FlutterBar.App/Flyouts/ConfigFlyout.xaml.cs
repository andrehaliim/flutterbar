using System.Windows;
using System.Windows.Controls;
using FlutterBar.Core.Config;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App.Flyouts;

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

    /// <summary>"Import dari IDE" (the import dialog belongs to Task 16).</summary>
    public event Action? ImportRequested;

    public event Action? CreateDefaultRequested;

    public event Action? OpenFileRequested;

    /// <summary>The list when it has rows, else the first action button.</summary>
    public IInputElement FocusTarget => List.HasItems ? List : MissingPanel.IsVisible ? ImportButton : OpenButton;

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
        FileActions.Visibility = Visibility.Collapsed;
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
        FileActions.Visibility = result.FileExists ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();
}
