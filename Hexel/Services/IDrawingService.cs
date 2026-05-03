using Hexel.Core;

namespace Hexel.Services
{
    public interface IDrawingService
    {
        void ApplyFloodFill(SpriteState state, int x, int y, bool newState);
        void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState, int brushSize);
        void DrawBrushStamp(SpriteState state, int cx, int cy, int brushSize, bool newState);
        void DrawRectangle(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawEllipse(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void ShiftGrid(SpriteState state, int offsetX, int offsetY);
        void InvertGrid(SpriteState state);
        bool[,] GetFloodFillMask(SpriteState state, int startX, int startY, out int minX, out int minY, out int maxX, out int maxY);
    }
}
