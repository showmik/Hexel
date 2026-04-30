using System;
using System.Collections.Generic;
using Hexel.Core;

namespace Hexel.Services
{
    public class DrawingService : IDrawingService
    {
        private bool[] _shiftBuffer;

        public void ApplyFloodFill(SpriteState state, int startIndex, bool newState)
        {
            bool targetState = state.Pixels[startIndex];
            if (targetState == newState) return;

            Queue<int> queue = new Queue<int>();
            queue.Enqueue(startIndex);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (state.Pixels[current] == targetState)
                {
                    state.Pixels[current] = newState;

                    int x = current % state.Size;
                    int y = current / state.Size;

                    if (x > 0) queue.Enqueue(current - 1);
                    if (x < state.Size - 1) queue.Enqueue(current + 1);
                    if (y > 0) queue.Enqueue(current - state.Size);
                    if (y < state.Size - 1) queue.Enqueue(current + state.Size);
                }
            }
        }

        public void DrawLine(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int x0 = startIdx % state.Size;
            int y0 = startIdx / state.Size;
            int x1 = endIdx % state.Size;
            int y1 = endIdx / state.Size;

            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                state.Pixels[(y0 * state.Size) + x0] = newState;
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        public void ShiftGrid(SpriteState state, int offsetX, int offsetY)
        {
            int totalPixels = state.Size * state.Size;

            // 2. Initialize or resize the buffer ONLY if necessary
            if (_shiftBuffer == null || _shiftBuffer.Length != totalPixels)
            {
                _shiftBuffer = new bool[totalPixels];
            }
            else
            {
                // 3. Clear the buffer for reuse (significantly faster than creating a new array)
                Array.Clear(_shiftBuffer, 0, totalPixels);
            }

            for (int y = 0; y < state.Size; y++)
            {
                for (int x = 0; x < state.Size; x++)
                {
                    if (state.Pixels[(y * state.Size) + x])
                    {
                        int newX = (x + offsetX) % state.Size;
                        if (newX < 0) newX += state.Size;

                        int newY = (y + offsetY) % state.Size;
                        if (newY < 0) newY += state.Size;

                        _shiftBuffer[(newY * state.Size) + newX] = true;
                    }
                }
            }

            // 4. Copy the shifted data directly back into the existing state array
            Array.Copy(_shiftBuffer, state.Pixels, totalPixels);
        }

        public void InvertGrid(SpriteState state)
        {
            for (int i = 0; i < state.Pixels.Length; i++)
            {
                state.Pixels[i] = !state.Pixels[i];
            }
        }

        // Add this new method inside the DrawingService class
        public void DrawRectangle(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int size = state.Size;
            int x0 = startIdx % size;
            int y0 = startIdx / size;
            int x1 = endIdx % size;
            int y1 = endIdx / size;

            int minX = Math.Min(x0, x1);
            int maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1);
            int maxY = Math.Max(y0, y1);

            // Draw top and bottom edges
            for (int x = minX; x <= maxX; x++)
            {
                state.Pixels[(minY * size) + x] = newState;
                state.Pixels[(maxY * size) + x] = newState;
            }

            // Draw left and right edges
            for (int y = minY; y <= maxY; y++)
            {
                state.Pixels[(y * size) + minX] = newState;
                state.Pixels[(y * size) + maxX] = newState;
            }
        }

        public void DrawEllipse(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int size = state.Size;
            int x0 = startIdx % size;
            int y0 = startIdx / size;
            int x1 = endIdx % size;
            int y1 = endIdx / size;

            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4 * (1 - a) * b * b, dy = 4 * (b1 + 1) * a * a;
            long err = dx + dy + b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            // Helper to safely set pixels within bounds
            void SetPixel(int px, int py)
            {
                if (px >= 0 && px < size && py >= 0 && py < size)
                    state.Pixels[(py * size) + px] = newState;
            }

            do
            {
                SetPixel(x1, y0);
                SetPixel(x0, y0);
                SetPixel(x0, y1);
                SetPixel(x1, y1);
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            } while (x0 <= x1);

            // Catch flat vertical/horizontal lines
            while (y0 - y1 < b)
            {
                SetPixel(x0 - 1, y0);
                SetPixel(x1 + 1, y0);
                SetPixel(x0 - 1, y1);
                SetPixel(x1 + 1, y1);
                y0++; y1--;
            }
        }
    }
}