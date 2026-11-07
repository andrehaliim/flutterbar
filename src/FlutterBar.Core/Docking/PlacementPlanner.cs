using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Docking;

/// <summary>
/// Where the widget goes in auto mode. <paramref name="X"/> is the left edge in physical px. <paramref name="NoFit"/> means
/// not even ButtonsOnly fits: the widget stays visible (ButtonsOnly at the last valid X) and shows the "taskbar full" hint.
/// <paramref name="Gap"/> is the free strip used, or null when nothing fits.
/// </summary>
public sealed record Placement(WidgetSize Size, double X, bool NoFit, Box? Gap);

public static class PlacementPlanner
{
    public const double Inset = 8;

    /// <summary>
    /// v3.1 rule: try sizes from <paramref name="preferred"/> down (Full -> Compact -> ButtonsOnly); the first size that fits
    /// any gap wins and docks at the right end of the rightmost gap that fits it. Nothing fits (<see cref="WidgetLayoutPolicy.Choose"/>
    /// returns null): ButtonsOnly at <paramref name="lastValidX"/>, else left of TrayNotifyWnd, else the taskbar's right edge,
    /// clamped to the taskbar. The widget is never hidden for lack of space.
    /// </summary>
    /// <param name="widths">Required width per size in physical px.</param>
    public static Placement Choose(
        IReadOnlyList<Box> gaps,
        WidgetSize preferred,
        IReadOnlyDictionary<WidgetSize, double> widths,
        Box taskbar,
        double? lastValidX,
        double? trayNotifyLeft)
    {
        // The first size fitting ANY gap is the largest size not wider than the widest gap.
        var widest = gaps.Count == 0 ? 0 : gaps.Max(g => g.Right - g.Left);
        if (WidgetLayoutPolicy.Choose(preferred, widest, widths) is { } size)
        {
            var width = widths[size];
            for (var i = gaps.Count - 1; i >= 0; i--)
            {
                if (gaps[i].Right - gaps[i].Left >= width)
                {
                    return new Placement(size, gaps[i].Right - width, NoFit: false, gaps[i]);
                }
            }
        }

        var buttonsWidth = widths.TryGetValue(WidgetSize.ButtonsOnly, out var w) ? w : 0;
        var x = lastValidX ?? FallbackX(taskbar, trayNotifyLeft, buttonsWidth);
        return new Placement(WidgetSize.ButtonsOnly, ManualPlacement.ClampX(taskbar, x, buttonsWidth), NoFit: true, null);
    }

    /// <summary>
    /// Auto mode before any valid UIA layout (or nothing fits and no last X): dock left of the Win32 TrayNotifyWnd
    /// (<c>left - 8 - width</c>), or at the taskbar's right edge when it is unknown; clamped to the taskbar.
    /// </summary>
    public static double FallbackX(Box taskbar, double? trayNotifyLeft, double width)
    {
        var x = trayNotifyLeft is { } tn && tn > taskbar.Left ? tn - Inset - width : taskbar.Right - width;
        return ManualPlacement.ClampX(taskbar, x, width);
    }
}

public enum DebounceAction
{
    AppliedImmediate,
    AppliedConfirmed,
    AppliedMaxHold,
    Unchanged,
    HeldBack,
}

public readonly record struct DebounceResult(DebounceAction Action, int HeldCount)
{
    public bool Applied => Action is DebounceAction.AppliedImmediate or DebounceAction.AppliedConfirmed or DebounceAction.AppliedMaxHold;
}

/// <summary>
/// v3.1 placement debounce, fed once per new UIA layout. A different placement (size, NoFit, or X beyond the tolerance)
/// is applied only when two consecutive candidates agree; after <see cref="MaxHold"/> held queries the latest candidate is
/// applied anyway (max-hold). The first candidate, and the first after <see cref="ArmImmediate"/> / <see cref="Reset"/>
/// (startup, Reset to auto position, TaskbarCreated), is applied at once. Not thread-safe (UI thread only).
/// </summary>
public sealed class PlacementDebouncer
{
    public const double Tolerance = 3;
    public const int MaxHold = 5;

    private Placement? _pending;
    private int _held;
    private bool _immediate = true;

    public Placement? Applied { get; private set; }

    public void ArmImmediate() => _immediate = true;

    public void Reset()
    {
        Applied = null;
        _pending = null;
        _held = 0;
        _immediate = true;
    }

    public DebounceResult Offer(Placement candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Applied is null || _immediate)
        {
            return Apply(candidate, DebounceAction.AppliedImmediate);
        }
        if (Same(candidate, Applied))
        {
            _pending = null;
            _held = 0;
            return new DebounceResult(DebounceAction.Unchanged, 0);
        }
        if (_pending is not null && Same(candidate, _pending))
        {
            return Apply(candidate, DebounceAction.AppliedConfirmed);
        }
        if (_held >= MaxHold)
        {
            return Apply(candidate, DebounceAction.AppliedMaxHold);
        }
        _pending = candidate;
        _held++;
        return new DebounceResult(DebounceAction.HeldBack, _held);
    }

    public static bool Same(Placement a, Placement b) =>
        a.Size == b.Size && a.NoFit == b.NoFit && Math.Abs(a.X - b.X) <= Tolerance;

    private DebounceResult Apply(Placement candidate, DebounceAction action)
    {
        var held = _held;
        Applied = candidate;
        _pending = null;
        _held = 0;
        _immediate = false;
        return new DebounceResult(action, held);
    }
}
