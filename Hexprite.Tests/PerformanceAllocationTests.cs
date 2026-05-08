using System;
using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public class PerformanceAllocationTests
{
    [Fact]
    public void HistoryService_SaveState_DoesNotDoubleCloneSelectionSnapshot()
    {
        var history = new HistoryService();
        var selection = new SelectionSnapshot
        {
            HasActiveSelection = true,
            Mask = new bool[128, 128],
            FloatingPixels = new bool[128, 128],
            OriginalFloatingPixels = new bool[128, 128]
        };
        var state = new SpriteState(128, 128) { SelectionSnapshot = selection };

        long before = GC.GetAllocatedBytesForCurrentThread();
        history.SaveState(state);
        long after = GC.GetAllocatedBytesForCurrentThread();

        long allocated = after - before;
        // Guard against regressions that reintroduce expensive duplicate snapshot clones.
        Assert.True(allocated < 3_000_000, $"Unexpected allocation spike: {allocated:N0} bytes.");
    }

    [Fact]
    public void SelectionService_RepeatedCombine_StaysWithinAllocationBudget()
    {
        var svc = new SelectionService();
        svc.BeginRectangleSelection(10, 10, SelectionMode.Replace);
        svc.UpdateRectangleSelection(80, 80);
        svc.FinalizeSelection();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            svc.BeginRectangleSelection(5, 5, SelectionMode.Add);
            svc.UpdateRectangleSelection(90, 90);
            svc.FinalizeSelection();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        long allocated = after - before;
        Assert.True(allocated < 20_000_000, $"Combine path allocated too much: {allocated:N0} bytes.");
    }
}
