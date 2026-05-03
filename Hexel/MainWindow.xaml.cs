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
        private readonly ViewModels.ShellViewModel _shell;
        private ViewModels.MainViewModel? ViewModel => _shell.ActiveDocument;
        private ISelectionService? _selection => ViewModel?.SelectionService;

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
        private Core.BrushShape _brushCursorCachedShape = (Core.BrushShape)(-1);
        private int _brushCursorCachedAngle = -1;
        private Point _lastCanvasMousePos;
        private bool _isMouseOverCanvas;

        // ── Currently-subscribed document (for event unwiring) ─────────────
        private ViewModels.MainViewModel? _subscribedDoc;

        // ── Constructor ───────────────────────────────────────────────────

        public MainWindow(ViewModels.ShellViewModel shell)
        {
            InitializeComponent();
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            DataContext = _shell;

            // Wire up tab change
            _shell.ActiveTabChanged += (_, _) => OnActiveTabChanged();

            // Zoom keyboard shortcuts: Ctrl+Plus, Ctrl+Minus, Ctrl+0
            CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom,
                (_, _) => ApplyZoomCentered(ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom,
                (_, _) => ApplyZoomCentered(1.0 / ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.Zoom,
                (_, _) => ZoomSlider.Value = 1.0));

            // Wire the initial document
            OnActiveTabChanged();
        }

        private void OnActiveTabChanged()
        {
            // Unwire previous document
            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored -= OnHistoryRestored;
                _subscribedDoc.CopyHexExecuted -= OnCopyHexExecuted;
                _subscribedDoc.PropertyChanged -= OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged -= OnSelectionChanged;
            }

            _subscribedDoc = ViewModel;

            // Wire new document
            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored += OnHistoryRestored;
                _subscribedDoc.CopyHexExecuted += OnCopyHexExecuted;
                _subscribedDoc.PropertyChanged += OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged += OnSelectionChanged;
            }

            ClearSelectionOverlays();
            HideBrushCursor();
            SyncBrushShapeRadioButtons();
        }

        /// <summary>
        /// Syncs the brush shape radio buttons to match the active document's BrushShape.
        /// Prevents desync when switching tabs or opening new documents.
        /// </summary>
        private void SyncBrushShapeRadioButtons()
        {
            if (ViewModel == null) return;

            switch (ViewModel.BrushShape)
            {
                case Core.BrushShape.Circle:
                    if (RbBrushCircle != null) RbBrushCircle.IsChecked = true;
                    break;
                case Core.BrushShape.Square:
                    if (RbBrushSquare != null) RbBrushSquare.IsChecked = true;
                    break;
                case Core.BrushShape.Line:
                    if (RbBrushLine != null) RbBrushLine.IsChecked = true;
                    break;
            }
        }

        private void OnHistoryRestored(object? s, EventArgs e)
        {
            ClearSelectionOverlays();
            ReleaseDragCapture();
        }
        private void OnCopyHexExecuted(object? s, EventArgs e) => ShowStatus("Copied to clipboard");
        private void OnSelectionChanged(object? s, EventArgs e) => UpdateSelectionOverlays();
        private void OnDocPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.BrushSize) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushShape) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushAngle))
                RefreshBrushCursor();
        }

        // ── Tab bar event handlers ────────────────────────────────────────

        private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.MainViewModel doc)
            {
                _shell.ActiveDocument = doc;
            }
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ViewModels.MainViewModel doc)
            {
                _shell.CloseTabCommand.Execute(doc);
            }
        }

        // ── Brush shape selection ─────────────────────────────────────────

        private void BrushShape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is null || ViewModel is null) return;

            ViewModel.BrushShape = rb.Tag.ToString() switch
            {
                "Square" => Core.BrushShape.Square,
                "Line" => Core.BrushShape.Line,
                _ => Core.BrushShape.Circle
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
                "FilledRectangle" => ToolMode.FilledRectangle,
                "FilledEllipse" => ToolMode.FilledEllipse,
                "Line" => ToolMode.Line,
                "MagicWand" => ToolMode.MagicWand,
                _ => ToolMode.Pencil
            };

            // Reset drawing state so the new tool doesn't inherit a stale draw mode
            // from the previous tool (e.g. pencil auto-drawing without mouse pressed).
            _activeDrawMode = DrawMode.None;
            ViewModel.CancelInProgressDrawing();

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

            if (ViewModel == null || _selection == null) return;

            // Update floating layer position during drag
            if (_selection.IsDragging && e.LeftButton == MouseButtonState.Pressed)
                UpdateDragPosition(e.GetPosition(PixelGridContainer));
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);

            if (ViewModel == null || _selection == null) return;

            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            }
            else if (ViewModel.CurrentTool == ToolMode.Pencil)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false, false);
            }

            // Always clear the draw mode on mouse-up so that switching tools
            // (via radio button click or keyboard shortcut) never leaves a
            // stale Draw/Erase mode that would cause the next tool to auto-draw.
            _activeDrawMode = DrawMode.None;

            if (e.ChangedButton == MouseButton.Left)
            {
                // Finalise a selection on mouse-up
                if ((ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.Marquee || ViewModel.CurrentTool == ToolMode.MagicWand) && _selection.IsSelecting)
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

            if (ViewModel == null || _selection == null) return;

            if (e.Key == Key.LeftShift || e.Key == Key.RightShift || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(ViewModel.CursorX, ViewModel.CursorY, ToolAction.Move, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }

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
                    case Key.W: if (RbMagicWand != null) RbMagicWand.IsChecked = true; e.Handled = true; break;

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
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (Keyboard.FocusedElement is TextBox) return;

                switch (e.Key)
                {
                    case Key.R: if (RbFilledRectangle != null) RbFilledRectangle.IsChecked = true; e.Handled = true; break;
                    case Key.E: if (RbFilledEllipse != null) RbFilledEllipse.IsChecked = true; e.Handled = true; break;
                }
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);

            if (ViewModel == null || _selection == null) return;

            if (e.Key == Key.LeftShift || e.Key == Key.RightShift || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(ViewModel.CursorX, ViewModel.CursorY, ToolAction.Move, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }
        }


        // ── Selection tool handling ───────────────────────────────────────

        private int _selectionAnchorX = -1;
        private int _selectionAnchorY = -1;

        private void HandleSelectionDown(MouseButtonEventArgs e, int x, int y)
        {
            Hexel.Core.SelectionMode mode = Hexel.Core.SelectionMode.Replace;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

            if (shift && alt) mode = Hexel.Core.SelectionMode.Intersect;
            else if (shift) mode = Hexel.Core.SelectionMode.Add;
            else if (alt) mode = Hexel.Core.SelectionMode.Subtract;

            if (_selection.HasActiveSelection && _selection.IsPixelInSelection(x, y) && mode == Hexel.Core.SelectionMode.Replace)
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
                return;
            }

            // We are starting a new selection or adding/subtracting from an existing one
            if (mode == Hexel.Core.SelectionMode.Replace)
            {
                CommitCurrentSelection();
            }
            else if (_selection.IsFloating)
            {
                // If adding/subtracting while floating, commit pixels but retain mask
                var oldMask = _selection.Mask;
                var oldMinX = _selection.MinX;
                var oldMinY = _selection.MinY;
                var oldMaxX = _selection.MaxX;
                var oldMaxY = _selection.MaxY;
                CommitCurrentSelection();
                if (oldMinX != -1)
                    _selection.ApplyMask(oldMask, oldMinX, oldMinY, oldMaxX, oldMaxY, Hexel.Core.SelectionMode.Replace);
            }

            if (ViewModel.CurrentTool == ToolMode.MagicWand)
            {
                ViewModel.SaveStateForUndo();
                var fillMask = ViewModel.DrawingService.GetFloodFillMask(ViewModel.SpriteState, x, y, out int minX, out int minY, out int maxX, out int maxY);
                _selection.ApplyMask(fillMask, minX, minY, maxX, maxY, mode);
                ViewModel.RedrawGridFromMemory();
            }
            else
            {
                _selectionAnchorX = x;
                _selectionAnchorY = y;

                if (ViewModel.CurrentTool == ToolMode.Lasso)
                    _selection.BeginLassoSelection(x, y, mode);
                else
                    _selection.BeginRectangleSelection(x, y, mode);
            }
        }

        private void HandleSelectionMove(int x, int y)
        {
            if (!_selection.IsSelecting) return;

            if (ViewModel.CurrentTool == ToolMode.Lasso)
            {
                _selection.AddLassoPoint(x, y);
            }
            else
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchorX != -1 && _selectionAnchorY != -1)
                {
                    int dx = x - _selectionAnchorX;
                    int dy = y - _selectionAnchorY;
                    int maxDist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    x = _selectionAnchorX + Math.Sign(dx) * maxDist;
                    y = _selectionAnchorY + Math.Sign(dy) * maxDist;
                }
                _selection.UpdateRectangleSelection(x, y);
            }
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

            if (_selection.Mask != null || ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.MagicWand)
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
            if (LassoOverlay == null || ViewModel == null) return;

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
            else if (_selection.HasActiveSelection || _selection.IsSelecting)
            {
                mask = _selection.Mask;
                baseX = _selection.MinX;
                baseY = _selection.MinY;
                maskW = _selection.MaxX - _selection.MinX + 1;
                maskH = _selection.MaxY - _selection.MinY + 1;
            }
            else
            {
                mask = null;
                baseX = 0; baseY = 0; maskW = 0; maskH = 0;
            }
            var groupGeom = new GeometryGroup();

            if (mask != null)
            {
                var edgesByStart = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<(int, int)>>();

                System.Action<int, int, int, int> addEdge = (x1, y1, x2, y2) =>
                {
                    if (!edgesByStart.TryGetValue((x1, y1), out var list))
                    {
                        list = new System.Collections.Generic.List<(int, int)>();
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
                ClearSelectionOverlays();
                return;
            }

            LassoOverlay.Data = groupGeom;
            LassoOverlay.Visibility = Visibility.Visible;
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
        { if (ViewModel != null) ViewModel.BrushSize--; }

        private void BtnBrushUp_Click(object sender, RoutedEventArgs e)
        { if (ViewModel != null) ViewModel.BrushSize++; }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl+Scroll = adjust brush size
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (ViewModel != null)
                {
                    ViewModel.BrushSize += e.Delta > 0 ? 1 : -1;
                }
                e.Handled = true;
                return;
            }

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

            if (ViewModel == null || _selection == null) return;

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
                    ViewModel.CurrentTool == ToolMode.Lasso ||
                    ViewModel.CurrentTool == ToolMode.MagicWand)
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
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
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

            if (ViewModel == null || _selection == null) return;

            ViewModel.CursorX = x;
            ViewModel.CursorY = y;

            // Always update the brush cursor position (even if pixel didn't change)
            UpdateBrushCursor(x, y, pos, image.ActualWidth, image.ActualHeight);

            if (x != _lastHoveredX || y != _lastHoveredY)
            {
                _lastHoveredX = x;
                _lastHoveredY = y;

                if ((ViewModel.CurrentTool == ToolMode.Marquee ||
                     ViewModel.CurrentTool == ToolMode.Lasso ||
                     ViewModel.CurrentTool == ToolMode.MagicWand)
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

                if (mode != DrawMode.None || ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(x, y, ToolAction.Move, mode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
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




        // ── Utility ───────────────────────────────────────────────────────

        private (int x, int y) GetPixelCoordinates(Point pos, double actualWidth, double actualHeight)
        {
            if (ViewModel == null) return (0, 0);

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
        /// Rebuilds the brush stamp bitmap when brush size, shape, or angle changes.
        /// Uses the same ComputeStampOffsets as DrawingService for pixel-accurate preview.
        /// </summary>
        private void RebuildBrushCursorBitmap(int brushSize, Core.BrushShape shape, int angleDeg)
        {
            if (brushSize == _brushCursorCachedSize &&
                shape == _brushCursorCachedShape &&
                angleDeg == _brushCursorCachedAngle &&
                _brushCursorBitmap != null) return;

            _brushCursorCachedSize = brushSize;
            _brushCursorCachedShape = shape;
            _brushCursorCachedAngle = angleDeg;

            var offsets = Services.DrawingService.ComputeStampOffsets(brushSize, shape, angleDeg);

            // Compute bounding box of the offsets
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

            _brushCursorBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                bmpW, bmpH, 96, 96, PixelFormats.Bgra32, null);

            var pixels = new uint[bmpW * bmpH];
            const uint edgeColor = 0xDDFFFFFF;
            const uint fillColor = 0x40FFFFFF;

            // Mark stamp pixels
            var inStamp = new bool[bmpW * bmpH];
            foreach (var (dx, dy) in offsets)
            {
                int px = dx - minDx;
                int py = dy - minDy;
                inStamp[py * bmpW + px] = true;
            }

            // Fill interior and outline edges
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

            // Invalidate the bitmap cache so it rebuilds with the new size/shape/angle
            _brushCursorCachedSize = -1;
            _brushCursorCachedShape = (Core.BrushShape)(-1);
            _brushCursorCachedAngle = -1;

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
            var brushShape = ViewModel.BrushShape;
            int brushAngle = ViewModel.BrushAngle;
            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / w;
            double ch = gh / h;

            // Compute the actual bounding box of the stamp from offsets
            var offsets = Services.DrawingService.ComputeStampOffsets(brushSize, brushShape, brushAngle);
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

            // Shrink factor: the bitmap represents the full stamp footprint but we
            // draw it slightly smaller so the outline sits inside the pixel cell,
            // not straddling the grid line of the neighbouring cell.
            const double shrink = 0.85;
            double cursorW = stampW * cw * shrink;
            double cursorH = stampH * ch * shrink;

            // Hide only when the entire stamp would be outside the canvas.
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

            RebuildBrushCursorBitmap(brushSize, brushShape, brushAngle);

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

        // ── Text input validation ─────────────────────────────────────────

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow digits
            e.Handled = !Regex.IsMatch(e.Text, @"^[0-9]+$");
        }

        private void BrushSizeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && ViewModel != null)
            {
                if (int.TryParse(tb.Text, out int val))
                    ViewModel.BrushSize = Math.Clamp(val, 1, 64);
                tb.Text = ViewModel.BrushSize.ToString();
            }
        }

        private void BrushAngleTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && ViewModel != null)
            {
                if (int.TryParse(tb.Text, out int val))
                    ViewModel.BrushAngle = ((val % 360) + 360) % 360;
                tb.Text = ViewModel.BrushAngle.ToString();
            }
        }
    }
}
