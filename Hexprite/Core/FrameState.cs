using System.Collections.Generic;

namespace Hexprite.Core
{
    public class FrameState
    {
        public string Name { get; set; } = "Frame 1";
        public List<LayerState> Layers { get; set; } = new();

        public FrameState Clone()
        {
            return new FrameState
            {
                Name = Name,
                Layers = Layers.ConvertAll(l => l.Clone())
            };
        }
    }
}
