namespace Hexprite.Core
{
    /// <summary>
    /// A platform-agnostic pixel coordinate. Replaces System.Windows.Point in all
    /// selection and drawing logic so those code paths stay portable.
    /// </summary>
    public readonly struct PixelPoint
    {
        public int X { get; }
        public int Y { get; }

        public PixelPoint(int x, int y) { X = x; Y = y; }

        public bool Equals(PixelPoint other) => X == other.X && Y == other.Y;
        public override string ToString() => $"({X}, {Y})";
    }
}
