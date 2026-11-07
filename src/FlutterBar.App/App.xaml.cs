using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Docking;
using FlutterBar.App.Theme;
using FlutterBar.App.Widget;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.App;

/// <summary>
/// Composition root (Task 13 scope): settings, theme, run session, the widget window and its docking. Task 14 adds the
/// AppController (flyouts, Run orchestration), Task 15 the LogWindow, Task 16 the full menu and single instance.
/// </summary>
public partial class App : Application
{
    private readonly AppSettingsStore _store = new(AppSettingsStore.DefaultPath);
    private AppSettings _settings = new();
    private TaskbarTheme? _theme;
    private RunSession? _session;
    private WidgetViewModel? _viewModel;
    private TaskbarWidget? _widget;
    private TaskbarDocking? _docking;
    private Task _saveChain = Task.CompletedTask;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write("unhandled exception (terminating)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Write("unobserved task exception", args.Exception);
        // A 40 px widget gains nothing from the GPU pipeline; software rendering avoids loading the D3D/driver stack (working set, spec §1).
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        AppLog.Write($"FlutterBar.App start pid={Environment.ProcessId} 64bit={Environment.Is64BitProcess} os={Environment.OSVersion.Version}");

        _settings = LoadSettings(); // before any window exists, so the synchronous read cannot stall the taskbar
        _theme = new TaskbarTheme(Resources, Dispatcher);
        _session = new RunSession(new ProcessLauncher(), new LogBuffer());
        _viewModel = new WidgetViewModel(_session, Dispatcher);
        InitializeLabels(_viewModel, _settings);
        _viewModel.RunRequested += () => AppLog.Write("Run requested from widget (orchestration arrives with Task 14)");
        _viewModel.OpenLogRequested += () => AppLog.Write("Open log requested from widget (LogWindow arrives with Task 15)");
        _viewModel.ChipRequested += chip => AppLog.Write($"{chip} chip clicked (flyouts arrive with Task 14)");
        CreateWidget("startup");
    }

    private AppSettings LoadSettings()
    {
        try
        {
            var settings = _store.Load(out var warning);
            if (warning is not null)
            {
                AppLog.Write("settings: " + warning); // toast arrives with Task 14
            }
            AppLog.Write($"settings: widget size={settings.Widget.Size} position={settings.Widget.Position} offsetFromRight={settings.Widget.OffsetFromRight?.ToString() ?? "-"} widthOverride={settings.Widget.WidthOverride?.ToString() ?? "-"}");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("settings: load failed, using defaults", ex);
            return new AppSettings();
        }
    }

    private static void InitializeLabels(WidgetViewModel vm, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentProject))
        {
            return;
        }
        vm.ProjectName = Path.GetFileName(settings.CurrentProject.TrimEnd('\\', '/'));
        if (settings.TryGetProject(settings.CurrentProject, out var state))
        {
            vm.ConfigName = state.Config ?? vm.ConfigName;
            vm.DeviceName = state.DeviceId ?? vm.DeviceName;
        }
    }

    /// <summary>Creates a widget window plus its docking; also used when the docking asks for a new window.</summary>
    private void CreateWidget(string why)
    {
        var vm = _viewModel ?? throw new InvalidOperationException("view model missing");
        var widget = new TaskbarWidget(vm)
        {
            PreferredSize = WidgetSizeNames.Parse(_settings.Widget.Size),
            WidthOverride = _settings.Widget.WidthOverride,
        };
        var docking = new TaskbarDocking(widget)
        {
            PreferredSize = widget.PreferredSize,
            RequiredWidths = widget.RequiredWidths,
        };

        widget.DragModeToggled += on => docking.DragMode = on;
        widget.ResetPositionRequested += () => ResetPosition(docking);
        widget.ExitRequested += () => _ = ExitAsync();
        widget.RequiredWidthsChanged += docking.InvalidateLayout;
        docking.DragModeChanged += widget.SetDragCue;
        docking.EffectiveSizeChanged += (size, full) =>
        {
            vm.EffectiveSize = size;
            vm.TaskbarFull = full;
        };
        docking.HiddenChanged += hidden => vm.IsHidden = hidden;
        docking.ManualOffsetChanged += SaveManualOffset;
        docking.WidthOverrideChanged += width =>
        {
            _settings.Widget.WidthOverride = width;
            widget.WidthOverride = _settings.Widget.WidthOverride; // validated value
            docking.InvalidateLayout();
            SaveSettings($"widthOverride={width}");
        };
        docking.RecreateRequested += reason => Dispatcher.BeginInvoke(() => Recreate(widget, reason));

        if (_settings.Widget.OffsetFromRight is { } offset)
        {
            docking.SetManual(offset);
        }
        docking.Start();
        widget.Show();
        _widget = widget;
        _docking = docking;
        AppLog.Write($"widget shown ({why}) inst={docking.InstanceId} preferred={widget.PreferredSize} manual={docking.IsManual}");
    }

    /// <summary>The old docking is already torn down; show the new window first, then close the old one.</summary>
    private void Recreate(TaskbarWidget old, string reason)
    {
        if (_exiting)
        {
            return;
        }
        var oldHandle = new System.Windows.Interop.WindowInteropHelper(old).Handle;
        CreateWidget("recreate: " + reason);
        if (oldHandle != IntPtr.Zero && Native.NativeMethods.IsWindow(oldHandle))
        {
            old.Close(); // its docking is already dead, so this does not trigger another recreate
        }
    }

    private void ResetPosition(TaskbarDocking docking)
    {
        docking.ResetToAuto();
        _settings.Widget.OffsetFromRight = null;
        _settings.Widget.Position = "auto";
        SaveSettings("reset to auto position");
    }

    private void SaveManualOffset(int offset)
    {
        _settings.Widget.OffsetFromRight = offset;
        _settings.Widget.Position = "manual";
        SaveSettings($"offsetFromRight={offset}");
    }

    /// <summary>
    /// Snapshot on the UI thread (in-memory JSON round trip), atomic write on a background task, writes serialized.
    /// The UI thread never waits on file I/O (its input queue is attached to the taskbar's).
    /// </summary>
    private void SaveSettings(string what)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings)) ?? new AppSettings();
        _saveChain = _saveChain.ContinueWith(_ =>
        {
            try
            {
                _store.Save(copy);
                AppLog.Write($"settings saved ({what})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"settings save failed ({what})", ex);
            }
        }, TaskScheduler.Default);
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        AppLog.Write("exit requested from widget menu");
        if (_session is { } session)
        {
            if (session.Snapshot.State is not (SessionState.Idle or SessionState.Failed))
            {
                await session.StopAsync(); // exit confirmation dialog arrives with Task 16
            }
            await session.DisposeAsync();
        }
        _docking?.Stop();
        _widget?.Close();
        _viewModel?.Dispose();
        _theme?.Dispose();
        await _saveChain;
        AppLog.Write("FlutterBar.App exit");
        AppLog.Flush(TimeSpan.FromSeconds(1)); // window is gone: nothing left to stall
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("unhandled UI exception (kept running)", e.Exception);
        e.Handled = true;
    }
}
