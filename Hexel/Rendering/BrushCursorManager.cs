using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Hexel.Core;
using Hexel.Services;
using Hexel.ViewModels;

namespace Hexel.Rendering
{
    /// <summary>
    /// Manages the brush cursor overlay: bitmap generation, positioning, and crosshair.
    /// Extracted from MainWindow.xaml.cs.
    /// </summary>
    public class BrushCursorManager
    {
        private readonly CanvasElementProvider _elements;
        private readonly Func<MainViewModel?> _getVm;
        private readonly Func<(int x, int y)> _getPixelCoords;

        private WriteableBitmap? _brushCursorBitmap;
        private int _brushCursorCachedSize = -1;
        private BrushShape _brushCursorCachedShape = (BrushShape)(-1);
        private int _brushCursorCachedAngle = -1;

        // Cached overlay dimensions — only recomputed when brush params or canvas size change
        private double _cachedOverlayW;
        private double _cachedOverlayH;
        private double _cachedHalfW;
        private double _cachedHalfH;
        private double _cachedCrossLen;
        private double _cachedCrossStroke;
        private int _cachedCanvasW = -1;
        private int _cachedCanvasH = -1;

        // Reusable TranslateTransform for the overlay (avoids layout invalidation)
        private TranslateTransform? _overlayTransform;

        public Point LastCanvasMousePos { get; set; }
        public bool IsMouseOverCanvas { get; set; }

        public BrushCursorManager(
            CanvasElementProvider elements,
            Func<MainViewModel?> getVm,
            Func<(int x, int y)> getPixelCoords)
        {
            _elements = elements ?? throw new ArgumentNullException(nameof(elements));
            _getVm = getVm ?? throw new ArgumentNullException(nameof(getVm));
            _getPixelCoords = getPixelCoords ?? throw new ArgumentNullException(nameof(getPixelCoords));
        }

        /// <summary>
        /// Called when BrushSize/Shape/Angle changes to instantly refresh using last mouse position.
        /// </summary>
        public void Refresh()
        {
            InvalidateCache();

            if (!IsMouseOverCanvas) return;
            var overlay = _elements.GetBrushCursorOverlay();
            if (overlay == null) return;
            var image = _elements.GetCanvasImage();
            if (image == null || image.ActualWidth == 0) return;

            var (x, y) = _getPixelCoords();
            Update(x, y, LastCanvasMousePos, image.ActualWidth, image.ActualHeight);
        }

        public void Update(int pixelX, int pixelY, Point mousePos, double imgWidth, double imgHeight)
        {
            var overlay = _elements.GetBrushCursorOverlay();
            if (overlay == null) return;

            var vm = _getVm();
            if (vm == null || vm.CurrentTool != ToolMode.Pencil)
            {
                Hide();
                return;
            }

            int brushSize = vm.BrushSize;
            var brushShape = vm.BrushShape;
            int brushAngle = vm.BrushAngle;
            int w = vm.SpriteState.Width;
            int h = vm.SpriteState.Height;

            // Rebuild cached overlay dimensions when brush params or canvas resolution change
            if (brushSize != _brushCursorCachedSize ||
                brushShape != _brushCursorCachedShape ||
                brushAngle != _brushCursorCachedAngle ||
                w != _cachedCanvasW || h != _cachedCanvasH ||
                _brushCursorBitmap == null)
            {
                RebuildOverlayCache(brushSize, brushShape, brushAngle, w, h);
            }

            bool completelyOutside =
                mousePos.X + _cachedHalfW < 0 || mousePos.X - _cachedHalfW > imgWidth ||
                mousePos.Y + _cachedHalfH < 0 || mousePos.Y - _cachedHalfH > imgHeight;

            if (completelyOutside)
            {
                Hide();
                return;
            }

            // Ensure the overlay has a TranslateTransform for positioning
            // (RenderTransform doesn't trigger layout passes — much faster than Canvas.SetLeft/Top)
            if (_overlayTransform == null)
            {
                _overlayTransform = new TranslateTransform();
                overlay.RenderTransform = _overlayTransform;
            }

            _overlayTransform.X = mousePos.X - _cachedHalfW;
            _overlayTransform.Y = mousePos.Y - _cachedHalfH;
            overlay.Visibility = Visibility.Visible;

            // Crosshair at exact mouse position — length and stroke scale with cell size
            var crossH = _elements.GetCrosshairH();
            var crossV = _elements.GetCrosshairV();

            if (crossH != null)
            {
                crossH.X1 = mousePos.X - _cachedCrossLen;
                crossH.X2 = mousePos.X + _cachedCrossLen;
                crossH.Y1 = mousePos.Y;
                crossH.Y2 = mousePos.Y;
                crossH.StrokeThickness = _cachedCrossStroke;
                crossH.Visibility = Visibility.Visible;
            }
            if (crossV != null)
            {
                crossV.X1 = mousePos.X;
                crossV.X2 = mousePos.X;
                crossV.Y1 = mousePos.Y - _cachedCrossLen;
                crossV.Y2 = mousePos.Y + _cachedCrossLen;
                crossV.StrokeThickness = _cachedCrossStroke;
                crossV.Visibility = Visibility.Visible;
            }

            var canvasImage = _elements.GetCanvasImage();
            if (canvasImage != null && canvasImage.Cursor != Cursors.None)
                canvasImage.Cursor = Cursors.None;
        }

