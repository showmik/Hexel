using System;

namespace Hexel.Core
{
    public class SpriteState
    {
        public int Size { get; }
        public bool[] Pixels { get; set; }

        public SpriteState(int size)
        {
            Size = size;
            Pixels = new bool[size * size];
        }

        public SpriteState Clone()
        {
            return new SpriteState(Size)
            {
                Pixels = (bool[])this.Pixels.Clone()
            };
        }
    }
}