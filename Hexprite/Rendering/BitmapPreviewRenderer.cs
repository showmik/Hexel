using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Hexprite.Rendering
{
    /// <summary>
    /// Handles bitmap-level shape plotting for live previews during drag operations.
    /// Depends only on <see cref="IBitmapBufferContext"/> rather than the full
    /// MainViewModel, keeping rendering concerns cleanly separated.
    /// </summary>
    public class BitmapPreviewRenderer
    {
        private readonly IBitmapBufferContext _ctx;

        public BitmapPreviewRenderer(IBitmapBufferContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
        }

        // ── Preview methods (called during shape drag, do not commit to state) ──

        public void PreviewLine(int x0, int y0, int x1, int y1, bool newState)
            => PreviewShape(PlotLine, x0, y0, x1, y1, newState);

        public void PreviewRectangle(int x0, int y0, int x1, int y1, bool newState)
            => PreviewShape(PlotRectangle, x0, y0, x1, y1, newState);

        public void PreviewEllipse(int x0, int y0, int x1, int y1, bool newState)
            => PreviewShape(PlotEllipse, x0, y0, x1, y1, newState);

        public void PreviewFilledRectangle(int x0, int y0, int x1, int y1, bool newState)
            => PreviewShape(PlotFilledRectangle, x0, y0, x1, y1, newState);

        public void PreviewFilledEllipse(int x0, int y0, int x1, int y1, bool newState)
            => PreviewShape(PlotFilledEllipse, x0, y0, x1, y1, newState);

        // ── Shared preview pipeline ───────────────────────────────────────
        // All preview methods follow the same pattern: redraw base → plot → flush.
        // Extracted into a single helper to eliminate the repeated boilerplate.

        private delegate void PlotAction(int x0, int y0, int x1, int y1, uint cc, uint pc);

        private void PreviewShape(PlotAction plot, int x0, int y0, int x1, int y1, bool newState)
        {
            _ctx.RedrawGridFromMemory();
            uint cc = newState ? _ctx.ColorOnUint : _ctx.ColorOffUint;
            uint pc = newState ? _ctx.PreviewOnUint : _ctx.PreviewOffUint;
            plot(x0, y0, x1, y1, cc, pc);
            FlushBitmaps();
        }

        // ── Bitmap plotting (writes directly to pixel buffers) ────────────────

        private void PlotPixel(int x, int y, uint canvasColor, uint previewColor)
        {
            int w = _ctx.SpriteState.Width;
            int h = _ctx.SpriteState.Height;
            if (x < 0 || x >= w || y < 0 || y >= h) return;

            // Clip preview to the active selection (if any)
            var sel = _ctx.SelectionService;
            if (sel.HasActiveSelection && !sel.IsFloating && !sel.IsPixelInSelection(x, y))
                return;

            int i = (y * w) + x;
            _ctx.CanvasBuffer[i] = canvasColor;
            _ctx.PreviewBuffer[i] = previewColor;
        }

        private void PlotLine(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                PlotPixel(x0, y0, cc, pc);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private void PlotRectangle(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            for (int x = minX; x <= maxX; x++) { PlotPixel(x, minY, cc, pc); PlotPixel(x, maxY, cc, pc); }
            for (int y = minY; y <= maxY; y++) { PlotPixel(minX, y, cc, pc); PlotPixel(maxX, y, cc, pc); }
        }

        private void PlotEllipse(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4L * (1 - a) * b * b, dy = 4L * (b1 + 1) * a * a;
            long err = dx + dy + (long)b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            do
            {
                PlotPixel(x1, y0, cc, pc); PlotPixel(x0, y0, cc, pc);
                PlotPixel(x0, y1, cc, pc); PlotPixel(x1, y1, cc, pc);
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            }
            while (x0 <= x1);

            while (y0 - y1 < b)
            {
                PlotPixel(x0 - 1, y0, cc, pc); PlotPixel(x1 + 1, y0, cc, pc);
                PlotPixel(x0 - 1, y1, cc, pc); PlotPixel(x1 + 1, y1, cc, pc);
                y0++; y1--;
            }
        }

        private void PlotFilledRectangle(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    PlotPixel(x, y, cc, pc);
        }

        private void PlotFilledEllipse(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4L * (1 - a) * b * b, dy = 4L * (b1 + 1) * a * a;
            long err = dx + dy + (long)b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            do
            {
                for (int px = x0; px <= x1; px++) { PlotPixel(px, y0, cc, pc); PlotPixel(px, y1, cc, pc); }
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            }
            while (x0 <= x1);

            while (y0 - y1 < b)
            {
                for (int px = x0 - 1; px <= x1 + 1; px++) { PlotPixel(px, y0, cc, pc); PlotPixel(px, y1, cc, pc); }
                y0++; y1--;
            }
        }

        private void FlushBitmaps()
        {
            var rect = new Int32Rect(0, 0, _ctx.SpriteState.Width, _ctx.SpriteState.Height);
            _ctx.CanvasBitmap.WritePixels(rect, _ctx.CanvasBuffer, _ctx.SpriteState.Width * 4, 0);
            _ctx.PreviewBitmap.WritePixels(rect, _ctx.PreviewBuffer, _ctx.SpriteState.Width * 4, 0);
        }
    }
}
