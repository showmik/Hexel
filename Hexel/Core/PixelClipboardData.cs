namespace Hexel.Core
{
    /// <summary>
    /// Immutable snapshot of copied pixel data.
    /// Stored in the application-level pixel clipboard so it can be pasted
    /// into any document tab.
    /// </summary>
    public class PixelClipboardData
    {
        public bool[,] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        public PixelClipboardData(bool[,] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }
    }
}
