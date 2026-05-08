using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public class HistoryServiceTests
{
    [Fact]
    public void Undo_WhenEmpty_ReturnsSameState()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);

        var result = history.Undo(state);

        Assert.Same(state, result);
    }

    [Fact]
    public void Redo_WhenEmpty_ReturnsSameState()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);

        var result = history.Redo(state);

        Assert.Same(state, result);
    }

    [Fact]
    public void SaveState_NullState_DoesNotThrow()
    {
        var history = new HistoryService();

        var exception = Record.Exception(() => history.SaveState(null!));

        Assert.Null(exception);
    }

    [Fact]
    public void UndoRedo_RoundTrip_RestoresOriginal()
    {
        var history = new HistoryService();
        var original = new SpriteState(4, 4);
        original.Pixels[0] = true;
        original.Pixels[5] = true;
        history.SaveState(original);

        // Modify current state
        var modified = new SpriteState(4, 4);
        modified.Pixels[15] = true;

        // Undo restores original
        var restored = history.Undo(modified);
        Assert.Equal(original.Pixels, restored.Pixels);

        // Redo restores modified
        var redone = history.Redo(restored);
        Assert.Equal(modified.Pixels, redone.Pixels);
    }

    [Fact]
    public void SaveState_AfterUndo_ClearsRedoStack()
    {
        var history = new HistoryService();
        var state1 = new SpriteState(4, 4);
        state1.Pixels[0] = true;
        history.SaveState(state1);

        var state2 = new SpriteState(4, 4);
        state2.Pixels[1] = true;
        history.SaveState(state2);

        // Undo to state1
        var current = new SpriteState(4, 4);
        var restored = history.Undo(current);

        // Save new state - should clear redo stack
        var state3 = new SpriteState(4, 4);
        state3.Pixels[2] = true;
        history.SaveState(state3);

        // Redo should return same state (redo stack cleared)
        var afterRedo = history.Redo(restored);
        Assert.Same(restored, afterRedo);
    }

    [Fact]
    public void SaveState_MaxHistory_Enforced()
    {
        var history = new HistoryService();

        // Save 101 states (exceeds MaxHistory of 100)
        for (int i = 0; i < 101; i++)
        {
            var state = new SpriteState(4, 4);
            state.Pixels[i % 16] = true;
            history.SaveState(state);
        }

        // Undo 100 times should still work, but 101st should return same
        var current = new SpriteState(4, 4);
        SpriteState? restored = null;
        for (int i = 0; i < 100; i++)
        {
            restored = history.Undo(current);
        }

        // 101st undo should return same state
        var finalUndo = history.Undo(restored ?? current);
        Assert.Same(restored, finalUndo);
    }

    [Fact]
    public void Undo_RestoresLayerState()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);
        state.Layers[0].Name = "Original Name";
        history.SaveState(state);

        var modified = new SpriteState(4, 4);
        modified.Layers[0].Name = "Modified Name";

        var restored = history.Undo(modified);

        Assert.Equal("Original Name", restored.Layers[0].Name);
    }

    [Fact]
    public void Undo_RestoresActiveLayerIndex()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);
        state.Layers.Add(new LayerState { Name = "Layer 2", Pixels = new bool[16] });
        state.ActiveLayerIndex = 1;
        history.SaveState(state);

        var modified = new SpriteState(4, 4);
        modified.ActiveLayerIndex = 0;

        var restored = history.Undo(modified);

        Assert.Equal(1, restored.ActiveLayerIndex);
    }

    [Fact]
    public void Undo_RestoresDisplayInvertedState()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4) { IsDisplayInverted = true };
        history.SaveState(state);

        var modified = new SpriteState(4, 4) { IsDisplayInverted = false };

        var restored = history.Undo(modified);

        Assert.True(restored.IsDisplayInverted);
    }

    [Fact]
    public void Undo_EnsuresLayers_AfterRestore()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);
        history.SaveState(state);

        var modified = new SpriteState(4, 4);
        modified.Layers.Clear();

        var restored = history.Undo(modified);

        // Should have normalized layers
        Assert.NotEmpty(restored.Layers);
    }
}
