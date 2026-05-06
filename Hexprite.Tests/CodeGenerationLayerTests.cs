using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public class CodeGenerationLayerTests
{
    [Fact]
    public void GenerateCode_UsesVisibleLayerComposite_HiddenLayersExcluded()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        state.Layers.Add(new LayerState
        {
            Name = "Visible",
            IsVisible = true,
            Pixels = new[] { true, false, false, false, false, false, false, false }
        });
        state.Layers.Add(new LayerState
        {
            Name = "Hidden",
            IsVisible = false,
            Pixels = new[] { false, true, false, false, false, false, false, false }
        });
        state.ActiveLayerIndex = 0;
        state.NormalizeLayerState();
        state.Pixels = state.CompositeVisiblePixels();

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings
        {
            Format = ExportFormat.RawHex,
            UseCommaSeparator = true
        };

        string code = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains("0x80", code);
        Assert.DoesNotContain("0x40", code);
    }

    [Fact]
    public void CompositeVisiblePixels_MergesVisibleLayers()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        state.Layers.Add(new LayerState
        {
            Name = "Bottom",
            IsVisible = true,
            Pixels = new[] { true, false, false, false, false, false, false, false }
        });
        state.Layers.Add(new LayerState
        {
            Name = "Top",
            IsVisible = true,
            Pixels = new[] { false, true, false, false, false, false, false, false }
        });

        bool[] composite = state.CompositeVisiblePixels();
        Assert.True(composite[0]);
        Assert.True(composite[1]);
    }

    [Fact]
    public void ParseHexToState_WritesOnlyToActiveLayer()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        state.Layers.Add(new LayerState { Name = "Bottom", IsVisible = true, Pixels = new bool[8] });
        state.Layers.Add(new LayerState { Name = "Top", IsVisible = true, Pixels = new bool[8] });
        state.ActiveLayerIndex = 1;
        state.NormalizeLayerState();

        var svc = new CodeGeneratorService();
        svc.ParseHexToState("0x80", state);

        Assert.False(state.Layers[0].Pixels[0]);
        Assert.True(state.Layers[1].Pixels[0]);
    }

    [Fact]
    public void GenerateCode_OverlaysFloatingSelectionPixels()
    {
        var state = new SpriteState(8, 1);
        Array.Clear(state.Pixels, 0, state.Pixels.Length);

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings
        {
            Format = ExportFormat.RawHex,
            UseCommaSeparator = true
        };

        bool[,] floating = new bool[1, 1];
        floating[0, 0] = true;

        string code = svc.GenerateCode(state, settings, true, floating, 0, 0, 1, 1);
        Assert.Contains("0x80", code);
    }

    [Fact]
    public async Task GenerateCodeAsync_RespectsProvidedProjectedPixels()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        state.Layers.Add(new LayerState
        {
            Name = "Layer 1",
            IsVisible = true,
            Pixels = new[] { false, false, false, false, false, false, false, false }
        });
        state.ActiveLayerIndex = 0;
        state.NormalizeLayerState();

        // Simulate VM-projected export pixels (merged visible result) that differ
        // from active-layer pixels.
        state.Pixels = new[] { true, false, false, false, false, false, false, false };

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings
        {
            Format = ExportFormat.RawHex,
            UseCommaSeparator = true
        };

        string code = await svc.GenerateCodeAsync(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains("0x80", code);
    }
}
