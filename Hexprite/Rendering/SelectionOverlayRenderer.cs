using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Application = System.Windows.Application;
using Hexprite.Core;
using Hexprite.Services;
using Hexprite.ViewModels;

namespace Hexprite.Rendering
{
    /// <summary>
    /// Manages marquee and lasso selection overlay rendering.
    /// Reads pixel-space data from ISelectionService and converts to screen-space.
    /// Extracted from MainWindow.xaml.cs.
    /// </summary>
    public class SelectionOverlayRenderer
    {
        private readonly CanvasElementProvider _elements;
        private readonly Func<MainViewModel?> _getVm;
        private readonly Func<ISelectionService?> _getSelection;
        private readonly List<Rectangle> _transformHandlePool = new(8);

        public SelectionOverlayRenderer(
            CanvasElementProvider elements,
            Func<MainViewModel?> getVm,
            Func<ISelectionService?> getSelection)
        {
            _elements = elements ?? throw new ArgumentNullException(nameof(elements));
            _getVm = getVm ?? throw new ArgumentNullException(nameof(getVm));
            _getSelection = getSelection ?? throw new ArgumentNullException(nameof(getSelection));
            for (int i = 0; i < 8; i++)
                _transformHandlePool.Add(new Rectangle { SnapsToDevicePixels = true, Visibility = Visibility.Hidden });
        }

        public void Update()
        {
            var sel = _getSelection();
            if (sel == null) { Clear(); return; }

            if (!sel.HasActiveSelection && !sel.IsSelecting)
            {
                Clear();
                return;
            }

            var vm = _getVm();
            if (vm == null) return;

            // Use the lasso/boundary renderer when the selection has a per-pixel mask,
            // or when actively drawing with a tool that produces one.
            // A finalized marquee selection (Mask == null) always uses the marquee renderer
            // regardless of which tool is currently active.
            bool needsLassoRenderer = sel.Mask != null
                || (sel.IsSelecting && (vm.CurrentTool == ToolMode.Lasso || vm.CurrentTool == ToolMode.MagicWand));

            if (needsLassoRenderer)
                UpdateLassoOverlay(sel, vm);
            else
                UpdateMarqueeOverlay(sel, vm);

            UpdateTransformHandles(sel, vm);
        }

        public void Clear()
        {
            var m = _elements.GetMarqueeOverlay();
            var l = _elements.GetLassoOverlay();
            var th = _elements.GetTransformHandlesLayer();
            if (m != null) m.Visibility = Visibility.Hidden;
            if (l != null) l.Visibility = Visibility.Hidden;
            if (th != null)
            {
                foreach (var handle in _transformHandlePool)
                    handle.Visibility = Visibility.Hidden;
                th.Visibility = Visibility.Hidden;
            }
        }

        // ── Marquee (rectangle selection) ─────────────────────────────────

        private void UpdateMarqueeOverlay(ISelectionService sel, MainViewModel vm)
        {
            var marquee = _elements.GetMarqueeOverlay();
            if (marquee == null) return;

            var grid = _elements.GetPixelGridContainer();
            double gw = grid.ActualWidth > 0 ? grid.ActualWidth : 400.0;
            double gh = grid.ActualHeight > 0 ? grid.ActualHeight : 400.0;
            double cw = gw / vm.SpriteState.Width;
            double ch = gh / vm.SpriteState.Height;

            int minX = sel.IsFloating ? sel.FloatingX : sel.MinX;
            int minY = sel.IsFloating ? sel.FloatingY : sel.MinY;
            int maxX = sel.IsFloating ? sel.FloatingX + sel.FloatingWidth - 1 : sel.MaxX;
            int maxY = sel.IsFloating ? sel.FloatingY + sel.FloatingHeight - 1 : sel.MaxY;

            Canvas.SetLeft(marquee, minX * cw);
            Canvas.SetTop(marquee, minY * ch);
            marquee.Width = (maxX - minX + 1) * cw;
            marquee.Height = (maxY - minY + 1) * ch;
            marquee.Visibility = Visibility.Visible;

            var lasso = _elements.GetLassoOverlay();
            if (lasso != null) lasso.Visibility = Visibility.Hidden;
        }

        // ── Lasso / Magic Wand (per-pixel boundary tracing) ───────────────

