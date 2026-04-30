namespace Hexel.Core
{
    public class SpriteState
    {
        public int Width { get; }
        public int Height { get; }
        public bool[] Pixels { get; set; }

        public SpriteState(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new bool[width * height];
        }

        public SpriteState Clone()
        {
            return new SpriteState(Width, Height)
            {
                Pixels = this.Pixels != null ? (bool[])this.Pixels.Clone() : new bool[Width * Height]
            };
        }
    }
}