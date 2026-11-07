using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlutterBar.App.Menu;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Sessions;

namespace FlutterBar.App.Widget;

/// <summary>
/// The taskbar widget window (spec §7.1): three layouts (Full / Compact / ButtonsOnly) kept in one grid; the docking
/// picks the size from the widths measured here. Placement, owner and topmost belong to <c>TaskbarDocking</c>.
/// </summary>
internal sealed partial class TaskbarWidget : Window
{
    public const string TaskbarFullText = "Taskbar is full — use Drag widget";

    private readonly WidgetViewModel _vm;
    private readonly DispatcherTimer _blink;
    private readonly Ellipse[] _dots;
    private IReadOnlyDictionary<WidgetSize, double>? _measured;
    private bool _remeasurePending;
    private bool _dragCue;
    private DispatcherTimer? _flash;

    public TaskbarWidget(WidgetViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        _dots = [DotFull, DotCompact, DotButtons];
        _blink = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _blink.Tick += OnBlink;
        Menu = new WidgetContextMenu();
        ContextMenu = Menu;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        Closed += OnClosed;
        ApplySize();
        ApplyDot();
        ApplyFrame();
    }

    /// <summary>The right-click menu (spec §7.4); the app subscribes to its events.</summary>
    public WidgetContextMenu Menu { get; }

    /// <summary>Content changed so the required widths changed: the docking must re-evaluate the placement.</summary>
    public event Action? RequiredWidthsChanged;

    /// <summary>Preferred size from settings (<c>widget.size</c>).</summary>
    public WidgetSize PreferredSize { get; set; } = WidgetSize.Full;

    /// <summary><c>widget.widthOverride</c> in DIPs (left-edge resize), or null.</summary>
    public double? WidthOverride { get; set; }

    /// <summary>Required width per size in DIPs: each layout measured unconstrained, then <see cref="WidthOverride"/> applied.</summary>
    public IReadOnlyDictionary<WidgetSize, double> RequiredWidths()
    {
        _measured ??= MeasureLayouts();
        return WidgetWidths.Resolve(_measured, PreferredSize, WidthOverride);
    }

    /// <summary>
    /// The 40 px widget gains nothing from the GPU pipeline: render THIS window in software (per window, not process-wide,
    /// so flyouts and the LogWindow keep hardware rendering).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target })
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }
    }

    /// <summary>Visual cue for drag mode: 1 px accent border, SizeWE cursor, menu check mark.</summary>
    public void SetDragCue(bool on)
    {
        _dragCue = on;
        Menu.DragChecked = on;
        Cursor = on ? Cursors.SizeWE : null;
        ApplyFrame();
    }

    /// <summary>Second launch / <c>show</c>: one short accent pulse of the frame so the user sees where the widget is.</summary>
    public void Flash()
    {
        _flash?.Stop();
        Frame.SetResourceReference(Border.BorderBrushProperty, "Fb.Accent");
        _flash = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            _flash = null;
            ApplyFrame();
        }, Dispatcher);
    }

    private Dictionary<WidgetSize, double> MeasureLayouts()
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var border = Frame.BorderThickness.Left + Frame.BorderThickness.Right;
        var widths = new Dictionary<WidgetSize, double>();
        foreach (var (size, panel) in new (WidgetSize, FrameworkElement)[] { (WidgetSize.Full, FullPanel), (WidgetSize.Compact, CompactPanel), (WidgetSize.ButtonsOnly, ButtonsPanel) })
        {
            panel.Measure(infinite); // Hidden (not Collapsed) panels still measure
            widths[size] = Math.Ceiling(panel.DesiredSize.Width + border);
        }
        Root.InvalidateMeasure(); // let the normal layout pass measure again with the real width
        return widths;
    }

    private void ScheduleRemeasure()
    {
        if (_remeasurePending)
        {
            return;
        }
        _remeasurePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _remeasurePending = false;
            var widths = MeasureLayouts();
            if (_measured is not null && widths.All(kv => _measured.TryGetValue(kv.Key, out var old) && Math.Abs(old - kv.Value) < 0.5))
            {
                return;
            }
            _measured = widths;
            RequiredWidthsChanged?.Invoke();
        }, DispatcherPriority.Loaded); // after bindings updated the visuals
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WidgetViewModel.EffectiveSize):
                ApplySize();
                break;
            case nameof(WidgetViewModel.TaskbarFull):
                ApplyFrame();
                break;
            case nameof(WidgetViewModel.Controls):
                ApplyDot();
                ScheduleRemeasure(); // button set depends on the state
                break;
            case nameof(WidgetViewModel.ProjectName):
            case nameof(WidgetViewModel.Branch):
            case nameof(WidgetViewModel.BranchVisible):
            case nameof(WidgetViewModel.ConfigName):
            case nameof(WidgetViewModel.DeviceName):
                ScheduleRemeasure();
                break;
        }
    }

    private void ApplySize()
    {
        var size = _vm.EffectiveSize;
        FullPanel.Visibility = size == WidgetSize.Full ? Visibility.Visible : Visibility.Hidden;
        CompactPanel.Visibility = size == WidgetSize.Compact ? Visibility.Visible : Visibility.Hidden;
        ButtonsPanel.Visibility = size == WidgetSize.ButtonsOnly ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyDot()
    {
        var dot = _vm.Controls.Dot;
        var key = dot switch
        {
            StatusDot.Starting => "Fb.Yellow",
            StatusDot.Running => "Fb.Green",
            StatusDot.Error => "Fb.Red",
            _ => "Fb.Grey",
        };
        foreach (var ellipse in _dots)
        {
            ellipse.SetResourceReference(Shape.FillProperty, key);
        }
        if (dot is StatusDot.Starting or StatusDot.Stopping)
        {
            _blink.Start();
        }
        else
        {
            _blink.Stop();
            SetDotOpacity(1);
        }
    }

    private void OnBlink(object? sender, EventArgs e) => SetDotOpacity(DotFull.Opacity < 1 ? 1 : 0.3);

    private void SetDotOpacity(double opacity)
    {
        foreach (var ellipse in _dots)
        {
            ellipse.Opacity = opacity;
        }
    }

    // Drag cue wins over the "taskbar full" hint (warning border + tooltip).
    private void ApplyFrame()
    {
        if (_dragCue)
        {
            Frame.SetResourceReference(Border.BorderBrushProperty, "Fb.Accent");
        }
        else if (_vm.TaskbarFull)
        {
            Frame.SetResourceReference(Border.BorderBrushProperty, "Fb.Warning");
        }
        else
        {
            Frame.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        ToolTip = _vm.TaskbarFull ? TaskbarFullText : null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _blink.Stop();
        _blink.Tick -= OnBlink;
        _flash?.Stop();
        DataContext = null;
    }
}
