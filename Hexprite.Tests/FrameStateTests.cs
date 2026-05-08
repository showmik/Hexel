using Hexprite.Core;
using Xunit;

namespace Hexprite.Tests;

public class FrameStateTests
{
    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        var original = new FrameState
        {
            Name = "Frame 1",
            Layers =
            [
                new LayerState { Name = "Layer A", Pixels = new bool[] { true, false } },
                new LayerState { Name = "Layer B", Pixels = new bool[] { false, true } }
            ]
        };

        var clone = original.Clone();

        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Layers.Count, clone.Layers.Count);

        // Modify clone and verify original is unchanged
        clone.Name = "Modified";
        clone.Layers[0].Name = "Modified Layer";

        Assert.Equal("Frame 1", original.Name);
        Assert.Equal("Layer A", original.Layers[0].Name);
    }

    [Fact]
    public void Clone_Layers_AreDeepCopied()
    {
        var original = new FrameState
        {
            Layers =
            [
                new LayerState { Name = "Layer 1", Pixels = new bool[] { true, false } }
            ]
        };

        var clone = original.Clone();

        // Layer instances should be different
        Assert.NotSame(original.Layers[0], clone.Layers[0]);

        // Pixels should also be different arrays
        Assert.NotSame(original.Layers[0].Pixels, clone.Layers[0].Pixels);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var frame = new FrameState();

        Assert.Equal("Frame 1", frame.Name);
        Assert.Empty(frame.Layers);
    }
}
