using System.Windows;
using System.Windows.Controls;
using StatefulBar.Core.Orchestration;
using StatefulBar.Core.Sessions;

namespace StatefulBar.App.Flyouts;

/// <summary>What the Flutter flyout shows: session state (what may run), the running command, build_runner.</summary>
internal sealed record FlutterFlyoutState(SessionState Session, FlutterCommand? Running, bool UsesBuildRunner);

internal sealed partial class FlutterFlyout : UserControl
{
    private readonly FlyoutList _list;

    public FlutterFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, null);
        _list.Chosen += item =>
        {
            if (item.Value is FlutterCommand command)
            {
                CommandChosen?.Invoke(command);
            }
        };
        CancelButton.Click += (_, _) => CancelRequested?.Invoke();
    }

    public event Action<FlutterCommand>? CommandChosen;

    public event Action? CancelRequested;

    public IInputElement FocusTarget => List;

    public void SetState(FlutterFlyoutState state)
    {
        var running = state.Running is not null;
        Spinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunningRow.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunningText.Text = state.Running is { } command ? $"Running {FlutterCommands.Display(command)}…" : string.Empty;

        var items = new List<FlutterCommand> { FlutterCommand.PubGet, FlutterCommand.Clean, FlutterCommand.CleanAndPubGet };
        if (state.UsesBuildRunner)
        {
            items.Add(FlutterCommand.BuildRunner);
        }
        items.Add(FlutterCommand.Doctor);

        _list.SetItems(items.Select(command => Row(command, state)).ToList());
    }

    private static FlyoutItem Row(FlutterCommand command, FlutterFlyoutState state)
    {
        var blocked = state.Running is not null ? "Another command is running" : FlutterCommands.Blocked(command, state.Session);
        return new FlyoutItem
        {
            Title = FlutterCommands.Title(command),
            Detail = blocked ?? Detail(command),
            ToolTip = blocked ?? FlutterCommands.Display(command),
            IsEnabled = blocked is null,
            Value = command,
        };
    }

    private static string Detail(FlutterCommand command) => command switch
    {
        FlutterCommand.BuildRunner => "build --delete-conflicting-outputs",
        FlutterCommand.Doctor => "flutter doctor (opens the log)",
        _ => FlutterCommands.Display(command),
    };
}
