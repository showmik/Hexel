using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public class DrawingServiceTests
{
    private static SpriteState MakeState(int w, int h, params (int x, int y)[] onPixels)
    {
        var s = new SpriteState(w, h);
        foreach (var (x, y) in onPixels)
            s.Pixels[(y * w) + x] = true;
        return s;
    }

    private static int CountOn(SpriteState state)
    {
        int count = 0;
        for (int i = 0; i < state.Pixels.Length; i++)
            if (state.Pixels[i]) count++;
        return count;
    }

    private sealed class ClipSelectionStub : ISelectionService
    {
        public required Func<int, int, bool> Selector { get; init; }
        public bool HasActiveSelection { get; set; } = true;
        public bool IsSelecting => false;
        public bool IsFloating => false;
        public bool IsDragging => false;
        public bool IsTransforming => false;
        public TransformHandle ActiveTransformHandle => TransformHandle.None;

        public int MinX => 0;
        public int MaxX => 0;
        public int MinY => 0;
        public int MaxY => 0;
        public bool[,]? Mask => null;
        public bool[,]? FloatingPixels => null;
        public int FloatingX => 0;
        public int FloatingY => 0;
        public int FloatingWidth => 0;
        public int FloatingHeight => 0;
        public IReadOnlyList<PixelPoint> LassoPoints => [];
        public event EventHandler? SelectionChanged { add { } remove { } }
        public void BeginRectangleSelection(int x, int y, SelectionMode mode = SelectionMode.Replace) { }
        public void UpdateRectangleSelection(int currentX, int currentY) { }
        public void BeginLassoSelection(int x, int y, SelectionMode mode = SelectionMode.Replace) { }
        public void AddLassoPoint(int x, int y) { }
        public void FinalizeSelection() { }
        public void ApplyMask(bool[,] mask, int minX, int minY, int maxX, int maxY, SelectionMode mode) { }
        public bool IsPixelInSelection(int x, int y) => Selector(x, y);
        public bool IsPointInLasso(int x, int y) => false;
        public void LiftSelection(SpriteState state) { }
        public void CommitSelection(SpriteState state) { }
        public void DeleteSelection(SpriteState state) { }
        public void Cancel() { }
        public PixelClipboardData? CopySelection(SpriteState state) => null;
        public void PasteAsFloating(PixelClipboardData data, int canvasWidth, int canvasHeight) { }
        public void BeginDrag() { }
        public void MoveFloatingTo(int newX, int newY) { }
        public void EndDrag() { }
        public SelectionSnapshot CreateSnapshot() => new();
        public void RestoreSnapshot(SelectionSnapshot snapshot) { }
        public void BeginTransform(TransformHandle handle) { }
        public void UpdateTransform(int newX, int newY, int newW, int newH) { }
        public void CommitTransform() { }
        public void CancelTransform() { }
        public void FlipFloatingHorizontally() { }
        public void FlipFloatingVertically() { }
    }

    [Fact]
    public void ComputeStampOffsets_SizeOne_ReturnsOnlyOrigin()
    {
        var offsets = DrawingService.ComputeStampOffsets(1, BrushShape.Circle, 0);
        Assert.Single(offsets);
        Assert.Contains((0, 0), offsets);
    }

    [Fact]
    public void ComputeStampOffsets_Square90_HasExpectedCardinalPoints()
    {
        var offsets = DrawingService.ComputeStampOffsets(3, BrushShape.Square, 90);
        Assert.Contains((0, 0), offsets);
        Assert.Contains((-1, 0), offsets);
        Assert.Contains((1, 0), offsets);
        Assert.Contains((0, -1), offsets);
        Assert.Contains((0, 1), offsets);
    }

    [Fact]
    public void DrawBrushStamp_SizeOne_SetsSinglePixel()
    {
        var svc = new DrawingService();
        var s = MakeState(8, 8);

        svc.DrawBrushStamp(s, 3, 4, 1, true);

        Assert.True(s.Pixels[(4 * 8) + 3]);
        Assert.Equal(1, CountOn(s));
    }

    [Fact]
    public void DrawBrushStamp_ClippedSelection_OnlyWritesAllowedPixel()
    {
        var svc = new DrawingService();
        var s = MakeState(8, 8);
        svc.SetSelectionClip(new ClipSelectionStub { Selector = static (x, y) => x == 3 && y == 3 });

        svc.DrawBrushStamp(s, 3, 3, 3, true, BrushShape.Square);

        Assert.True(s.Pixels[(3 * 8) + 3]);
        Assert.Equal(1, CountOn(s));
    }

    [Fact]
    public void DrawLine_Diagonal_SetsExpectedPixels()
    {
        var svc = new DrawingService();
        var s = MakeState(6, 6);

        svc.DrawLine(s, 0, 0, 3, 3, true);

        Assert.True(s.Pixels[(0 * 6) + 0]);
        Assert.True(s.Pixels[(1 * 6) + 1]);
        Assert.True(s.Pixels[(2 * 6) + 2]);
        Assert.True(s.Pixels[(3 * 6) + 3]);
        Assert.Equal(4, CountOn(s));
    }

    [Fact]
    public void DrawLine_ReverseOrder_ProducesSameResult()
    {
        var svc = new DrawingService();
        var a = MakeState(6, 6);
        var b = MakeState(6, 6);

        svc.DrawLine(a, 0, 3, 5, 3, true);
        svc.DrawLine(b, 5, 3, 0, 3, true);

        Assert.Equal(a.Pixels, b.Pixels);
    }

    [Fact]
    public void DrawLine_WithBrushSize_DrawsThickerThanThinLine()
    {
        var svc = new DrawingService();
        var thin = MakeState(10, 10);
        var thick = MakeState(10, 10);

        svc.DrawLine(thin, 1, 1, 8, 1, true);
        svc.DrawLine(thick, 1, 1, 8, 1, true, 3, BrushShape.Circle);

        Assert.True(CountOn(thick) > CountOn(thin));
    }

    [Fact]
    public void DrawRectangle_DegenerateToPoint_SetsSinglePixel()
    {
        var svc = new DrawingService();
        var s = MakeState(5, 5);

        svc.DrawRectangle(s, 2, 2, 2, 2, true);

        Assert.True(s.Pixels[(2 * 5) + 2]);
        Assert.Equal(1, CountOn(s));
    }

    [Fact]
    public void DrawRectangle_OnlyPerimeterIsSet()
    {
        var svc = new DrawingService();
        var s = MakeState(6, 6);

        svc.DrawRectangle(s, 1, 1, 4, 4, true);

        Assert.True(s.Pixels[(1 * 6) + 1]);
        Assert.True(s.Pixels[(1 * 6) + 4]);
        Assert.True(s.Pixels[(4 * 6) + 1]);
        Assert.True(s.Pixels[(4 * 6) + 4]);
        Assert.False(s.Pixels[(2 * 6) + 2]); // interior stays off
    }

    [Fact]
    public void DrawFilledRectangle_FillsInterior()
    {
        var svc = new DrawingService();
        var s = MakeState(6, 6);

        svc.DrawFilledRectangle(s, 1, 1, 3, 3, true);

        Assert.Equal(9, CountOn(s));
        Assert.True(s.Pixels[(2 * 6) + 2]);
    }

    [Fact]
    public void DrawFilledRectangle_ClippedSelection_RespectsClip()
    {
        var svc = new DrawingService();
        var s = MakeState(6, 6);
        svc.SetSelectionClip(new ClipSelectionStub { Selector = static (x, y) => x == 2 && y == 2 });

        svc.DrawFilledRectangle(s, 0, 0, 5, 5, true);

        Assert.Equal(1, CountOn(s));
        Assert.True(s.Pixels[(2 * 6) + 2]);
    }

    [Fact]
    public void DrawEllipse_DegenerateToPoint_SetsSinglePixel()
    {
        var svc = new DrawingService();
        var s = MakeState(8, 8);

        svc.DrawEllipse(s, 4, 4, 4, 4, true);

        Assert.True(s.Pixels[(4 * 8) + 4]);
        Assert.Equal(1, CountOn(s));
    }

    [Fact]
    public void DrawFilledEllipse_FillsMorePixelsThanOutlineEllipse()
    {
        var svc = new DrawingService();
        var outline = MakeState(12, 12);
        var filled = MakeState(12, 12);

        svc.DrawEllipse(outline, 2, 3, 9, 8, true);
        svc.DrawFilledEllipse(filled, 2, 3, 9, 8, true);

        Assert.True(CountOn(filled) > CountOn(outline));
    }

    [Fact]
    public void DrawFilledEllipse_FixedCrossoverPath_FillsCenter()
    {
        var svc = new DrawingService();
        var s = MakeState(10, 10);

        svc.DrawFilledEllipse(s, 1, 1, 8, 5, true);

        // Sanity around the old row crossover bug: center area should be filled.
        Assert.True(s.Pixels[(3 * 10) + 4]);
    }

    [Fact]
    public void ApplyFloodFill_FillsOnlyConnectedRegion()
    {
        // 5x5 with a vertical barrier at x=2 except gap at y=4.
        var s = MakeState(5, 5,
            (2, 0), (2, 1), (2, 2), (2, 3));
        var svc = new DrawingService();

        svc.ApplyFloodFill(s, 0, 0, true);

        // Left upper side becomes true.
        Assert.True(s.Pixels[(0 * 5) + 0]);
        Assert.True(s.Pixels[(3 * 5) + 1]);
        // Barrier cells remain true as originally (newState=true still true).
        Assert.True(s.Pixels[(1 * 5) + 2]);
    }

    [Fact]
    public void ApplyFloodFill_StartOutOfBounds_NoChange()
    {
        var s = MakeState(4, 4, (1, 1));
        bool[] before = (bool[])s.Pixels.Clone();
        var svc = new DrawingService();

        svc.ApplyFloodFill(s, -1, 0, true);

        Assert.Equal(before, s.Pixels);
    }

    [Fact]
    public void ApplyFloodFill_StartPixelAlreadyTarget_NoChange()
    {
        var s = MakeState(4, 4, (0, 0));
        bool[] before = (bool[])s.Pixels.Clone();
        var svc = new DrawingService();

        svc.ApplyFloodFill(s, 0, 0, true);

        Assert.Equal(before, s.Pixels);
    }

    [Fact]
    public void ApplyFloodFill_ClippedSelection_FillsOnlySelectedConnectedPixels()
    {
        var s = MakeState(6, 6);
        var svc = new DrawingService();
        svc.SetSelectionClip(new ClipSelectionStub
        {
            Selector = static (x, y) => x >= 1 && x <= 3 && y >= 1 && y <= 3
        });

        svc.ApplyFloodFill(s, 2, 2, true);

        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                bool expected = x >= 1 && x <= 3 && y >= 1 && y <= 3;
                Assert.Equal(expected, s.Pixels[(y * 6) + x]);
            }
        }
    }

    [Fact]
    public void GetFloodFillMask_ReturnsCroppedMaskAndBounds()
    {
        var s = MakeState(6, 6, (3, 3), (3, 4), (4, 3), (4, 4));
        var svc = new DrawingService();

        bool[,] mask = svc.GetFloodFillMask(s, 0, 0, out int minX, out int minY, out int maxX, out int maxY);

        Assert.Equal(0, minX);
        Assert.Equal(0, minY);
        Assert.Equal(5, maxX);
        Assert.Equal(5, maxY);
        Assert.Equal(6, mask.GetLength(0));
        Assert.Equal(6, mask.GetLength(1));
        // start region is false-valued component, so obstacle points should be false in mask
        Assert.False(mask[3, 3]);
        Assert.True(mask[0, 0]);
    }

    [Fact]
    public void GetFloodFillMask_OutOfBoundsStart_ReturnsFullSizeFalseMask()
    {
        var s = MakeState(3, 2);
        var svc = new DrawingService();

        bool[,] mask = svc.GetFloodFillMask(s, 99, 99, out _, out _, out _, out _);

        Assert.Equal(3, mask.GetLength(0));
        Assert.Equal(2, mask.GetLength(1));
        Assert.False(mask[0, 0]);
        Assert.False(mask[2, 1]);
    }

    [Fact]
    public void ShiftGrid_PositiveOffset_WrapsAround()
    {
        var s = MakeState(4, 3, (0, 0), (3, 2));
        var svc = new DrawingService();

        svc.ShiftGrid(s, 1, 1);

        Assert.True(s.Pixels[(1 * 4) + 1]); // from (0,0)
        Assert.True(s.Pixels[(0 * 4) + 0]); // from (3,2) wraps
        Assert.Equal(2, CountOn(s));
    }

    [Fact]
    public void ShiftGrid_NegativeOffset_WrapsAround()
    {
        var s = MakeState(4, 3, (0, 0), (1, 1));
        var svc = new DrawingService();

        svc.ShiftGrid(s, -1, -1);

        Assert.True(s.Pixels[(2 * 4) + 3]); // (0,0) -> (3,2)
        Assert.True(s.Pixels[(0 * 4) + 0]); // (1,1) -> (0,0)
        Assert.Equal(2, CountOn(s));
    }

    [Fact]
    public void InvertGrid_TogglesAllPixels()
    {
        var s = MakeState(3, 2, (0, 0), (2, 1));
        var svc = new DrawingService();

        svc.InvertGrid(s);

        Assert.False(s.Pixels[(0 * 3) + 0]);
        Assert.False(s.Pixels[(1 * 3) + 2]);
        Assert.True(s.Pixels[(0 * 3) + 1]);
        Assert.True(s.Pixels[(1 * 3) + 0]);
        Assert.True(s.Pixels[(1 * 3) + 1]);
    }

    [Fact]
    public void RotatePixels_Clockwise90_On2x3_SwapsTo3x2()
    {
        var svc = new DrawingService();
        var src = new bool[6]; // 2×3
        src[(1 * 2) + 0] = true; // (0,1)

        var dst = svc.RotatePixels(src, 2, 3, RotationDirection.Clockwise90);

        Assert.Equal(6, dst.Length);
        // (0,1) in 2×3 → CW90 → destination (1,1) in 3×2 → flat index 4
        Assert.True(dst[(1 * 3) + 1]);
    }

    [Fact]
    public void RotatePixels_CounterClockwise_IsInverseOfClockwise90()
    {
        var svc = new DrawingService();
        var src = new bool[6];
        src[(0 * 2) + 0] = true;
        src[(2 * 2) + 1] = true;

        var cw = svc.RotatePixels(src, 2, 3, RotationDirection.Clockwise90);
        var back = svc.RotatePixels(cw, 3, 2, RotationDirection.CounterClockwise90);

        Assert.Equal(src, back);
    }

    [Fact]
    public void RotatePixels_OneEightyTwice_IsIdentity()
    {
        var svc = new DrawingService();
        var src = new bool[12];
        src[(0 * 4) + 1] = true;
        src[(2 * 4) + 3] = true;

        var once = svc.RotatePixels(src, 4, 3, RotationDirection.OneEighty);
        var twice = svc.RotatePixels(once, 4, 3, RotationDirection.OneEighty);

        Assert.Equal(src, twice);
    }

    [Fact]
    public void RotatePixels_TwoClockwise90_EqualsOneEighty()
    {
        var svc = new DrawingService();
        var src = new bool[12];
        for (int i = 0; i < 12; i++)
            src[i] = i % 3 == 0;

        var step1 = svc.RotatePixels(src, 4, 3, RotationDirection.Clockwise90);
        var step2 = svc.RotatePixels(step1, 3, 4, RotationDirection.Clockwise90);
        var direct = svc.RotatePixels(src, 4, 3, RotationDirection.OneEighty);

        Assert.Equal(direct, step2);
    }

    [Fact]
    public void FlipPixels_Horizontal_2x3_MirrorsX()
    {
        var svc = new DrawingService();
        int w = 2, h = 3;
        var src = new bool[w * h];
        src[(0 * w) + 0] = true; // (0,0)

        var dst = svc.FlipPixels(src, w, h, FlipDirection.Horizontal);

        for (int i = 0; i < dst.Length; i++)
        {
            if (i == 1) Assert.True(dst[i]); // (1,0)
            else Assert.False(dst[i]);
        }
    }

    [Fact]
    public void FlipPixels_Vertical_2x3_MirrorsY()
    {
        var svc = new DrawingService();
        int w = 2, h = 3;
        var src = new bool[w * h];
        src[(0 * w) + 0] = true; // (0,0)

        var dst = svc.FlipPixels(src, w, h, FlipDirection.Vertical);

        int expectedIndex = (h - 1) * w + 0; // (0,2) => 4
        for (int i = 0; i < dst.Length; i++)
        {
            if (i == expectedIndex) Assert.True(dst[i]);
            else Assert.False(dst[i]);
        }
    }

    [Fact]
    public void FlipPixels_DoubleHorizontal_IsIdentity()
    {
        var svc = new DrawingService();
        int w = 4, h = 3;
        var src = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src[(y * w) + x] = (x + y) % 3 == 0;

        var once = svc.FlipPixels(src, w, h, FlipDirection.Horizontal);
        var twice = svc.FlipPixels(once, w, h, FlipDirection.Horizontal);

        Assert.Equal(src, twice);
    }

    [Fact]
    public void FlipPixels_DoubleVertical_IsIdentity()
    {
        var svc = new DrawingService();
        int w = 4, h = 3;
        var src = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src[(y * w) + x] = (x * 2 + y) % 4 == 0;

        var once = svc.FlipPixels(src, w, h, FlipDirection.Vertical);
        var twice = svc.FlipPixels(once, w, h, FlipDirection.Vertical);

        Assert.Equal(src, twice);
    }
}
