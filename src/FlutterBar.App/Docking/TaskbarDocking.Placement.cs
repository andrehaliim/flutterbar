using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.App.Docking;

/// <summary>Placement: auto (gaps + debounce), manual (offset from right), drag; auto-hide tracking, the background UIA query, then show/hide and the topmost reassert.</summary>
internal sealed partial class TaskbarDocking
{
    private const double HeightDip = 40;

    private static readonly IReadOnlyDictionary<WidgetSize, double> SpikeWidths = new Dictionary<WidgetSize, double>
    {
        [WidgetSize.Full] = 300,
        [WidgetSize.Compact] = 260,
        [WidgetSize.ButtonsOnly] = 120,
    };

    private readonly PlacementDebouncer _debouncer = new();
    private WidgetSize _preferred = WidgetSize.Full;
    private int? _manualOffset;
    private volatile TaskbarInfo? _layout;
    private TaskbarInfo? _evaluatedLayout;
    private string _evaluatedKey = string.Empty;
    private double? _lastValidX;
    private volatile bool _queryRunning;
    private volatile int _layoutGeneration; // bumped on TaskbarCreated (UI thread); older query results are dropped
    private DateTime _lastQuery = DateTime.MinValue;
    private WidgetSize? _effectiveSize;
    private bool _taskbarFull;
    private Box? _area;
    private bool? _hiddenRaised;
    private string _lastLayoutLog = string.Empty;

    private double Scale()
    {
        var dpi = _hwnd == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    /// <summary>Required widths in physical px for the current DPI.</summary>
    private Dictionary<WidgetSize, double> PhysicalWidths(double scale)
    {
        var dips = RequiredWidths?.Invoke() ?? SpikeWidths;
        var widths = new Dictionary<WidgetSize, double>();
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            widths[size] = Math.Round((dips.TryGetValue(size, out var w) ? w : SpikeWidths[size]) * scale);
        }
        return widths;
    }

    /// <summary>
    /// Sizes the Size menu may offer: in auto mode with a known layout, those <see cref="PlacementPlanner.Fits"/> the current
    /// gaps (ButtonsOnly always, the widget is never hidden for lack of space). Manual/drag mode does not avoid the app
    /// buttons, and before the first layout nothing is known: every size then.
    /// </summary>
    public IReadOnlySet<WidgetSize> SizesThatFit()
    {
        var all = Enum.GetValues<WidgetSize>().ToHashSet();
        if (_dragMode || _manualOffset is not null || _layout is not { } layout)
        {
            return all;
        }
        var widths = PhysicalWidths(Scale());
        var gaps = TaskbarGeometry.FreeGaps(layout.Taskbar, layout.Tray.Left, layout.AppButtons, PlacementPlanner.Inset);
        all.RemoveWhere(size => size != WidgetSize.ButtonsOnly && !PlacementPlanner.Fits(gaps, size, widths));
        return all;
    }

