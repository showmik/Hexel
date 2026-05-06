using System.Text.Json.Serialization;
using System.Collections.Generic;
using System;

namespace Hexprite.Core
{
    public class SpriteState
    {
        /// <summary>Hard upper bound used by NewCanvasCommand to prevent OOM crashes.</summary>
        public const int MaxDimension = 512;

        public int Width { get; }
        public int Height { get; }
        public bool[] Pixels { get; set; }
        public List<LayerState> Layers { get; set; } = new();
        public int ActiveLayerIndex { get; set; }
        [JsonIgnore]
        public bool[] ActiveLayerPixels => Layers[ActiveLayerIndex].Pixels;

        /// <summary>
        /// Stored alongside pixels so that Undo/Redo can restore the visual invert
        /// state along with the pixel data.
        /// </summary>
        public bool IsDisplayInverted { get; set; }

        /// <summary>
        /// Last-used export settings for this document.
        /// Nullable so that existing .Hexprite files (which lack this field) still
        /// deserialise cleanly; the ViewModel falls back to default settings when null.
        /// Not cloned into undo snapshots — settings changes are never undoable.
        /// </summary>
        public ExportSettings? ExportSettings { get; set; }

        /// <summary>
        /// [JsonConstructor] tells System.Text.Json to use this constructor when
        /// deserializing. Parameter names must match property names (case-insensitive).
        /// Without this, get-only Width/Height come back as 0 and Pixels is null.
        /// </summary>
        [JsonConstructor]
        public SpriteState(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new bool[width * height];
            Layers.Add(new LayerState
            {
                Name = "Layer 1",
                IsVisible = true,
                Pixels = Pixels
            });
            ActiveLayerIndex = 0;
        }

        [JsonIgnore]
        public SelectionSnapshot? SelectionSnapshot { get; set; }

        public bool NormalizeLayerState()
        {
            bool changed = false;
            int pixelCount = Width * Height;

            if (Layers == null || Layers.Count == 0)
            {
                Layers = new List<LayerState>
                {
                    new LayerState
                    {
                        Name = "Layer 1",
                        IsVisible = true,
                        Pixels = Pixels?.Length == pixelCount ? (bool[])Pixels.Clone() : new bool[pixelCount]
                    }
                };
                ActiveLayerIndex = 0;
                changed = true;
            }

            for (int i = 0; i < Layers.Count; i++)
            {
                string fallbackName = $"Layer {i + 1}";
                string normalizedName = string.IsNullOrWhiteSpace(Layers[i].Name) ? fallbackName : Layers[i].Name.Trim();
                if (Layers[i].Name != normalizedName)
                {
                    Layers[i].Name = normalizedName;
                    changed = true;
                }
                if (Layers[i].Pixels == null || Layers[i].Pixels.Length != pixelCount)
                {
                    Layers[i].Pixels = new bool[pixelCount];
                    changed = true;
                }
            }

            int clampedActiveLayer = Math.Clamp(ActiveLayerIndex, 0, Layers.Count - 1);
            if (ActiveLayerIndex != clampedActiveLayer)
            {
                ActiveLayerIndex = clampedActiveLayer;
                changed = true;
            }

            if (!ReferenceEquals(Pixels, Layers[ActiveLayerIndex].Pixels))
            {
                Pixels = Layers[ActiveLayerIndex].Pixels;
                changed = true;
            }

            return changed;
        }

        public void EnsureLayers()
        {
            NormalizeLayerState();
        }

        public void SetActiveLayerPixels(bool[] pixels)
        {
            EnsureLayers();
            if (pixels == null || pixels.Length != Width * Height)
                throw new ArgumentException("Active layer pixels must match canvas dimensions.", nameof(pixels));

            Layers[ActiveLayerIndex].Pixels = pixels;
            Pixels = Layers[ActiveLayerIndex].Pixels;
        }

        public void SetActiveLayer(int index)
        {
            EnsureLayers();
            ActiveLayerIndex = Math.Clamp(index, 0, Layers.Count - 1);
            Pixels = Layers[ActiveLayerIndex].Pixels;
        }

        public bool[] CompositeVisiblePixels()
        {
            EnsureLayers();
            var composite = new bool[Width * Height];
            foreach (var layer in Layers)
            {
                if (!layer.IsVisible) continue;
                for (int i = 0; i < composite.Length; i++)
                {
                    if (layer.Pixels[i])
                        composite[i] = true;
                }
            }
            return composite;
        }

        public SpriteState Clone()
        {
            EnsureLayers();
            var clone = new SpriteState(Width, Height)
            {
                Layers = Layers.ConvertAll(l => l.Clone()),
                ActiveLayerIndex = ActiveLayerIndex,
                IsDisplayInverted = IsDisplayInverted,
                SelectionSnapshot = SelectionSnapshot?.Clone()
                // ExportSettings intentionally NOT cloned: settings tweaks
                // should not be undone when the user hits Ctrl+Z.
            };
            clone.EnsureLayers();
            return clone;
        }
    }
}
