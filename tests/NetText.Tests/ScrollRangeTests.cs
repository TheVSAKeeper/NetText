using NetText.UI;
using Xunit;

namespace NetText.Tests;

public sealed class ScrollRangeTests
{
    [Fact]
    public void SmallViewportCanBeDraggedFromItsExactPositionToBothEnds()
    {
        const ulong size = 3UL * 1024 * 1024 * 1024 * 1024;
        var range = ScrollRange.FromViewport(size / 3 + 17, 2_000, size);
        var (offset, height) = range.Thumb(640, 12);

        Assert.Equal(12, height);
        Assert.Equal(range.Start, range.Drag(0, offset, 640 - height));
        Assert.Equal(0UL, range.Drag(-offset, offset, 640 - height));
        Assert.Equal(range.LastStart, range.Drag(640 - height - offset, offset, 640 - height));
    }

    [Fact]
    public void TrackSeekCentersViewportAndClampsAtBothEnds()
    {
        var range = ScrollRange.FromViewport(0, 25, 1_025);

        Assert.Equal(0UL, range.Seek(0, 400));
        Assert.Equal(500UL, range.Seek(200, 400));
        Assert.Equal(1_000UL, range.Seek(399, 400));
        Assert.False(ScrollRange.FromViewport(15, 30, 20).CanScroll);
    }
}