    private void Place(IntPtr tray, Box taskbar, bool wantVisible)
    {
        var scale = Scale();
        var widths = PhysicalWidths(scale);
        var taskbarHeight = taskbar.Bottom - taskbar.Top;
        var h = Math.Round(Math.Min(HeightDip * scale, taskbarHeight));
        var y = taskbar.Top + Math.Round((taskbarHeight - h) / 2); // live taskbar rect: follows an auto-hiding bar

        WidgetSize size;
        double x;
        var noFit = false;
        Box? area = null;
        string mode;
        if (_dragMode)
        {
            mode = "drag";
            size = _effectiveSize ?? _preferred;
            x = ManualPlacement.ClampX(taskbar, _dragX ?? NativeMethods.WindowBox(_hwnd)?.Left ?? taskbar.Left, widths[size]);
        }
        else if (_manualOffset is { } offset)
        {
            mode = "manual";
            size = _preferred;
            x = ManualPlacement.XFromOffset(taskbar, offset, widths[size]); // no auto-avoidance in manual mode
        }
        else
        {
            mode = "auto";
            var layout = _layout;
            if (layout is not null && !_autoHidden)
            {
                var key = string.Join(",", widths.Values) + "|" + _preferred;
                if (!ReferenceEquals(layout, _evaluatedLayout) || key != _evaluatedKey)
                {
                    if (key != _evaluatedKey && _evaluatedKey.Length > 0)
                    {
                        _debouncer.ArmImmediate(); // content/size/DPI change: apply at once
                    }
                    _evaluatedLayout = layout;
                    _evaluatedKey = key;
                    Evaluate(layout, widths);
                }
            }
            if (_debouncer.Applied is { } applied)
            {
                size = applied.Size;
                noFit = applied.NoFit;
                area = applied.Gap;
                x = ManualPlacement.ClampX(taskbar, applied.X, widths[size]);
            }
            else
            {
                // no valid UIA layout yet: dock left of TrayNotifyWnd until one arrives (a secondary taskbar has none: wait
                // briefly for the first layout, then fall back to its right edge)
                size = _preferred;
                x = PlacementPlanner.FallbackX(taskbar, NativeMethods.TrayNotifyLeft(tray, taskbar), widths[size]);
                if (WaitingForSecondaryLayout())
                {
                    mode = "auto (waiting for the secondary taskbar layout)";
                    wantVisible = false;
                }
            }
        }
        var w = widths[size];

        RaiseLayoutEvents(size, noFit, area);
        ApplyWindow(wantVisible, (int)Math.Round(x), (int)y, (int)w, (int)h);

        var layoutLog = $"mode={mode} size={size}{(noFit ? " (taskbar full)" : string.Empty)} win=({x:F0},{y:F0},{x + w:F0},{y + h:F0}) visible={wantVisible} scale={scale:F2} offset={_manualOffset?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
        if (layoutLog != _lastLayoutLog)
        {
            _lastLayoutLog = layoutLog;
            AppLog.Write($"layout inst={_id}: {layoutLog}");
        }
    }

    /// <summary>Called once per new UIA layout (or when the widths change). Debounced per v3.1.</summary>
    private void Evaluate(TaskbarInfo layout, IReadOnlyDictionary<WidgetSize, double> widths)
    {
        var gaps = TaskbarGeometry.FreeGaps(layout.Taskbar, layout.Tray.Left, layout.AppButtons, PlacementPlanner.Inset);
        var candidate = PlacementPlanner.Choose(gaps, _preferred, widths, layout.Taskbar, _lastValidX, layout.TrayNotifyLeft);
        var result = _debouncer.Offer(candidate);
        var gapText = string.Join(" ", gaps.Select(g => $"[{g.Left:F0}..{g.Right:F0}]={g.Right - g.Left:F0}"));
        var desc = $"{candidate.Size} x={candidate.X:F0}{(candidate.NoFit ? " NOFIT" : string.Empty)}";
        if (result.Applied)
        {
            if (!candidate.NoFit)
            {
                _lastValidX = candidate.X;
            }
            AppLog.Write($"placement APPLIED {desc} inst={_id} [{result.Action}] gaps: {gapText} trayLeft={layout.Tray.Left:F0}");
        }
        else if (result.Action == DebounceAction.HeldBack && _debouncer.Applied is { } applied)
        {
            AppLog.Write($"placement candidate {desc} held back ({result.HeldCount}/{PlacementDebouncer.MaxHold}, applied {applied.Size} x={applied.X:F0}); needs 2 consecutive queries; gaps: {gapText}");
        }
    }

    private void RaiseLayoutEvents(WidgetSize size, bool noFit, Box? area)
    {
        if (noFit != _taskbarFull)
        {
            AppLog.Write(noFit
                ? $"taskbar full inst={_id}: nothing fits even ButtonsOnly -> widget STAYS VISIBLE (ButtonsOnly, last X) with hint"
                : $"taskbar full inst={_id}: fits again");
        }
        if (size != _effectiveSize || noFit != _taskbarFull)
        {
            _effectiveSize = size;
            _taskbarFull = noFit;
            EffectiveSizeChanged?.Invoke(size, noFit);
        }
        if (area != _area)
        {
            _area = area;
            AreaChanged?.Invoke(area);
        }
    }