        private void UpdateLassoOverlay(ISelectionService sel, MainViewModel vm)
        {
            var lasso = _elements.GetLassoOverlay();
            if (lasso == null) return;

            var grid = _elements.GetPixelGridContainer();
            double gw = grid.ActualWidth > 0 ? grid.ActualWidth : 400.0;
            double gh = grid.ActualHeight > 0 ? grid.ActualHeight : 400.0;
            double cw = gw / vm.SpriteState.Width;
            double ch = gh / vm.SpriteState.Height;

            bool[,]? mask;
            int baseX, baseY, maskW, maskH;

            if (sel.IsFloating && sel.FloatingPixels != null)
            {
                // Use the original mask shape if available, otherwise fallback to the colored pixels
                mask = sel.Mask ?? sel.FloatingPixels;
                baseX = sel.FloatingX;
                baseY = sel.FloatingY;
                maskW = sel.FloatingWidth;
                maskH = sel.FloatingHeight;
            }
            else if (sel.HasActiveSelection || sel.IsSelecting)
            {
                mask = sel.Mask;
                baseX = sel.MinX;
                baseY = sel.MinY;
                maskW = sel.MaxX - sel.MinX + 1;
                maskH = sel.MaxY - sel.MinY + 1;
            }
            else
            {
                mask = null;
                baseX = 0; baseY = 0; maskW = 0; maskH = 0;
            }

            var groupGeom = new GeometryGroup();

            // NEW: Hardware-accelerated vector preview during lasso drag
            // We removed the `mask == null` check because during Add/Subtract modes,
            // or after the initial 1x1 point calculation, mask is not null, but we still need the preview.
            if (sel.IsSelecting && sel.LassoPoints.Count > 1)
            {
                var dragGeom = new StreamGeometry();
                using (var ctx = dragGeom.Open())
                {
                    // Start at the first point
                    ctx.BeginFigure(new Point(sel.LassoPoints[0].X * cw, sel.LassoPoints[0].Y * ch), isFilled: true, isClosed: true);
                    
                    // Draw lines to all subsequent points
                    for (int i = 1; i < sel.LassoPoints.Count; i++)
                    {
                        ctx.LineTo(new Point(sel.LassoPoints[i].X * cw, sel.LassoPoints[i].Y * ch), isStroked: true, isSmoothJoin: false);
                    }
                }
                dragGeom.Freeze();
                groupGeom.Children.Add(dragGeom);
            }

            // During active lasso drag, drawing the vector preview path is enough.
            // Defer expensive per-pixel boundary extraction until selection is finalized.
            if (!sel.IsSelecting && mask != null)
            {
                // Keep renderer robust when selection bounds and mask dimensions
                // are transiently out of sync during transforms.
                int effectiveMaskW = Math.Min(maskW, mask.GetLength(0));
                int effectiveMaskH = Math.Min(maskH, mask.GetLength(1));
                if (effectiveMaskW > 0 && effectiveMaskH > 0)
                {
                    int estimatedEdges = (effectiveMaskW + effectiveMaskH) * 2;
                    var edgesByStart = new Dictionary<(int, int), List<(int, int)>>(estimatedEdges);

                    Action<int, int, int, int> addEdge = (x1, y1, x2, y2) =>
                    {
                        if (!edgesByStart.TryGetValue((x1, y1), out var list))
                        {
                            list = new List<(int, int)>(2);
                            edgesByStart[(x1, y1)] = list;
                        }
                        list.Add((x2, y2));
                    };

                    for (int y = 0; y < effectiveMaskH; y++)
                    {
                        for (int x = 0; x < effectiveMaskW; x++)
                        {
                            if (mask[x, y])
                            {
                                if (y == 0 || !mask[x, y - 1]) addEdge(x, y, x + 1, y);
                                if (y == effectiveMaskH - 1 || !mask[x, y + 1]) addEdge(x + 1, y + 1, x, y + 1);
                                if (x == 0 || !mask[x - 1, y]) addEdge(x, y + 1, x, y);
                                if (x == effectiveMaskW - 1 || !mask[x + 1, y]) addEdge(x + 1, y, x + 1, y + 1);
                            }
                        }
                    }
                    var maskGeom = new StreamGeometry();
                    using (var ctx = maskGeom.Open())
                    {
                        while (edgesByStart.Count > 0)
                        {
                            var e = edgesByStart.GetEnumerator();
                            e.MoveNext();
                            var startNode = e.Current.Key;
                            e.Dispose();

                            ctx.BeginFigure(new Point((baseX + startNode.Item1) * cw, (baseY + startNode.Item2) * ch), isFilled: false, isClosed: true);

                            var curr = startNode;
                            while (true)
                            {
                                if (!edgesByStart.TryGetValue(curr, out var list) || list.Count == 0)
                                    break;

                                var next = list[list.Count - 1];
                                list.RemoveAt(list.Count - 1);
                                if (list.Count == 0) edgesByStart.Remove(curr);

                                ctx.LineTo(new Point((baseX + next.Item1) * cw, (baseY + next.Item2) * ch), isStroked: true, isSmoothJoin: false);
                                curr = next;
                                if (curr == startNode) break;
                            }
                        }
                    }
                    maskGeom.Freeze();
                    groupGeom.Children.Add(maskGeom);
                }
            }

            if (groupGeom.Children.Count == 0)
            {
                lasso.Visibility = Visibility.Hidden;
                var marqueeEmpty = _elements.GetMarqueeOverlay();
                if (marqueeEmpty != null) marqueeEmpty.Visibility = Visibility.Hidden;
                return;
            }

            lasso.Data = groupGeom;
            lasso.Visibility = Visibility.Visible;
            var marquee = _elements.GetMarqueeOverlay();
            if (marquee != null) marquee.Visibility = Visibility.Hidden;
        }

