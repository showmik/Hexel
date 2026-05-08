using System.Text.Json;
using Hexprite.Core;
using Xunit;

namespace Hexprite.Tests;

public class SpriteStateTests
{
    [Fact]
    public void Constructor_SetsDimensions_AndInitializesPixels()
    {
        var state = new SpriteState(8, 16);

        Assert.Equal(8, state.Width);
        Assert.Equal(16, state.Height);
        Assert.Equal(128, state.Pixels.Length);
        Assert.All(state.Pixels, p => Assert.False(p));
    }

    [Fact]
    public void Constructor_CreatesDefaultLayer()
    {
        var state = new SpriteState(4, 4);

        Assert.Single(state.Layers);
        Assert.Equal("Layer 1", state.Layers[0].Name);
        Assert.True(state.Layers[0].IsVisible);
        Assert.Same(state.Pixels, state.Layers[0].Pixels);
        Assert.Equal(0, state.ActiveLayerIndex);
    }

    [Fact]
    public void ActiveLayerPixels_ReturnsCorrectLayer()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Add(new LayerState { Name = "Layer 2", Pixels = new bool[16] });
        state.ActiveLayerIndex = 1;

        Assert.Same(state.Layers[1].Pixels, state.ActiveLayerPixels);
    }

    [Fact]
    public void SetActiveLayer_UpdatesIndexAndPixels()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Add(new LayerState { Name = "Layer 2", Pixels = new bool[16] });

        state.SetActiveLayer(1);

        Assert.Equal(1, state.ActiveLayerIndex);
        Assert.Same(state.Layers[1].Pixels, state.Pixels);
    }

    [Fact]
    public void SetActiveLayer_ClampsToValidRange()
    {
        var state = new SpriteState(4, 4);

        state.SetActiveLayer(-1);
        Assert.Equal(0, state.ActiveLayerIndex);

        state.SetActiveLayer(100);
        Assert.Equal(0, state.ActiveLayerIndex);

        state.Layers.Add(new LayerState { Pixels = new bool[16] });
        state.SetActiveLayer(100);
        Assert.Equal(1, state.ActiveLayerIndex);
    }

    [Fact]
    public void SetActiveLayerPixels_UpdatesActiveLayer()
    {
        var state = new SpriteState(4, 4);
        var newPixels = new bool[16];
        newPixels[0] = true;

        state.SetActiveLayerPixels(newPixels);

        Assert.Same(newPixels, state.Layers[0].Pixels);
        Assert.Same(newPixels, state.Pixels);
        Assert.True(state.Pixels[0]);
    }

    [Fact]
    public void SetActiveLayerPixels_InvalidLength_Throws()
    {
        var state = new SpriteState(4, 4);

        Assert.Throws<ArgumentException>(() => state.SetActiveLayerPixels(new bool[8]));
        Assert.Throws<ArgumentException>(() => state.SetActiveLayerPixels(new bool[32]));
        Assert.Throws<ArgumentException>(() => state.SetActiveLayerPixels(null!));
    }

    [Fact]
    public void CompositeVisiblePixels_MergesVisibleLayers()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Clear();

        var layer1 = new LayerState
        {
            Name = "Bottom",
            IsVisible = true,
            Pixels = new bool[16]
        };
        layer1.Pixels[0] = true;

        var layer2 = new LayerState
        {
            Name = "Middle",
            IsVisible = true,
            Pixels = new bool[16]
        };
        layer2.Pixels[1] = true;

        var layer3 = new LayerState
        {
            Name = "Hidden",
            IsVisible = false,
            Pixels = new bool[16]
        };
        layer3.Pixels[2] = true;

        state.Layers.Add(layer1);
        state.Layers.Add(layer2);
        state.Layers.Add(layer3);
        state.NormalizeLayerState();

        var composite = state.CompositeVisiblePixels();

        Assert.True(composite[0]); // From visible layer 1
        Assert.True(composite[1]); // From visible layer 2
        Assert.False(composite[2]); // Hidden layer excluded
    }

    [Fact]
    public void CompositeVisiblePixels_NoVisibleLayers_ReturnsEmpty()
    {
        var state = new SpriteState(4, 4);
        state.Layers[0].IsVisible = false;

        var composite = state.CompositeVisiblePixels();

        Assert.All(composite, p => Assert.False(p));
    }

    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        var original = new SpriteState(4, 4);
        original.Pixels[0] = true;
        original.IsDisplayInverted = true;
        original.ExportSettings = new ExportSettings { SpriteName = "Test" };

        var clone = original.Clone();

        Assert.Equal(original.Width, clone.Width);
        Assert.Equal(original.Height, clone.Height);
        Assert.Equal(original.Pixels, clone.Pixels);
        Assert.Equal(original.IsDisplayInverted, clone.IsDisplayInverted);
        Assert.Equal(original.ActiveLayerIndex, clone.ActiveLayerIndex);

        // Modifying clone should not affect original
        clone.Pixels[1] = true;
        Assert.False(original.Pixels[1]);
    }

    [Fact]
    public void Clone_Layers_AreDeepCopied()
    {
        var original = new SpriteState(4, 4);
        original.Layers.Add(new LayerState { Name = "Layer 2", Pixels = new bool[16] });

        var clone = original.Clone();

        Assert.Equal(original.Layers.Count, clone.Layers.Count);
        Assert.NotSame(original.Layers[0], clone.Layers[0]);
        Assert.NotSame(original.Layers[0].Pixels, clone.Layers[0].Pixels);
    }

    [Fact]
    public void Clone_ExportSettings_NotCopied()
    {
        var original = new SpriteState(4, 4)
        {
            ExportSettings = new ExportSettings { SpriteName = "Test" }
        };

        var clone = original.Clone();

        // ExportSettings should not be cloned (by design)
        Assert.Null(clone.ExportSettings);
    }

    [Fact]
    public void Clone_WithSelectionSnapshot_ClonesSnapshot()
    {
        var original = new SpriteState(4, 4);
        original.SelectionSnapshot = new SelectionSnapshot
        {
            HasActiveSelection = true,
            MinX = 0,
            MaxX = 3
        };

        var clone = original.Clone(cloneSelectionSnapshot: true);

        Assert.NotNull(clone.SelectionSnapshot);
        Assert.Equal(original.SelectionSnapshot.HasActiveSelection, clone.SelectionSnapshot.HasActiveSelection);
        Assert.Equal(original.SelectionSnapshot.MinX, clone.SelectionSnapshot.MinX);
        Assert.NotSame(original.SelectionSnapshot, clone.SelectionSnapshot);
    }

    [Fact]
    public void Clone_WithoutSelectionSnapshot_KeepsReference()
    {
        var original = new SpriteState(4, 4);
        var snapshot = new SelectionSnapshot { HasActiveSelection = true };
        original.SelectionSnapshot = snapshot;

        var clone = original.Clone(cloneSelectionSnapshot: false);

        Assert.Same(snapshot, clone.SelectionSnapshot);
    }

    [Fact]
    public void NormalizeLayerState_CreatesDefaultLayer_WhenEmpty()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Clear();
        state.ActiveLayerIndex = 99;

        var changed = state.NormalizeLayerState();

        Assert.True(changed);
        Assert.Single(state.Layers);
        Assert.Equal(0, state.ActiveLayerIndex);
        Assert.Equal("Layer 1", state.Layers[0].Name);
        Assert.Same(state.Layers[0].Pixels, state.Pixels);
    }

    [Fact]
    public void NormalizeLayerState_RepairsInvalidLayerNames()
    {
        var state = new SpriteState(4, 4);
        state.Layers[0].Name = "  ";

        state.NormalizeLayerState();

        Assert.Equal("Layer 1", state.Layers[0].Name);
    }

    [Fact]
    public void NormalizeLayerState_RepairsInvalidPixelArrays()
    {
        var state = new SpriteState(4, 4);
        state.Layers[0].Pixels = new bool[8]; // Wrong size

        state.NormalizeLayerState();

        Assert.Equal(16, state.Layers[0].Pixels.Length);
    }

    [Fact]
    public void NormalizeLayerState_ClampsActiveLayerIndex()
    {
        var state = new SpriteState(4, 4);
        state.Layers.Add(new LayerState { Pixels = new bool[16] });
        state.ActiveLayerIndex = 10;

        state.NormalizeLayerState();

        Assert.Equal(1, state.ActiveLayerIndex);
    }

    [Fact]
    public void NormalizeLayerState_RepairsPixelsReference()
    {
        var state = new SpriteState(4, 4);
        var wrongPixels = new bool[16];
        state.Pixels = wrongPixels; // Wrong reference

        state.NormalizeLayerState();

        Assert.Same(state.Layers[state.ActiveLayerIndex].Pixels, state.Pixels);
    }

    [Fact]
    public void JsonSerialization_RoundTrip_PreservesAllData()
    {
        var original = new SpriteState(8, 8);
        original.Pixels[0] = true;
        original.Pixels[63] = true;
        original.IsDisplayInverted = true;
        original.Layers[0].Name = "Test Layer";
        original.ExportSettings = new ExportSettings { SpriteName = "Test" };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<SpriteState>(json);

        Assert.NotNull(restored);
        restored!.NormalizeLayerState();

        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.Height, restored.Height);
        Assert.Equal(original.IsDisplayInverted, restored.IsDisplayInverted);
        Assert.Equal(original.Pixels, restored.Pixels);
        Assert.Equal(original.Layers[0].Name, restored.Layers[0].Name);
    }

    [Fact]
    public void MaxDimension_Is512()
    {
        Assert.Equal(512, SpriteState.MaxDimension);
    }
}
