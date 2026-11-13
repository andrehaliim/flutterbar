using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Tests.Layout;

public sealed class WidgetLayoutPolicyTests
{
    private static readonly IReadOnlyDictionary<WidgetSize, double> Widths = new Dictionary<WidgetSize, double>
    {
        [WidgetSize.Full] = 560,
        [WidgetSize.Compact] = 260,
        [WidgetSize.ButtonsOnly] = 120,
    };

    [Fact]
    public void Choose_FitsPreferred()
    {
        Assert.Equal(WidgetSize.Full, WidgetLayoutPolicy.Choose(WidgetSize.Full, 600, Widths));
    }

    [Fact]
    public void Choose_StepsDownOneAtATime()
    {
        Assert.Equal(WidgetSize.Compact, WidgetLayoutPolicy.Choose(WidgetSize.Full, 300, Widths));
        Assert.Equal(WidgetSize.ButtonsOnly, WidgetLayoutPolicy.Choose(WidgetSize.Full, 150, Widths));
    }

    [Fact]
    public void Choose_NeverLargerThanPreferred()
    {
        Assert.Equal(WidgetSize.Compact, WidgetLayoutPolicy.Choose(WidgetSize.Compact, 1000, Widths));
    }

    [Fact]
    public void Choose_NothingFits_Null()
    {
        Assert.Null(WidgetLayoutPolicy.Choose(WidgetSize.Full, 100, Widths));
    }

    [Fact]
    public void Choose_SkipsSizeMissingFromDictionary()
    {
        var widths = new Dictionary<WidgetSize, double>
        {
            [WidgetSize.Full] = 560,
            [WidgetSize.ButtonsOnly] = 120,
        };

        Assert.Equal(WidgetSize.ButtonsOnly, WidgetLayoutPolicy.Choose(WidgetSize.Full, 300, widths));
    }
}
