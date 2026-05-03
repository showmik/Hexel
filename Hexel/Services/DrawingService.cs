using System;
using System.Collections.Generic;
using Hexel.Core;

namespace Hexel.Services
{
    public class DrawingService : IDrawingService
    {
        // ── Flood fill ────────────────────────────────────────────────────

        public void ApplyFloodFill(SpriteState state, int startX, int startY, bool newState)
        {
            if (startX < 0 || startX >= state.Width || startY < 0 || startY >= state.Height) return;

            int startIndex = (startY * state.Width) + startX;
            bool targetState = state.Pixels[startIndex];
            if (targetState == newState) return;

            int total = state.Width * state.Height;
            var visited = new bool[total];
            var queue = new Queue<int>();

            visited[startIndex] = true;
            queue.Enqueue(startIndex);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                state.Pixels[current] = newState;

                int x = current % state.Width;
                int y = current / state.Width;

                EnqueueIfTarget(queue, visited, state.Pixels, targetState, x > 0 ? current - 1 : -1);
                EnqueueIfTarget(queue, visited, state.Pixels, targetState, x < state.Width - 1 ? current + 1 : -1);
                EnqueueIfTarget(queue, visited, state.Pixels, targetState, y > 0 ? current - state.Width : -1);
                EnqueueIfTarget(queue, visited, state.Pixels, targetState, y < state.Height - 1 ? current + state.Width : -1);
            }
        }

        /// <summary>
        /// Enqueues a neighbor pixel if it hasn't been visited and matches the target state.
        /// Prevents the same pixel from being enqueued multiple times.
        /// </summary>
        private static void EnqueueIfTarget(Queue<int> queue, bool[] visited, bool[] pixels, bool targetState, int index)
        {
            if (index < 0 || visited[index] || pixels[index] != targetState) return;
            visited[index] = true;
            queue.Enqueue(index);
        }

