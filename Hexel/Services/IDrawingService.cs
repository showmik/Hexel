using Hexel.Core;

namespace Hexel.Services
{
    public interface IDrawingService
    {
        void ApplyFloodFill(SpriteState state, int startIndex, bool newState);
        void DrawLine(SpriteState state, int startIdx, int endIdx, bool newState);
        void DrawRectangle(SpriteState state, int startIdx, int endIdx, bool newState);
        void DrawEllipse(SpriteState state, int startIdx, int endIdx, bool newState);
        void ShiftGrid(SpriteState state, int offsetX, int offsetY);
        void InvertGrid(SpriteState state);
    }
}
