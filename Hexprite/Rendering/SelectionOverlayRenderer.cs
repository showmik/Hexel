using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

        public SelectionOverlayRenderer(
            CanvasElementProvider elements,
            Func<MainViewModel?> getVm,
            Func<ISelectionService?> getSelection)
        {
            _elements = elements ?? throw new ArgumentNullException(nameof(elements));
            _getVm = getVm ?? throw new ArgumentNullException(nameof(getVm));
            _getSelection = getSelection ?? throw new ArgumentNullException(nameof(getSelection));
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
        }

        public void Clear()
        {
            var m = _elements.GetMarqueeOverlay();
            var l = _elements.GetLassoOverlay();
            if (m != null) m.Visibility = Visibility.Hidden;
            if (l != null) l.Visibility = Visibility.Hidden;
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
                mask = sel.FloatingPixels;
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

            if (mask != null)
            {
                var edgesByStart = new Dictionary<(int, int), List<(int, int)>>();

                Action<int, int, int, int> addEdge = (x1, y1, x2, y2) =>
                {
                    if (!edgesByStart.TryGetValue((x1, y1), out var list))
                    {
                        list = new List<(int, int)>();
                        edgesByStart[(x1, y1)] = list;
                    }
                    list.Add((x2, y2));
                };

                for (int y = 0; y < maskH; y++)
                {
                    for (int x = 0; x < maskW; x++)
                    {
                        if (mask[x, y])
                        {
                            if (y == 0 || !mask[x, y - 1]) addEdge(x, y, x + 1, y);
                            if (y == maskH - 1 || !mask[x, y + 1]) addEdge(x + 1, y + 1, x, y + 1);
                            if (x == 0 || !mask[x - 1, y]) addEdge(x, y + 1, x, y);
                            if (x == maskW - 1 || !mask[x + 1, y]) addEdge(x + 1, y, x + 1, y + 1);
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

            if (groupGeom.Children.Count == 0)
            {
                Clear();
                return;
            }

            lasso.Data = groupGeom;
            lasso.Visibility = Visibility.Visible;
            var marquee = _elements.GetMarqueeOverlay();
            if (marquee != null) marquee.Visibility = Visibility.Hidden;
        }
    }
}