        public void Hide()
        {
            var overlay = _elements.GetBrushCursorOverlay();
            if (overlay != null) overlay.Visibility = Visibility.Hidden;
            var crossH = _elements.GetCrosshairH();
            if (crossH != null) crossH.Visibility = Visibility.Hidden;
            var crossV = _elements.GetCrosshairV();
            if (crossV != null) crossV.Visibility = Visibility.Hidden;

            IsMouseOverCanvas = false;

            var canvasImage = _elements.GetCanvasImage();
            if (canvasImage != null && canvasImage.Cursor == Cursors.None)
                canvasImage.Cursor = null;
        }

        public void OnMouseLeave() => Hide();

        // ── Private helpers ───────────────────────────────────────────────

        private void InvalidateCache()
        {
            _brushCursorCachedSize = -1;
            _brushCursorCachedShape = (BrushShape)(-1);
            _brushCursorCachedAngle = -1;
            _cachedCanvasW = -1;
            _cachedCanvasH = -1;
        }

        /// <summary>
        /// Rebuilds the overlay bitmap AND all cached dimensions/metrics.
        /// Called only when brush parameters or canvas resolution change,
        /// NOT on every mouse move.
        /// </summary>
        private void RebuildOverlayCache(int brushSize, BrushShape shape, int angleDeg, int canvasW, int canvasH)
        {
            _cachedCanvasW = canvasW;
            _cachedCanvasH = canvasH;

            var grid = _elements.GetPixelGridContainer();
            double gw = grid.ActualWidth > 0 ? grid.ActualWidth : 400.0;
            double gh = grid.ActualHeight > 0 ? grid.ActualHeight : 400.0;
            double cw = gw / canvasW;
            double ch = gh / canvasH;
            double cellUnit = Math.Min(cw, ch);

            // Crosshair metrics
            _cachedCrossLen = cellUnit * 0.65;
            _cachedCrossStroke = Math.Max(cellUnit * 0.08, 0.3);

            // Apply crosshair theme brush once per cache rebuild to avoid DynamicResource lag
            var crosshairBrush = (SolidColorBrush)Application.Current.Resources["Theme.CrosshairStrokeBrush"];
            var crossH = _elements.GetCrosshairH();
            var crossV = _elements.GetCrosshairV();
            if (crossH != null) crossH.Stroke = crosshairBrush;
            if (crossV != null) crossV.Stroke = crosshairBrush;

            // Rebuild bitmap (will skip if brush params haven't changed)
            RebuildBitmap(brushSize, shape, angleDeg);

            // Compute overlay dimensions from the stamp offsets
            var offsets = DrawingService.ComputeStampOffsets(brushSize, shape, angleDeg);
            int minDx = 0, maxDx = 0, minDy = 0, maxDy = 0;
            foreach (var (dx, dy) in offsets)
            {
                if (dx < minDx) minDx = dx;
                if (dx > maxDx) maxDx = dx;
                if (dy < minDy) minDy = dy;
                if (dy > maxDy) maxDy = dy;
            }
            int stampW = maxDx - minDx + 1;
            int stampH = maxDy - minDy + 1;

            const double shrink = 0.85;
            _cachedOverlayW = stampW * cw * shrink;
            _cachedOverlayH = stampH * ch * shrink;
            _cachedHalfW = _cachedOverlayW / 2.0;
            _cachedHalfH = _cachedOverlayH / 2.0;

            // Apply overlay dimensions (only when they change, not every frame)
            var overlay = _elements.GetBrushCursorOverlay();
            if (overlay != null)
            {
                overlay.Width = _cachedOverlayW;
                overlay.Height = _cachedOverlayH;
            }
        }

