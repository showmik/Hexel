using Hexprite.Controllers;
using Hexprite.Core;
using Xunit;

namespace Hexprite.Tests;

public class SelectionInputControllerResizeTests
{
    [Fact]
    public void ComputeResizeRect_EastPastWest_FlipsX_AndNormalizesRect()
    {
        var (x, y, w, h, flipX, flipY) = SelectionInputController.ComputeResizeRect(
            TransformHandle.E,
            ox: 10, oy: 20, ow: 5, oh: 3,
            dx: -10, dy: 0,
            shiftAspect: false, altFromCenter: false);

        Assert.Equal(4, x);
        Assert.Equal(20, y);
        Assert.Equal(7, w);
        Assert.Equal(3, h);
        Assert.True(flipX);
        Assert.False(flipY);
    }

    [Fact]
    public void ComputeResizeRect_SEPastNW_FlipsBothAxes()
    {
        var (x, y, w, h, flipX, flipY) = SelectionInputController.ComputeResizeRect(
            TransformHandle.SE,
            ox: 0, oy: 0, ow: 4, oh: 3,
            dx: -10, dy: -10,
            shiftAspect: false, altFromCenter: false);

        Assert.Equal(-7, x);
        Assert.Equal(-8, y);
        Assert.Equal(8, w);
        Assert.Equal(9, h);
        Assert.True(flipX);
        Assert.True(flipY);
    }

    [Fact]
    public void ComputeResizeRect_AltFromCenter_CornerCrossesCenter_FlipsAndKeepsCenterFixed()
    {
        // Original box: x=0..3 (w=4) => center at 2.0 with this code's convention (ox + ow/2).
        var (x, y, w, h, flipX, flipY) = SelectionInputController.ComputeResizeRect(
            TransformHandle.SE,
            ox: 0, oy: 0, ow: 4, oh: 4,
            dx: -3, dy: -3,
            shiftAspect: false, altFromCenter: true);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.True(flipX);
        Assert.True(flipY);
    }

    [Fact]
    public void ComputeResizeRect_ShiftAspect_EastPastWest_PreservesAspect_AndKeepsFixedEdge()
    {
        // Original aspect: 4x2. Drag E far past W so flipX becomes true; Shift should scale both axes.
        var (x, y, w, h, flipX, flipY) = SelectionInputController.ComputeResizeRect(
            TransformHandle.E,
            ox: 0, oy: 0, ow: 4, oh: 2,
            dx: -10, dy: 0,
            shiftAspect: true, altFromCenter: false);

        Assert.Equal(-7, x);
        Assert.Equal(0, y);
        Assert.Equal(8, w);
        Assert.Equal(4, h);
        Assert.True(flipX);
        Assert.False(flipY);
    }
}

