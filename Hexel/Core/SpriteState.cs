using System.Text.Json.Serialization;

namespace Hexel.Core
{
    public class SpriteState
    {
        /// <summary>Hard upper bound used by NewCanvasCommand to prevent OOM crashes.</summary>
        public const int MaxDimension = 256;

        public int Width { get; }
        public int Height { get; }
        public bool[] Pixels { get; set; }

        /// <summary>
        /// Stored alongside pixels so that Undo/Redo can restore the visual invert
        /// state along with the pixel data.
        /// </summary>
        public bool IsDisplayInverted { get; set; }

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
        }

        public SpriteState Clone() => new SpriteState(Width, Height)
        {
            Pixels = (bool[])Pixels.Clone(),
            IsDisplayInverted = IsDisplayInverted
        };
    }
}
