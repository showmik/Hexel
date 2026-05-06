using System;

namespace Hexprite.Core
{
    public class LayerState
    {
        public string Name { get; set; } = "Layer";
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; }
        public bool[] Pixels { get; set; } = Array.Empty<bool>();

        public LayerState Clone()
        {
            return new LayerState
            {
                Name = Name,
                IsVisible = IsVisible,
                IsLocked = IsLocked,
                Pixels = (bool[])Pixels.Clone()
            };
        }
    }
}
