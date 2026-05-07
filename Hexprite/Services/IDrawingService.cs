using Hexprite.Core;

namespace Hexprite.Services
{
    public interface IDrawingService
    {
        /// <summary>
        /// Sets (or clears) the active selection clip. When set, all pixel-write
        /// operations will be masked to only affect pixels inside the selection.
        /// Pass null to disable clipping.
        /// </summary>
        void SetSelectionClip(ISelectionService? selectionService);

        void ApplyFloodFill(SpriteState state, int x, int y, bool newState);
        void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState, int brushSize, BrushShape shape = BrushShape.Circle, int angleDeg = 0);
        void DrawBrushStamp(SpriteState state, int cx, int cy, int brushSize, bool newState, BrushShape shape = BrushShape.Circle, int angleDeg = 0);
        void DrawRectangle(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawFilledRectangle(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawEllipse(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void DrawFilledEllipse(SpriteState state, int x0, int y0, int x1, int y1, bool newState);
        void ShiftGrid(SpriteState state, int offsetX, int offsetY);
        void InvertGrid(SpriteState state);

        /// <summary>
        /// Rotates a flat row-major pixel buffer (width × height). Returns a new buffer;
        /// width and height swap for 90° rotations.
        /// </summary>
        bool[] RotatePixels(bool[] src, int srcW, int srcH, RotationDirection dir);
        bool[,] GetFloodFillMask(SpriteState state, int startX, int startY, out int minX, out int minY, out int maxX, out int maxY);
    }
}