        /// <summary>
        /// Decorative resize handles around a floating selection (hit-testing is done in the View).
        /// </summary>
        private void UpdateTransformHandles(ISelectionService sel, MainViewModel vm)
        {
            var layer = _elements.GetTransformHandlesLayer();
            if (layer == null) return;

            if (!sel.HasActiveSelection || !sel.IsFloating || sel.IsSelecting)
            {
                foreach (var handle in _transformHandlePool)
                    handle.Visibility = Visibility.Hidden;
                layer.Visibility = Visibility.Hidden;
                return;
            }

            var grid = _elements.GetPixelGridContainer();
            double gw = grid.ActualWidth > 0 ? grid.ActualWidth : 400.0;
            double gh = grid.ActualHeight > 0 ? grid.ActualHeight : 400.0;
            double cw = gw / vm.SpriteState.Width;
            double ch = gh / vm.SpriteState.Height;

            Brush stroke = (Brush)(Application.Current.TryFindResource("Brush.Accent.PreviewBorder") ?? Brushes.White);
            Brush fill = (Brush)(Application.Current.TryFindResource("Brush.Surface.Base") ?? Brushes.Black);

            double maxHs = Math.Min(cw, ch) * 0.45;
            if (maxHs <= 0)
            {
                foreach (var handle in _transformHandlePool)
                    handle.Visibility = Visibility.Hidden;
                layer.Visibility = Visibility.Hidden;
                return;
            }
            double minHs = Math.Min(4.0, maxHs);
            double hs = Math.Clamp(14.0 / vm.ZoomLevel, minHs, maxHs);

            int fx = sel.FloatingX;
            int fy = sel.FloatingY;
            int fw = sel.FloatingWidth;
            int fh = sel.FloatingHeight;

            double left = fx * cw;
            double top = fy * ch;
            double right = (fx + fw) * cw;
            double bottom = (fy + fh) * ch;
            double midX = (fx + fw * 0.5) * cw;
            double midY = (fy + fh * 0.5) * ch;

            double strokeThick = Math.Max(0.5, vm.SelectionStrokeThickness * 0.35);

            if (layer.Children.Count == 0)
            {
                foreach (var handle in _transformHandlePool)
                    layer.Children.Add(handle);
            }

            static void PositionHandle(Rectangle r, double px, double py, double hs, Brush stroke, Brush fill, double strokeThick)
            {
                r.Width = hs;
                r.Height = hs;
                r.Stroke = stroke;
                r.StrokeThickness = strokeThick;
                r.Fill = fill;
                Canvas.SetLeft(r, px - hs * 0.5);
                Canvas.SetTop(r, py - hs * 0.5);
                r.Visibility = Visibility.Visible;
            }

            PositionHandle(_transformHandlePool[0], left, top, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[1], midX, top, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[2], right, top, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[3], right, midY, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[4], right, bottom, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[5], midX, bottom, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[6], left, bottom, hs, stroke, fill, strokeThick);
            PositionHandle(_transformHandlePool[7], left, midY, hs, stroke, fill, strokeThick);

            layer.Visibility = Visibility.Visible;
        }
    }
}
