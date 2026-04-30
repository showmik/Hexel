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

                    int x = current % state.Width;
                    int y = current / state.Width;

                    if (x > 0) queue.Enqueue(current - 1);
                    if (x < state.Width - 1) queue.Enqueue(current + 1);
                    if (y > 0) queue.Enqueue(current - state.Width);
                    if (y < state.Height - 1) queue.Enqueue(current + state.Width);
                }
            }
        }

        public void DrawLine(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int x0 = startIdx % state.Width;
            int y0 = startIdx / state.Width;
            int x1 = endIdx % state.Width;
            int y1 = endIdx / state.Width;

            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                state.Pixels[(y0 * state.Width) + x0] = newState;
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        public void ShiftGrid(SpriteState state, int offsetX, int offsetY)
        {
            int totalPixels = state.Width * state.Height;

            if (_shiftBuffer == null || _shiftBuffer.Length != totalPixels)
            {
                _shiftBuffer = new bool[totalPixels];
            }
            else
            {
                Array.Clear(_shiftBuffer, 0, totalPixels);
            }

            for (int y = 0; y < state.Height; y++)
            {
                for (int x = 0; x < state.Width; x++)
                {
                    if (state.Pixels[(y * state.Width) + x])
                    {
                        int newX = (x + offsetX) % state.Width;
                        if (newX < 0) newX += state.Width;

                        int newY = (y + offsetY) % state.Height;
                        if (newY < 0) newY += state.Height;

                        _shiftBuffer[(newY * state.Width) + newX] = true;
                    }
                }
            }

            Array.Copy(_shiftBuffer, state.Pixels, totalPixels);
        }

        public void InvertGrid(SpriteState state)
        {
            for (int i = 0; i < state.Pixels.Length; i++)
            {
                state.Pixels[i] = !state.Pixels[i];
            }
        }

        public void DrawRectangle(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int width = state.Width;
            int x0 = startIdx % width;
            int y0 = startIdx / width;
            int x1 = endIdx % width;
            int y1 = endIdx / width;

            int minX = Math.Min(x0, x1);
            int maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1);
            int maxY = Math.Max(y0, y1);

            for (int x = minX; x <= maxX; x++)
            {
                state.Pixels[(minY * width) + x] = newState;
                state.Pixels[(maxY * width) + x] = newState;
            }

            for (int y = minY; y <= maxY; y++)
            {
                state.Pixels[(y * width) + minX] = newState;
                state.Pixels[(y * width) + maxX] = newState;
            }
        }

        public void DrawEllipse(SpriteState state, int startIdx, int endIdx, bool newState)
        {
            int width = state.Width;
            int height = state.Height;
            int x0 = startIdx % width;
            int y0 = startIdx / width;
            int x1 = endIdx % width;
            int y1 = endIdx / width;

            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4 * (1 - a) * b * b, dy = 4 * (b1 + 1) * a * a;
            long err = dx + dy + b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            void SetPixel(int px, int py)
            {
                if (px >= 0 && px < width && py >= 0 && py < height)
                    state.Pixels[(py * width) + px] = newState;
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