        // ── Private: bitmap generation ────────────────────────────────────

        private void RebuildBitmap(int brushSize, BrushShape shape, int angleDeg)
        {
            if (brushSize == _brushCursorCachedSize &&
                shape == _brushCursorCachedShape &&
                angleDeg == _brushCursorCachedAngle &&
                _brushCursorBitmap != null) return;

            _brushCursorCachedSize = brushSize;
            _brushCursorCachedShape = shape;
            _brushCursorCachedAngle = angleDeg;

            var offsets = DrawingService.ComputeStampOffsets(brushSize, shape, angleDeg);

            int minDx = 0, maxDx = 0, minDy = 0, maxDy = 0;
            foreach (var (dx, dy) in offsets)
            {
                if (dx < minDx) minDx = dx;
                if (dx > maxDx) maxDx = dx;
                if (dy < minDy) minDy = dy;
                if (dy > maxDy) maxDy = dy;
            }

            int bmpW = maxDx - minDx + 1;
            int bmpH = maxDy - minDy + 1;
            if (bmpW < 1) bmpW = 1;
            if (bmpH < 1) bmpH = 1;

            _brushCursorBitmap = new WriteableBitmap(bmpW, bmpH, 96, 96, PixelFormats.Bgra32, null);

            var pixels = new uint[bmpW * bmpH];
            // Fetch colors from active theme
            var res = Application.Current.Resources;
            Color edge = (Color)res["Color.BrushCursorEdge"];
            Color fill = (Color)res["Color.BrushCursorFill"];
            uint edgeColor = (uint)((edge.A << 24) | (edge.R << 16) | (edge.G << 8) | edge.B);
            uint fillColor = (uint)((fill.A << 24) | (fill.R << 16) | (fill.G << 8) | fill.B);

            var inStamp = new bool[bmpW * bmpH];
            foreach (var (dx, dy) in offsets)
            {
                int px = dx - minDx;
                int py = dy - minDy;
                inStamp[py * bmpW + px] = true;
            }

            for (int py = 0; py < bmpH; py++)
            {
                for (int px = 0; px < bmpW; px++)
                {
                    if (!inStamp[py * bmpW + px]) continue;

                    bool isEdge = px == 0 || py == 0 || px == bmpW - 1 || py == bmpH - 1
                        || !inStamp[py * bmpW + (px - 1)]
                        || !inStamp[py * bmpW + (px + 1)]
                        || !inStamp[(py - 1) * bmpW + px]
                        || !inStamp[(py + 1) * bmpW + px];

                    pixels[py * bmpW + px] = isEdge ? edgeColor : fillColor;
                }
            }

            _brushCursorBitmap.WritePixels(
                new Int32Rect(0, 0, bmpW, bmpH), pixels, bmpW * 4, 0);
            var overlayImg = _elements.GetBrushCursorOverlay();
            if (overlayImg != null) overlayImg.Source = _brushCursorBitmap;
        }
    }
}
