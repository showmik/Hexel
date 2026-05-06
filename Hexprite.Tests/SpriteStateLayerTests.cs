using System.Text.Json;
using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public class SpriteStateLayerTests
{
    [Fact]
    public void NormalizeLayerState_CreatesDefaultLayer_AndFixesActiveReference()
    {
        var state = new SpriteState(8, 8)
        {
            Layers = new List<LayerState>(),
            ActiveLayerIndex = 99,
            Pixels = new bool[1]
        };

        state.NormalizeLayerState();

        Assert.Single(state.Layers);
        Assert.Equal(0, state.ActiveLayerIndex);
        Assert.Same(state.Layers[0].Pixels, state.Pixels);
        Assert.Equal(64, state.Pixels.Length);
    }

    [Fact]
    public void NormalizeLayerState_RepairsInvalidLayerNameAndPixelLength()
    {
        var state = new SpriteState(4, 4);
        state.Layers[0].Name = " ";
        state.Layers[0].Pixels = new bool[3];

        state.NormalizeLayerState();

        Assert.Equal("Layer 1", state.Layers[0].Name);
        Assert.Equal(16, state.Layers[0].Pixels.Length);
        Assert.Same(state.Layers[0].Pixels, state.Pixels);
    }

    [Fact]
    public void HistoryService_UndoRedo_RestoresLayerMutations()
    {
        var history = new HistoryService();
        var state = new SpriteState(4, 4);
        state.Layers[0].Name = "Base";
        history.SaveState(state);

        state.Layers[0].Name = "Edited";
        var undone = history.Undo(state);
        Assert.Equal("Base", undone.Layers[0].Name);

        var redone = history.Redo(undone);
        Assert.Equal("Edited", redone.Layers[0].Name);
    }

    [Fact]
    public void PersistenceRoundTrip_PreservesLayerOrderAndActiveLayer()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Clear();
        state.Layers.Add(new LayerState
        {
            Name = "Bottom",
            IsVisible = true,
            IsLocked = false,
            Pixels = new bool[16]
        });
        state.Layers.Add(new LayerState
        {
            Name = "Top",
            IsVisible = false,
            IsLocked = true,
            Pixels = new bool[16]
        });
        state.ActiveLayerIndex = 1;
        state.NormalizeLayerState();

        string json = JsonSerializer.Serialize(state);
        var loaded = JsonSerializer.Deserialize<SpriteState>(json);

        Assert.NotNull(loaded);
        loaded!.NormalizeLayerState();
        Assert.Equal(2, loaded.Layers.Count);
        Assert.Equal("Bottom", loaded.Layers[0].Name);
        Assert.Equal("Top", loaded.Layers[1].Name);
        Assert.Equal(1, loaded.ActiveLayerIndex);
        Assert.False(loaded.Layers[1].IsVisible);
        Assert.True(loaded.Layers[1].IsLocked);
    }
}