        public bool[,] GetFloodFillMask(SpriteState state, int startX, int startY, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = startX;
            minY = startY;
            maxX = startX;
            maxY = startY;

            var mask = new bool[state.Width, state.Height];

            if (startX < 0 || startX >= state.Width || startY < 0 || startY >= state.Height)
                return mask;

            int startIndex = (startY * state.Width) + startX;
            bool targetState = state.Pixels[startIndex];

            var queue = new Queue<int>();
            queue.Enqueue(startIndex);
            mask[startX, startY] = true;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int x = current % state.Width;
                int y = current / state.Width;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x > 0)
                {
                    int idx = current - 1;
                    if (state.Pixels[idx] == targetState && !mask[x - 1, y])
                    {
                        mask[x - 1, y] = true;
                        queue.Enqueue(idx);
                    }
                }
                if (x < state.Width - 1)
                {
                    int idx = current + 1;
                    if (state.Pixels[idx] == targetState && !mask[x + 1, y])
                    {
                        mask[x + 1, y] = true;
                        queue.Enqueue(idx);
                    }
                }
                if (y > 0)
                {
                    int idx = current - state.Width;
                    if (state.Pixels[idx] == targetState && !mask[x, y - 1])
                    {
                        mask[x, y - 1] = true;
                        queue.Enqueue(idx);
                    }
                }
                if (y < state.Height - 1)
                {
                    int idx = current + state.Width;
                    if (state.Pixels[idx] == targetState && !mask[x, y + 1])
                    {
                        mask[x, y + 1] = true;
                        queue.Enqueue(idx);
                    }
                }
            }

            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            var croppedMask = new bool[w, h];
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    croppedMask[x - minX, y - minY] = mask[x, y];
                }
            }

            return croppedMask;
        }

        // ── Brush stamp ───────────────────────────────────────────────────

        /// <summary>
        /// Computes the set of pixel offsets (dx, dy) relative to the brush center
        /// for the given shape, size, and rotation angle. Shared by DrawBrushStamp
        /// and the cursor preview in the View.
        /// </summary>
        public static List<(int dx, int dy)> ComputeStampOffsets(int brushSize, BrushShape shape, int angleDeg)
        {
            var offsets = new List<(int dx, int dy)>();

            if (brushSize <= 1)
            {
                offsets.Add((0, 0));
                return offsets;
            }

            switch (shape)
            {
                case BrushShape.Circle:
                {
                    // Use float radius for symmetrical shapes at all sizes
                    double radius = brushSize / 2.0;
                    double rSq = radius * radius;
                    for (int py = 0; py < brushSize; py++)
                    {
                        for (int px = 0; px < brushSize; px++)
                        {
                            double cx = px - radius + 0.5;
                            double cy = py - radius + 0.5;
                            if (cx * cx + cy * cy <= rSq)
                            {
                                int dx = px - (brushSize - 1) / 2;
                                int dy = py - (brushSize - 1) / 2;
                                offsets.Add((dx, dy));
                            }
                        }
                    }
                    break;
                }

                case BrushShape.Square:
                {
                    int half = (brushSize - 1) / 2;
                    double rad = angleDeg * Math.PI / 180.0;
                    double cos = Math.Cos(rad), sin = Math.Sin(rad);
                    var set = new HashSet<(int, int)>();

                    for (int dy = -half; dy <= half + (brushSize % 2 == 0 ? 0 : 0); dy++)
                    {
                        for (int dx = -half; dx <= half; dx++)
                        {
                            int rx = (int)Math.Round(dx * cos - dy * sin);
                            int ry = (int)Math.Round(dx * sin + dy * cos);
                            set.Add((rx, ry));
                        }
                    }
                    offsets.AddRange(set);
                    break;
                }

                case BrushShape.Line:
                {
                    int half = (brushSize - 1) / 2;
                    double rad = angleDeg * Math.PI / 180.0;
                    double cos = Math.Cos(rad), sin = Math.Sin(rad);
                    var set = new HashSet<(int, int)>();

                    // Horizontal line of brushSize pixels, rotated by angleDeg
                    for (int dx = -half; dx <= half; dx++)
                    {
                        int rx = (int)Math.Round(dx * cos);
                        int ry = (int)Math.Round(dx * sin);
                        set.Add((rx, ry));
                    }
                    offsets.AddRange(set);
                    break;
                }
            }

            return offsets;
        }

        public void DrawBrushStamp(SpriteState state, int cx, int cy, int brushSize, bool newState, BrushShape shape = BrushShape.Circle, int angleDeg = 0)
        {
            if (brushSize <= 1)
            {
                if (cx >= 0 && cx < state.Width && cy >= 0 && cy < state.Height)
                    state.Pixels[(cy * state.Width) + cx] = newState;
                return;
            }

            int w = state.Width, h = state.Height;
            var offsets = ComputeStampOffsets(brushSize, shape, angleDeg);
            foreach (var (dx, dy) in offsets)
            {
                int px = cx + dx, py = cy + dy;
                if (px >= 0 && px < w && py >= 0 && py < h)
                    state.Pixels[(py * w) + px] = newState;
            }
        }

        // ── Line ──────────────────────────────────────────────────────────

        public void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < state.Width && y0 >= 0 && y0 < state.Height)
                    state.Pixels[(y0 * state.Width) + x0] = newState;
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        public void DrawLine(SpriteState state, int x0, int y0, int x1, int y1, bool newState, int brushSize, BrushShape shape = BrushShape.Circle, int angleDeg = 0)
        {
            if (brushSize <= 1)
            {
                DrawLine(state, x0, y0, x1, y1, newState);
                return;
            }

            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                DrawBrushStamp(state, x0, y0, brushSize, newState, shape, angleDeg);
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        // ── Rectangle ─────────────────────────────────────────────────────

        public void DrawRectangle(SpriteState state, int x0, int y0, int x1, int y1, bool newState)
        {
            if (x0 == x1 && y0 == y1)
            {
                // Degenerate case: single pixel
                if (x0 >= 0 && x0 < state.Width && y0 >= 0 && y0 < state.Height)
                    state.Pixels[(y0 * state.Width) + x0] = newState;
                return;
            }

            int width = state.Width;
            int height = state.Height;

            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);

            for (int x = minX; x <= maxX; x++)
            {
                if (x >= 0 && x < width)
                {
                    if (minY >= 0 && minY < height) state.Pixels[(minY * width) + x] = newState;
                    if (maxY >= 0 && maxY < height) state.Pixels[(maxY * width) + x] = newState;
                }
            }
            for (int y = minY + 1; y < maxY; y++)
            {
                if (y >= 0 && y < height)
                {
                    if (minX >= 0 && minX < width) state.Pixels[(y * width) + minX] = newState;
                    if (maxX >= 0 && maxX < width) state.Pixels[(y * width) + maxX] = newState;
                }
            }
        }

        // ── Filled Rectangle ──────────────────────────────────────────────

        public void DrawFilledRectangle(SpriteState state, int x0, int y0, int x1, int y1, bool newState)
        {
            int width = state.Width;
            int height = state.Height;

            int minX = Math.Clamp(Math.Min(x0, x1), 0, width - 1);
            int maxX = Math.Clamp(Math.Max(x0, x1), 0, width - 1);
            int minY = Math.Clamp(Math.Min(y0, y1), 0, height - 1);
            int maxY = Math.Clamp(Math.Max(y0, y1), 0, height - 1);

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    state.Pixels[(y * width) + x] = newState;
        }

        // ── Ellipse ───────────────────────────────────────────────────────

        public void DrawEllipse(SpriteState state, int x0, int y0, int x1, int y1, bool newState)
        {
            int width = state.Width;
            int height = state.Height;

            if (x0 == x1 && y0 == y1)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                    state.Pixels[(y0 * width) + x0] = newState;
                return;
            }

            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4L * (1 - a) * b * b, dy = 4L * (b1 + 1) * a * a;
            long err = dx + dy + (long)b1 * a * a, e2;

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
                SetPixel(x1, y0); SetPixel(x0, y0);
                SetPixel(x0, y1); SetPixel(x1, y1);
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            }
            while (x0 <= x1);

            while (y0 - y1 < b)
            {
                SetPixel(x0 - 1, y0); SetPixel(x1 + 1, y0);
                SetPixel(x0 - 1, y1); SetPixel(x1 + 1, y1);
                y0++; y1--;
            }
        }

        // ── Filled Ellipse ────────────────────────────────────────────────

        public void DrawFilledEllipse(SpriteState state, int x0, int y0, int x1, int y1, bool newState)
        {
            int width = state.Width;
            int height = state.Height;

            if (x0 == x1 && y0 == y1)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                    state.Pixels[(y0 * width) + x0] = newState;
                return;
            }

            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4L * (1 - a) * b * b, dy = 4L * (b1 + 1) * a * a;
            long err = dx + dy + (long)b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            void FillRow(int lx, int rx, int py)
            {
                if (py < 0 || py >= height) return;
                int clampL = Math.Clamp(lx, 0, width - 1);
                int clampR = Math.Clamp(rx, 0, width - 1);
                for (int px = clampL; px <= clampR; px++)
                    state.Pixels[(py * width) + px] = newState;
            }

            do
            {
                FillRow(x0, x1, y0);
                FillRow(x0, x1, y1);
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            }
            while (x0 <= x1);

            while (y0 - y1 < b)
            {
                FillRow(x0 - 1, x1 + 1, y0);
                FillRow(x0 - 1, x1 + 1, y1);
                y0++; y1--;
            }
        }

        // ── Grid operations ───────────────────────────────────────────────

        public void ShiftGrid(SpriteState state, int offsetX, int offsetY)
        {
            int total = state.Width * state.Height;

            // FIX: was a reused instance field. Now local to avoid shared mutable state
            // between concurrent calls (shouldn't happen, but the instance field was
            // also wasteful for large canvases that were later resized smaller).
            var buffer = new bool[total];

            for (int y = 0; y < state.Height; y++)
            {
                for (int x = 0; x < state.Width; x++)
                {
                    if (!state.Pixels[(y * state.Width) + x]) continue;

                    int newX = ((x + offsetX) % state.Width + state.Width) % state.Width;
                    int newY = ((y + offsetY) % state.Height + state.Height) % state.Height;
                    buffer[(newY * state.Width) + newX] = true;
                }
            }

            Array.Copy(buffer, state.Pixels, total);
        }

        public void InvertGrid(SpriteState state)
        {
            for (int i = 0; i < state.Pixels.Length; i++)
                state.Pixels[i] = !state.Pixels[i];
        }
    }
}
