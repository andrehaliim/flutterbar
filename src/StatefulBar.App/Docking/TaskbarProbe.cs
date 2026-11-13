using System.Runtime.InteropServices;
using System.Windows.Automation;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;
using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;

namespace StatefulBar.App.Docking;

/// <summary>
/// One read of the target taskbar (main or secondary, spec §7.3). <paramref name="AppButtons"/> holds every OCCUPIED box left of the tray (Start,
/// Search, Widgets, Task View, app buttons ...), sorted left to right; <paramref name="Tray"/> spans from the tray's left
/// edge to the taskbar's right edge. All rects are physical px. <paramref name="Visible"/> is false while auto-hide has
/// moved the bar off screen. <paramref name="TrayNotifyLeft"/> is the Win32 TrayNotifyWnd left edge, when found.
/// </summary>
internal sealed record TaskbarInfo(
    Box Taskbar,
    Box Tray,
    IReadOnlyList<Box> AppButtons,
    bool AutoHide,
    bool Visible,
    double Dpi,
    double? TrayNotifyLeft);

/// <summary>
/// Reads the Windows 11 taskbar through UI Automation (class names / AutomationIds from the findings doc) and applies the
/// Core rules (<see cref="TaskbarLayoutAnalyzer"/>). Call from a background (MTA) thread only, never the UI thread: UIA
/// calls are cross-process and can stall. The root element is cached per taskbar handle and the whole subtree
/// is fetched with ONE cache request per read (five properties, no live element references), instead of one cross-process
/// call per element and property. Elements of our own process (the owned widget shows up in the tree) are skipped.
/// </summary>
internal sealed class TaskbarProbe
{
    private const int MaxDepth = 6; // as validated by the spike
    private readonly object _gate = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly CacheRequest _request;
    private readonly HashSet<string> _loggedVerdicts = [];
    private IntPtr _rootHandle;
    private AutomationElement? _root;

    private readonly Func<bool> _isCancelled;

    /// <param name="isCancelled">True once the owning docking instance is dead: no more logging or results.</param>
    public TaskbarProbe(Func<bool> isCancelled)
    {
        _isCancelled = isCancelled;
        _request = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = new NotCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _ownPid)),
            AutomationElementMode = AutomationElementMode.None,
        };
        _request.Add(AutomationElement.ClassNameProperty);
        _request.Add(AutomationElement.AutomationIdProperty);
        _request.Add(AutomationElement.ControlTypeProperty);
        _request.Add(AutomationElement.BoundingRectangleProperty);
        _request.Add(AutomationElement.ProcessIdProperty);
    }

    /// <summary>
    /// Reads taskbar <paramref name="tray"/> (<c>Shell_TrayWnd</c> or a <c>Shell_SecondaryTrayWnd</c>). <paramref name="secondary"/>:
    /// no notification area, so a missing clock is fine (<see cref="TaskbarLayoutAnalyzer.Analyze"/>). <paramref name="monitor"/>
    /// is the rect auto-hide visibility is measured against. Null when the taskbar is gone, UIA failed, or the result failed
    /// the sanity check (caller keeps the last good layout).
    /// </summary>
    public TaskbarInfo? Read(IntPtr tray, bool secondary, Box? monitor)
    {
        lock (_gate)
        {
            if (tray == IntPtr.Zero || NativeMethods.WindowBox(tray) is not { } taskbar)
            {
                AppLog.Limited("probe-notray", "UIA probe: taskbar window gone", 5);
                return null;
            }

            var elements = new List<TaskbarElement>();
            var pids = new List<int>();
            try
            {
                if (_root is null || _rootHandle != tray)
                {
                    _root = AutomationElement.FromHandle(tray);
                    _rootHandle = tray;
                }
                var snapshot = _root.GetUpdatedCache(_request);
                Collect(snapshot, 0, elements, pids);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
            {
                _root = null; // re-resolve next time (explorer restarted, element gone)
                if (_isCancelled())
                {
                    return null;
                }
                AppLog.Limited("probe-fail", $"UIA query failed (keeping last good layout): {ex.GetType().Name}: {ex.Message}", 5);
                return null;
            }

            if (_isCancelled())
            {
                return null; // the instance died while UIA was running: no logs, no result
            }
            var trayNotifyLeft = NativeMethods.TrayNotifyLeft(tray, taskbar);
            var analysis = TaskbarLayoutAnalyzer.Analyze(taskbar, elements, trayNotifyLeft, trayOptional: secondary);
            LogNewVerdicts(analysis, pids);
            if (analysis.TrayFromTrayNotifyWnd)
            {
                AppLog.Limited("tray-mismatch", $"tray-left mismatch > {TaskbarLayoutAnalyzer.TrayMismatchLimit} px: using TrayNotifyWnd left={trayNotifyLeft}", 5);
            }
            if (!analysis.IsValid)
            {
                AppLog.Limited(
                    "layout-invalid",
                    $"UIA layout REJECTED ({analysis.RejectReason}, occupied={analysis.Occupied.Count}, elements={elements.Count}, TrayNotifyWnd.left={trayNotifyLeft}) -> keeping last good layout",
                    5);
                return null;
            }

            var autoHide = NativeMethods.IsAutoHide();
            var visible = !(autoHide && monitor is { } m && AutoHideVisibility.IsHidden(taskbar, m));
            return new TaskbarInfo(
                taskbar,
                new Box(analysis.TrayLeft, taskbar.Top, taskbar.Right, taskbar.Bottom),
                analysis.Occupied,
                autoHide,
                visible,
                NativeMethods.GetDpiForWindow(tray),
                trayNotifyLeft);
        }
    }

    private void Collect(AutomationElement element, int depth, List<TaskbarElement> elements, List<int> pids)
    {
        var cached = element.Cached;
        if (cached.ProcessId == _ownPid)
        {
            return; // our widget window and its subtree
        }
        var rect = cached.BoundingRectangle;
        var box = rect.IsEmpty ? default : new Box(rect.Left, rect.Top, rect.Right, rect.Bottom);
        elements.Add(new TaskbarElement(
            cached.ClassName ?? string.Empty,
            cached.AutomationId ?? string.Empty,
            cached.ControlType?.ProgrammaticName ?? string.Empty,
            ProcessNames.Of((uint)cached.ProcessId) ?? "?",
            box));
        pids.Add(cached.ProcessId);
        if (depth >= MaxDepth)
        {
            return;
        }
        foreach (AutomationElement child in element.CachedChildren)
        {
            Collect(child, depth + 1, elements, pids);
        }
    }

    /// <summary>Each new (class, id, controlType, pid) is logged once with its verdict and reason (tray elements are not logged).</summary>
    private void LogNewVerdicts(TaskbarAnalysis analysis, List<int> pids)
    {
        if (_loggedVerdicts.Count > 2048)
        {
            _loggedVerdicts.Clear();
        }
        for (var i = 0; i < analysis.Verdicts.Count; i++)
        {
            var (e, verdict) = analysis.Verdicts[i];
            if (verdict.Role == ElementRole.Tray || !_loggedVerdicts.Add($"{e.ClassName}|{e.AutomationId}|{e.ControlType}|{pids[i]}"))
            {
                continue;
            }
            var label = verdict.Role == ElementRole.Occupied ? "COUNTED" : $"IGNORED ({verdict.Reason})";
            var r = e.Rect;
            AppLog.Write($"UIA element {label}: class='{e.ClassName}' id='{e.AutomationId}' type={e.ControlType} pid={pids[i]}({e.ProcessName}) rect=({r.Left:F0},{r.Top:F0},{r.Right:F0},{r.Bottom:F0})");
        }
    }
}
