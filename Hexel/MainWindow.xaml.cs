using Hexel.Core;
using Hexel.Services;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hexel
{
    public partial class MainWindow : Window
    {
        // ── Dependencies ──────────────────────────────────────────────────
        private ViewModels.MainViewModel ViewModel => (ViewModels.MainViewModel)DataContext;
        private readonly ISelectionService _selection;

        // ── Drag tracking (screen-space, belongs in the View) ─────────────
        private Point _dragStartMousePos;
        private int _dragStartFloatingX;
        private int _dragStartFloatingY;

        // ── Panning state ─────────────────────────────────────────────────
        private Point _panStartMouse;
        private Point _panStartScroll;
        private bool _isPanning;

        // ── Last hovered pixel (avoids spamming ViewModel on same pixel) ──
        private int _lastHoveredX = -1;
        private int _lastHoveredY = -1;

        // ── Draw mode locked for the duration of a stroke ─────────────────
        private DrawMode _activeDrawMode = DrawMode.None;

        // ── Status label fade timer ───────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _statusTimer;

        // ── Brush cursor bitmap cache ─────────────────────────────────────
        private System.Windows.Media.Imaging.WriteableBitmap? _brushCursorBitmap;
        private int _brushCursorCachedSize = -1;
        private Point _lastCanvasMousePos;
        private bool _isMouseOverCanvas;

        // ── Constructor ───────────────────────────────────────────────────

        public MainWindow(ViewModels.MainViewModel vm, ISelectionService selection)
        {
            InitializeComponent();
            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));

            // When SelectionService state changes, redraw the overlays
            _selection.SelectionChanged += (_, _) => UpdateSelectionOverlays();

            // When Undo/Redo fires, cancel any in-progress overlays
            vm.HistoryRestored += (_, _) =>
            {
                ClearSelectionOverlays();
                ReleaseDragCapture();
            };

            // Replace the old blocking MessageBox "Copied!" with a status label
            vm.CopyHexExecuted += (_, _) => ShowStatus("Copied to clipboard");

            // Zoom keyboard shortcuts: Ctrl+Plus, Ctrl+Minus, Ctrl+0
            CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom,
                (_, _) => ApplyZoomCentered(ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom,
                (_, _) => ApplyZoomCentered(1.0 / ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.Zoom,
                (_, _) => ZoomSlider.Value = 1.0));

            // Refresh brush cursor immediately when BrushSize changes (keyboard, slider, etc.)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.BrushSize))
                    RefreshBrushCursor();
            };
        }

        // ── Tool selection ────────────────────────────────────────────────

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is null || ViewModel is null) return;

            ViewModel.CurrentTool = rb.Tag.ToString() switch
            {
                "Fill" => ToolMode.Fill,
                "Marquee" => ToolMode.Marquee,
                "Lasso" => ToolMode.Lasso,
                "Rectangle" => ToolMode.Rectangle,
                "Ellipse" => ToolMode.Ellipse,
                "Line" => ToolMode.Line,
                _ => ToolMode.Pencil
            };

            // Switching away from a selection tool commits any floating content
            if (ViewModel.CurrentTool != ToolMode.Marquee &&
                ViewModel.CurrentTool != ToolMode.Lasso)
            {
                CommitCurrentSelection();
            }

            // Hide the brush cursor when switching away from pencil
            if (ViewModel.CurrentTool != ToolMode.Pencil)
                HideBrushCursor();
        }

        // ── Global overrides: mouse up, mouse move, key down ─────────────

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            // Update floating layer position during drag
            if (_selection.IsDragging && e.LeftButton == MouseButtonState.Pressed)
                UpdateDragPosition(e.GetPosition(PixelGridContainer));
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);

            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false);
                _activeDrawMode = DrawMode.None;
            }
            else if (ViewModel.CurrentTool == ToolMode.Pencil)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false);
                _activeDrawMode = DrawMode.None;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                // Finalise a selection on mouse-up
                if ((ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.Marquee) && _selection.IsSelecting)
                    _selection.FinalizeSelection();

                ReleaseDragCapture();
                _selection.EndDrag();
            }

            // Always release mouse capture from the canvas if we had it
            if (Mouse.Captured == CanvasImage)
                Mouse.Capture(null);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Up) { ViewModel.ShiftGrid(0, -1); e.Handled = true; }
                else if (e.Key == Key.Down) { ViewModel.ShiftGrid(0, 1); e.Handled = true; }
                else if (e.Key == Key.Left) { ViewModel.ShiftGrid(-1, 0); e.Handled = true; }
                else if (e.Key == Key.Right) { ViewModel.ShiftGrid(1, 0); e.Handled = true; }
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Keyboard.FocusedElement is TextBox) return;

                switch (e.Key)
                {
                    case Key.Delete:
                    case Key.Back:
                        if (ViewModel.DeleteSelectionCommand.CanExecute(null))
                        {
                            ViewModel.DeleteSelectionCommand.Execute(null);
                            ClearSelectionOverlays();
                            e.Handled = true;
                        }
                        break;

                    case Key.Escape:
                        CommitCurrentSelection();
                        e.Handled = true;
                        break;

                    case Key.P: if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; break;
                    case Key.L: if (RbLine != null) RbLine.IsChecked = true; e.Handled = true; break;
                    case Key.R: if (RbRectangle != null) RbRectangle.IsChecked = true; e.Handled = true; break;
                    case Key.E: if (RbEllipse != null) RbEllipse.IsChecked = true; e.Handled = true; break;
                    case Key.F: if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; break;
                    case Key.M: if (RbMarquee != null) RbMarquee.IsChecked = true; e.Handled = true; break;
                    case Key.S: if (RbLasso != null) RbLasso.IsChecked = true; e.Handled = true; break;

                    case Key.OemOpenBrackets:  // '['
                        ViewModel.BrushSize--;
                        e.Handled = true;
                        break;
                    case Key.OemCloseBrackets: // ']'
                        ViewModel.BrushSize++;
                        e.Handled = true;
                        break;
                }
            }
        }

        // ── Selection tool handling ───────────────────────────────────────

        private void HandleSelectionDown(MouseButtonEventArgs e, int x, int y)
        {

            if (_selection.HasActiveSelection && _selection.IsPixelInSelection(x, y))
            {
                // Click inside an existing selection → lift it into a floating layer and begin drag
                ViewModel.SaveStateForUndo();
                _selection.LiftSelection(ViewModel.SpriteState);
                _selection.BeginDrag();

                _dragStartMousePos = e.GetPosition(PixelGridContainer);
                _dragStartFloatingX = _selection.FloatingX;
                _dragStartFloatingY = _selection.FloatingY;

                Mouse.Capture(PixelGridContainer);
                ViewModel.RedrawGridFromMemory();
            }
            else
            {
                // Click outside → commit any existing selection and start a new one
                CommitCurrentSelection();

                if (ViewModel.CurrentTool == ToolMode.Lasso)
                    _selection.BeginLassoSelection(x, y);
                else
                    _selection.BeginRectangleSelection(x, y);
            }
        }

        private void HandleSelectionMove(int x, int y)
        {
            if (!_selection.IsSelecting) return;

            if (ViewModel.CurrentTool == ToolMode.Lasso)
                _selection.AddLassoPoint(x, y);
            else
                _selection.UpdateRectangleSelection(x, y);
        }

        private void CommitCurrentSelection()
        {
            if (!_selection.HasActiveSelection) return;
            _selection.CommitSelection(ViewModel.SpriteState);
            ViewModel.RedrawGridFromMemory();
            ViewModel.UpdateTextOutputs();
            ClearSelectionOverlays();
        }

        // ── Drag position update ──────────────────────────────────────────

        private void UpdateDragPosition(Point currentPos)
        {
            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            int newX = _dragStartFloatingX + (int)Math.Round((currentPos.X - _dragStartMousePos.X) / cw);
            int newY = _dragStartFloatingY + (int)Math.Round((currentPos.Y - _dragStartMousePos.Y) / ch);

            if (newX != _selection.FloatingX || newY != _selection.FloatingY)
            {
                _selection.MoveFloatingTo(newX, newY);
                ViewModel.RedrawGridFromMemory();
            }
        }

        private void ReleaseDragCapture()
        {
            if (Mouse.Captured == PixelGridContainer)
                Mouse.Capture(null);
        }

        // ── Overlay rendering ─────────────────────────────────────────────
        // All overlay methods are pure View concerns: they read pixel-space data
        // from ISelectionService and convert to screen-space for rendering.

        private void UpdateSelectionOverlays()
        {
            if (!_selection.HasActiveSelection && !_selection.IsSelecting)
            {
                ClearSelectionOverlays();
                return;
            }

            if (ViewModel.CurrentTool == ToolMode.Lasso)
                UpdateLassoOverlay();
            else
                UpdateMarqueeOverlay();
        }

        private void UpdateMarqueeOverlay()
        {
            if (MarqueeOverlay == null) return;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            // When a floating layer is active, highlight the floating layer bounds
            int minX = _selection.IsFloating ? _selection.FloatingX : _selection.MinX;
            int minY = _selection.IsFloating ? _selection.FloatingY : _selection.MinY;
            int maxX = _selection.IsFloating ? _selection.FloatingX + _selection.FloatingWidth - 1 : _selection.MaxX;
            int maxY = _selection.IsFloating ? _selection.FloatingY + _selection.FloatingHeight - 1 : _selection.MaxY;

            Canvas.SetLeft(MarqueeOverlay, minX * cw);
            Canvas.SetTop(MarqueeOverlay, minY * ch);
            MarqueeOverlay.Width = (maxX - minX + 1) * cw;
            MarqueeOverlay.Height = (maxY - minY + 1) * ch;
            MarqueeOverlay.Visibility = Visibility.Visible;

            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;
        }

        private void UpdateLassoOverlay()
        {
            if (LassoOverlay == null) return;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            // After finalization (or while dragging), show the per-pixel filled region.
            // Use GeometryGroup to avoid the O(n²) Geometry.Combine loop that was here before.
            bool[,]? mask;
            int baseX, baseY, maskW, maskH;

            if (_selection.IsFloating && _selection.FloatingPixels != null)
            {
                mask = _selection.FloatingPixels;
                baseX = _selection.FloatingX;
                baseY = _selection.FloatingY;
                maskW = _selection.FloatingWidth;
                maskH = _selection.FloatingHeight;
            }
            else if (_selection.IsSelecting && !_selection.HasActiveSelection)
            {
                // While the user is still drawing, just show the lasso polyline —
                // computing a full pixel mask on every mouse move is far too expensive.
                if (_selection.LassoPoints.Count < 2)
                {
                    ClearSelectionOverlays();
                    return;
                }

                var polyGeom = new StreamGeometry();
                using (var ctx = polyGeom.Open())
                {
                    var pts = _selection.LassoPoints;
                    ctx.BeginFigure(
                        new Point((pts[0].X + 0.5) * cw, (pts[0].Y + 0.5) * ch),
                        isFilled: true, isClosed: true);
                    for (int i = 1; i < pts.Count; i++)
                        ctx.LineTo(
                            new Point((pts[i].X + 0.5) * cw, (pts[i].Y + 0.5) * ch),
                            isStroked: true, isSmoothJoin: false);
                }
                polyGeom.Freeze();

                LassoOverlay.Data = polyGeom;
                LassoOverlay.Visibility = Visibility.Visible;
                if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
                return;
            }
            else if (_selection.Mask != null)
            {
                mask = _selection.Mask;
                baseX = _selection.MinX;
                baseY = _selection.MinY;
                maskW = _selection.MaxX - _selection.MinX + 1;
                maskH = _selection.MaxY - _selection.MinY + 1;
            }
            else
            {
                ClearSelectionOverlays();
                return;
            }

            var edgesByStart = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<(int, int)>>();

            System.Action<int, int, int, int> addEdge = (x1, y1, x2, y2) =>
            {
                var start = (x1, y1);
                if (!edgesByStart.TryGetValue(start, out var list))
                {
                    list = new System.Collections.Generic.List<(int, int)>();
                    edgesByStart[start] = list;
                }
                list.Add((x2, y2));
            };

            for (int fy = 0; fy < maskH; fy++)
            {
                for (int fx = 0; fx < maskW; fx++)
                {
                    if (mask[fx, fy])
                    {
                        if (fy == 0 || !mask[fx, fy - 1]) addEdge(fx, fy, fx + 1, fy);
                        if (fx == maskW - 1 || !mask[fx + 1, fy]) addEdge(fx + 1, fy, fx + 1, fy + 1);
                        if (fy == maskH - 1 || !mask[fx, fy + 1]) addEdge(fx + 1, fy + 1, fx, fy + 1);
                        if (fx == 0 || !mask[fx - 1, fy]) addEdge(fx, fy + 1, fx, fy);
                    }
                }
            }

            var geom = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = geom.Open())
            {
                while (edgesByStart.Count > 0)
                {
                    var startPointEnum = edgesByStart.Keys.GetEnumerator();
                    startPointEnum.MoveNext();
                    var currentPoint = startPointEnum.Current;

                    var startPt = new Point((baseX + currentPoint.Item1) * cw, (baseY + currentPoint.Item2) * ch);
                    bool first = true;

                    while (true)
                    {
                        if (!edgesByStart.TryGetValue(currentPoint, out var outEdges) || outEdges.Count == 0)
                        {
                            if (outEdges != null && outEdges.Count == 0) edgesByStart.Remove(currentPoint);
                            break;
                        }

                        var target = outEdges[outEdges.Count - 1];
                        outEdges.RemoveAt(outEdges.Count - 1);
                        if (outEdges.Count == 0) edgesByStart.Remove(currentPoint);

                        currentPoint = target;
                        var pt = new Point((baseX + currentPoint.Item1) * cw, (baseY + currentPoint.Item2) * ch);

                        if (first)
                        {
                            ctx.BeginFigure(startPt, isFilled: true, isClosed: true);
                            first = false;
                        }
                        ctx.LineTo(pt, isStroked: true, isSmoothJoin: false);
                    }
                }
            }
            geom.Freeze();
            Geometry finalGeom = geom;

            LassoOverlay.Data = finalGeom;
            LassoOverlay.Visibility = finalGeom != Geometry.Empty ? Visibility.Visible : Visibility.Hidden;
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
        }

        private void ClearSelectionOverlays()
        {
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;
        }

        // ── Zoom & pan ────────────────────────────────────────────────────

        private const double ZoomFactor = 1.15; // 15% per scroll notch

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
            => ApplyZoomCentered(ZoomFactor);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
            => ApplyZoomCentered(1.0 / ZoomFactor);

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
            => ZoomSlider.Value = 1.0;

        // ── Brush size buttons ───────────────────────────────────────────

        private void BtnBrushDown_Click(object sender, RoutedEventArgs e)
            => ViewModel.BrushSize--;

        private void BtnBrushUp_Click(object sender, RoutedEventArgs e)
            => ViewModel.BrushSize++;

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Hold Ctrl to pan/scroll normally; without Ctrl, scroll-wheel zooms
            if (Keyboard.Modifiers == ModifierKeys.Control)
                return; // let the ScrollViewer handle normal 

            var sv = (ScrollViewer)sender;
            double factor = e.Delta > 0 ? ZoomFactor : 1.0 / ZoomFactor;
            double oldZoom = ZoomSlider.Value;
            double newZoom = SnapToTick(Math.Clamp(oldZoom * factor, ZoomSlider.Minimum, ZoomSlider.Maximum), factor > 1.0 ? 1 : -1);

            if (Math.Abs(newZoom - oldZoom) < 0.001) return;
            e.Handled = true;

            // Compute the mouse position relative to the scroll viewport
            var mouseInSv = e.GetPosition(sv);

            // Current content offset under the mouse (in unscaled content coords)
            double contentX = (sv.HorizontalOffset + mouseInSv.X) / oldZoom;
            double contentY = (sv.VerticalOffset + mouseInSv.Y) / oldZoom;

            ZoomSlider.Value = newZoom;

            // Force layout so the new ScaleTransform is applied
            sv.UpdateLayout();

            // Scroll so that the same content point remains under the mouse
            sv.ScrollToHorizontalOffset(contentX * newZoom - mouseInSv.X);
            sv.ScrollToVerticalOffset(contentY * newZoom - mouseInSv.Y);
        }

        /// <summary>
        /// Applies a multiplicative zoom step, centering on the viewport midpoint.
        /// Used by the +/- buttons and Ctrl+Plus/Minus keyboard shortcuts.
        /// </summary>
        private void ApplyZoomCentered(double factor)
        {
            var sv = FindScrollViewer();
            if (sv == null)
            {
                // Fallback: just change the slider value
                ZoomSlider.Value = SnapToTick(Math.Clamp(ZoomSlider.Value * factor, ZoomSlider.Minimum, ZoomSlider.Maximum), factor > 1.0 ? 1 : -1);
                return;
            }

            double oldZoom = ZoomSlider.Value;
            double newZoom = SnapToTick(Math.Clamp(oldZoom * factor, ZoomSlider.Minimum, ZoomSlider.Maximum), factor > 1.0 ? 1 : -1);
            if (Math.Abs(newZoom - oldZoom) < 0.001) return;

            // Center of the viewport
            double cx = sv.ViewportWidth / 2.0;
            double cy = sv.ViewportHeight / 2.0;

            double contentX = (sv.HorizontalOffset + cx) / oldZoom;
            double contentY = (sv.VerticalOffset + cy) / oldZoom;

            ZoomSlider.Value = newZoom;
            sv.UpdateLayout();

            sv.ScrollToHorizontalOffset(contentX * newZoom - cx);
            sv.ScrollToVerticalOffset(contentY * newZoom - cy);
        }

        /// <summary>
        /// Snaps a zoom value to the slider's tick grid, ensuring at least one tick
        /// of movement in the intended <paramref name="direction"/> (positive = zoom in).
        /// </summary>
        private double SnapToTick(double value, double direction)
        {
            double tick = ZoomSlider.TickFrequency;
            long ticks = (long)Math.Round(value / tick);
            double snapped = ticks * tick;

            // If rounding collapsed the step, force one tick in the intended direction
            double current = ZoomSlider.Value;
            if (Math.Abs(snapped - current) < 0.001)
                snapped = direction > 0 ? current + tick : current - tick;

            return Math.Clamp(snapped, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }

        /// <summary>Locates the ScrollViewer that wraps the canvas.</summary>
        private ScrollViewer? FindScrollViewer()
        {
            // The ScrollViewer is the direct child of the canvas panel Border
            return CanvasImage != null ? FindParent<ScrollViewer>(CanvasImage) : null;
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool isPanGesture = e.ChangedButton == MouseButton.Middle ||
                               (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space));

            var sv = (ScrollViewer)sender;

            if (isPanGesture)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(this);
                _panStartScroll = new Point(sv.HorizontalOffset, sv.VerticalOffset);
                sv.CaptureMouse();
                sv.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            // Ignore scrollbar clicks
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = VisualTreeHelper.GetParent(dep);
                while (parent != null)
                {
                    if (parent is System.Windows.Controls.Primitives.ScrollBar)
                        return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not Image)
            {
                // Clicked outside the canvas in the empty viewer space
                if (_selection.HasActiveSelection)
                {
                    CommitCurrentSelection();
                }
            }

            // Start drawing or selecting!
            var image = CanvasImage;
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
            {
                image.CaptureMouse();

                var (x, y) = GetPixelCoordinates(e.GetPosition(image), image.ActualWidth, image.ActualHeight);

                if (ViewModel.CurrentTool == ToolMode.Marquee ||
                    ViewModel.CurrentTool == ToolMode.Lasso)
                {
                    // Clamp for selection tools which require in-bounds coordinates
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionDown(e, cx, cy);
                    return;
                }

                _activeDrawMode = e.ChangedButton == MouseButton.Left ? DrawMode.Draw
                               : e.ChangedButton == MouseButton.Right ? DrawMode.Erase
                               : DrawMode.None;

                if (_activeDrawMode != DrawMode.None)
                {
                    _lastHoveredX = x;
                    _lastHoveredY = y;
                    ViewModel.ProcessToolInput(x, y, ToolAction.Down, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                }
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var sv = (ScrollViewer)sender;
                var delta = e.GetPosition(this) - _panStartMouse;
                sv.ScrollToHorizontalOffset(_panStartScroll.X - delta.X);
                sv.ScrollToVerticalOffset(_panStartScroll.Y - delta.Y);
                e.Handled = true;
                HideBrushCursor();
                return;
            }

            var image = CanvasImage;
            var pos = e.GetPosition(image);
            var (x, y) = GetPixelCoordinates(pos, image.ActualWidth, image.ActualHeight);

            // Track mouse position for brush cursor refresh on size change
            _lastCanvasMousePos = pos;
            _isMouseOverCanvas = pos.X >= 0 && pos.X <= image.ActualWidth &&
                                 pos.Y >= 0 && pos.Y <= image.ActualHeight;

            ViewModel.CursorX = x;
            ViewModel.CursorY = y;

            // Always update the brush cursor position (even if pixel didn't change)
            UpdateBrushCursor(x, y, pos, image.ActualWidth, image.ActualHeight);

            if (x != _lastHoveredX || y != _lastHoveredY)
            {
                _lastHoveredX = x;
                _lastHoveredY = y;

                if ((ViewModel.CurrentTool == ToolMode.Marquee ||
                     ViewModel.CurrentTool == ToolMode.Lasso)
                    && e.LeftButton == MouseButtonState.Pressed)
                {
                    // Clamp for selection tools which require in-bounds coordinates
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionMove(cx, cy);
                    return;
                }

                // Use the draw mode captured on mouse-down so that pressing
                // the other button mid-stroke cannot flip between draw/erase.
                var mode = _activeDrawMode;

                if (mode != DrawMode.None || ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
                {
                    ViewModel.ProcessToolInput(x, y, ToolAction.Move, mode, false);
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            var sv = (ScrollViewer)sender;
            _isPanning = false;
            sv.ReleaseMouseCapture();
            sv.Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        // ── Status message ────────────────────────────────────────────────

        private void ShowStatus(string message)
        {
            if (StatusLabel == null) return;
            StatusLabel.Content = message;
            StatusLabel.Visibility = Visibility.Visible;

            _statusTimer?.Stop();
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                StatusLabel.Visibility = Visibility.Hidden;
                _statusTimer.Stop();
            };
            _statusTimer.Start();
        }

        // ── Canvas size input helpers ─────────────────────────────────────

        private void CanvasInput_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Highlight existing text rather than deleting it, so a mis-click doesn't
            // instantly wipe the value.
            if (sender is TextBox tb) tb.SelectAll();
        }

        private void CanvasInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                tb.Focus();
            }
        }

        private void CanvasInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            if (!int.TryParse(ViewModel.InputWidth, out int w) || w <= 0)
                ViewModel.InputWidth = (ViewModel.SpriteState?.Width ?? 16).ToString();
            if (!int.TryParse(ViewModel.InputHeight, out int h) || h <= 0)
                ViewModel.InputHeight = (ViewModel.SpriteState?.Height ?? 16).ToString();
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9]+");
        }

        private void TextBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)) &&
                Regex.IsMatch((string)e.DataObject.GetData(typeof(string))!, "[^0-9]+"))
                e.CancelCommand();
            else if (!e.DataObject.GetDataPresent(typeof(string)))
                e.CancelCommand();
        }

        // ── Canvas input: Enter key commits ─────────────────────────────

        private void CanvasInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            // Move focus away so LostFocus validation fires, then execute resize
            Keyboard.ClearFocus();
            if (ViewModel?.ResizeCanvasCommand?.CanExecute(null) == true)
                ViewModel.ResizeCanvasCommand.Execute(null);

            e.Handled = true;
        }

        // ── Anchor grid handler ──────────────────────────────────────────

        private void AnchorButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string tagStr) return;
            if (ViewModel == null) return;

            if (Enum.TryParse<Hexel.Core.ResizeAnchor>(tagStr, out var anchor))
                ViewModel.ResizeAnchor = anchor;
        }

        // ── Utility ───────────────────────────────────────────────────────

        private (int x, int y) GetPixelCoordinates(Point pos, double actualWidth, double actualHeight)
        {
            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;
            if (actualWidth == 0 || actualHeight == 0) return (0, 0);

            int x = (int)Math.Floor(pos.X / actualWidth * w);
            int y = (int)Math.Floor(pos.Y / actualHeight * h);

            // When the mouse is inside the canvas area, clamp to valid pixel range
            // (the right/bottom edge maps to w/h which is one past the last pixel).
            // When truly outside, leave unclamped so shape tools can extend past edges.
            if (pos.X >= 0 && pos.X <= actualWidth) x = Math.Clamp(x, 0, w - 1);
            if (pos.Y >= 0 && pos.Y <= actualHeight) y = Math.Clamp(y, 0, h - 1);

            return (x, y);
        }

        // ── Brush cursor overlay ───────────────────────────────────────────

        /// <summary>
        /// Rebuilds the brush stamp bitmap when brush size changes.
        /// Replicates the same circular pattern as DrawBrushStamp so the
        /// preview is pixel-accurate.
        /// </summary>
        private void RebuildBrushCursorBitmap(int brushSize)
        {
            if (brushSize == _brushCursorCachedSize && _brushCursorBitmap != null) return;
            _brushCursorCachedSize = brushSize;

            _brushCursorBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                brushSize, brushSize, 96, 96, PixelFormats.Bgra32, null);

            var pixels = new uint[brushSize * brushSize];

            // Edge color: bright white outline. Fill: semi-transparent.
            const uint edgeColor = 0xDDFFFFFF; // ~87% opaque white
            const uint fillColor = 0x40FFFFFF; // ~25% opaque white

            if (brushSize <= 1)
            {
                pixels[0] = edgeColor;
            }
            else
            {
                int offset = (brushSize - 1) / 2;
                int rSq = brushSize * brushSize / 4;

                // First pass: mark which pixels are in the stamp
                var inStamp = new bool[brushSize * brushSize];
                for (int dy = -offset; dy < brushSize - offset; dy++)
                {
                    for (int dx = -offset; dx < brushSize - offset; dx++)
                    {
                        if (dx * dx + dy * dy > rSq) continue;
                        int px = dx + offset, py = dy + offset;
                        inStamp[py * brushSize + px] = true;
                    }
                }

                // Second pass: fill interior, outline edges
                for (int py = 0; py < brushSize; py++)
                {
                    for (int px = 0; px < brushSize; px++)
                    {
                        if (!inStamp[py * brushSize + px]) continue;

                        // Check if any neighbor is outside the stamp → edge pixel
                        bool isEdge = px == 0 || py == 0 || px == brushSize - 1 || py == brushSize - 1
                            || !inStamp[py * brushSize + (px - 1)]
                            || !inStamp[py * brushSize + (px + 1)]
                            || !inStamp[(py - 1) * brushSize + px]
                            || !inStamp[(py + 1) * brushSize + px];

                        pixels[py * brushSize + px] = isEdge ? edgeColor : fillColor;
                    }
                }
            }

            _brushCursorBitmap.WritePixels(
                new Int32Rect(0, 0, brushSize, brushSize), pixels, brushSize * 4, 0);
            BrushCursorOverlay.Source = _brushCursorBitmap;
        }

        /// <summary>
        /// Called when BrushSize changes (keyboard, slider, etc.) to instantly
        /// refresh the cursor overlay using the last known mouse position.
        /// </summary>
        private void RefreshBrushCursor()
        {
            if (!_isMouseOverCanvas || BrushCursorOverlay == null) return;

            var image = CanvasImage;
            if (image == null || image.ActualWidth == 0) return;

            // Invalidate the bitmap cache so it rebuilds with the new size
            _brushCursorCachedSize = -1;

            var (x, y) = GetPixelCoordinates(_lastCanvasMousePos, image.ActualWidth, image.ActualHeight);
            UpdateBrushCursor(x, y, _lastCanvasMousePos, image.ActualWidth, image.ActualHeight);
        }

        private void UpdateBrushCursor(int pixelX, int pixelY, Point mousePos, double imgWidth, double imgHeight)
        {
            if (BrushCursorOverlay == null) return;

            if (ViewModel.CurrentTool != ToolMode.Pencil)
            {
                HideBrushCursor();
                return;
            }

            int brushSize = ViewModel.BrushSize;
            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / w;
            double ch = gh / h;

            // Shrink factor: the bitmap represents the full stamp footprint but we
            // draw it slightly smaller so the outline sits inside the pixel cell,
            // not straddling the grid line of the neighbouring cell.
            const double shrink = 0.85;
            double cursorW = brushSize * cw * shrink;
            double cursorH = brushSize * ch * shrink;

            // Hide only when the entire stamp would be outside the canvas.
            // Half-widths in pixel-space for the clamped overlap check:
            double halfW = cursorW / 2.0;
            double halfH = cursorH / 2.0;

            bool completelyOutside =
                mousePos.X + halfW < 0 || mousePos.X - halfW > imgWidth ||
                mousePos.Y + halfH < 0 || mousePos.Y - halfH > imgHeight;

            if (completelyOutside)
            {
                HideBrushCursor();
                return;
            }

            RebuildBrushCursorBitmap(brushSize);

            BrushCursorOverlay.Width = cursorW;
            BrushCursorOverlay.Height = cursorH;

            // Follow the mouse freely — the overlay Canvas clips anything outside its bounds.
            double left = mousePos.X - halfW;
            double top  = mousePos.Y - halfH;

            Canvas.SetLeft(BrushCursorOverlay, left);
            Canvas.SetTop(BrushCursorOverlay, top);
            BrushCursorOverlay.Visibility = Visibility.Visible;

            // Crosshair at exact mouse position
            double crossLen = Math.Max(cw, 6.0);
            CrosshairH.X1 = mousePos.X - crossLen;
            CrosshairH.X2 = mousePos.X + crossLen;
            CrosshairH.Y1 = mousePos.Y;
            CrosshairH.Y2 = mousePos.Y;
            CrosshairV.X1 = mousePos.X;
            CrosshairV.X2 = mousePos.X;
            CrosshairV.Y1 = mousePos.Y - crossLen;
            CrosshairV.Y2 = mousePos.Y + crossLen;
            CrosshairH.Visibility = Visibility.Visible;
            CrosshairV.Visibility = Visibility.Visible;

            if (CanvasImage.Cursor != Cursors.None)
                CanvasImage.Cursor = Cursors.None;
        }

        private void HideBrushCursor()
        {
            if (BrushCursorOverlay != null)
                BrushCursorOverlay.Visibility = Visibility.Hidden;
            if (CrosshairH != null)
                CrosshairH.Visibility = Visibility.Hidden;
            if (CrosshairV != null)
                CrosshairV.Visibility = Visibility.Hidden;

            _isMouseOverCanvas = false;

            if (CanvasImage != null && CanvasImage.Cursor == Cursors.None)
                CanvasImage.Cursor = null;
        }

        private void CanvasImage_MouseLeave(object sender, MouseEventArgs e)
        {
            HideBrushCursor();
        }
    }
}