    /// <summary>
    /// Hide/show from the ACTUAL window state (IsWindowVisible; WPF may show the window behind our back), move/size only
    /// when the target changed, and reassert HWND_TOPMOST every time unless a popup is open (then SWP_NOZORDER).
    /// </summary>
    private void ApplyWindow(bool wantVisible, int x, int y, int w, int h)
    {
        var visible = NativeMethods.IsWindowVisible(_hwnd);
        if (!wantVisible)
        {
            if (visible)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }
        }
        else
        {
            if (!visible)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
            var move = _gesture != Gesture.Move && NativeMethods.WindowBox(_hwnd) != new Box(x, y, x + w, y + h);
            var flags = NativeMethods.SWP_NOACTIVATE | (move ? 0 : NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            if (TopmostSuspended)
            {
                flags |= NativeMethods.SWP_NOZORDER; // a menu/tooltip/flyout is open: do not raise the widget over it
            }
            if (!NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, flags))
            {
                AppLog.Limited("swp-fail", $"SetWindowPos failed inst={_id} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}", 5);
            }
        }
        if (_hiddenRaised != !wantVisible)
        {
            _hiddenRaised = !wantVisible;
            HiddenChanged?.Invoke(!wantVisible);
        }
    }

    /// <summary>v3.2: with auto-hide on, hide while less than 50% of the live taskbar rect is on its monitor (<see cref="AutoHideMonitor"/>).</summary>
    private void UpdateAutoHide(Box taskbar, string reason)
    {
        var hidden = false;
        if (_autoHide)
        {
            if (AutoHideMonitor() is { } monitor)
            {
                hidden = AutoHideVisibility.IsHidden(taskbar, monitor);
            }
            else
            {
                AppLog.Limited("ab-monitor", $"GetMonitorInfo(taskbar monitor) failed, auto-hide tracking skipped, err={Marshal.GetLastWin32Error()}", 10);
            }
        }
        if (hidden != _autoHidden)
        {
            _autoHidden = hidden;
            AppLog.Write($"auto-hide: taskbar {(hidden ? "OFF-SCREEN -> hide widget, UIA re-layout paused" : "VISIBLE -> show widget, follow taskbar rect")} inst={_id} tbRect=({taskbar.Left},{taskbar.Top},{taskbar.Right},{taskbar.Bottom}) [{reason}]");
        }
    }

    /// <summary>Background UIA query, at most one at a time and once per second; the result triggers a reassert.</summary>
    private void KickQuery()
    {
        if (_queryRunning || DateTime.UtcNow - _lastQuery < TimeSpan.FromSeconds(1))
        {
            return;
        }
        _queryRunning = true;
        _lastQuery = DateTime.UtcNow;
        var dispatcher = _window.Dispatcher;
        var generation = _layoutGeneration;
        var tray = _tray; // read on the UI thread: the target may change while the query runs (then the generation does too)
        var secondary = _onSecondary;
        var monitor = AutoHideMonitor();
        Task.Run(() =>
        {
            try
            {
                var info = _probe.Read(tray, secondary, monitor);
                if (info is null || _dead || _autoHidden || generation != _layoutGeneration)
                {
                    return; // dead instance, hidden bar, or started before TaskbarCreated: drop silently
                }
                dispatcher.BeginInvoke(() =>
                {
                    // re-check on the UI thread, where _dead and _layoutGeneration change
                    if (!_dead && generation == _layoutGeneration)
                    {
                        _layout = info;
                        Reassert("layout");
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_dead)
                {
                    AppLog.Limited("uia-fail", $"UIA query failed inst={_id} (keeping last good layout): {ex}", 5);
                }
            }
            finally
            {
                _queryRunning = false;
            }
        });
    }
}
