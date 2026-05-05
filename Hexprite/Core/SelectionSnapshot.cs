using System;
using System.Collections.Generic;

namespace Hexprite.Core
{
    public class SelectionSnapshot
    {
        public bool HasActiveSelection { get; set; }
        public bool IsSelecting { get; set; }
        public bool IsFloating { get; set; }
        public bool IsDragging { get; set; }
        public int MinX { get; set; }
        public int MaxX { get; set; }
        public int MinY { get; set; }
        public int MaxY { get; set; }
        public bool[,]? Mask { get; set; }
        public bool[,]? FloatingPixels { get; set; }
        public int FloatingX { get; set; }
        public int FloatingY { get; set; }
        public int FloatingWidth { get; set; }
        public int FloatingHeight { get; set; }
        public List<PixelPoint> LassoPoints { get; set; } = new();

        public SelectionSnapshot Clone()
        {
            var clone = new SelectionSnapshot
            {
                HasActiveSelection = HasActiveSelection,
                IsSelecting = IsSelecting,
                IsFloating = IsFloating,
                IsDragging = IsDragging,
                MinX = MinX,
                MaxX = MaxX,
                MinY = MinY,
                MaxY = MaxY,
                FloatingX = FloatingX,
                FloatingY = FloatingY,
                FloatingWidth = FloatingWidth,
                FloatingHeight = FloatingHeight
            };

            if (Mask != null)
                clone.Mask = (bool[,])Mask.Clone();

            if (FloatingPixels != null)
                clone.FloatingPixels = (bool[,])FloatingPixels.Clone();

            if (LassoPoints != null)
                clone.LassoPoints = new List<PixelPoint>(LassoPoints);

            return clone;
        }
    }
}
