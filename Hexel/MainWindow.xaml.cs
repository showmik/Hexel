using Hexel.Core;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Collections.Generic;

namespace Hexel
{
    public partial class MainWindow : Window
    {
        #region Properties & Fields

        private ViewModels.MainViewModel ViewModel => (ViewModels.MainViewModel)DataContext;

        // --- Selection & Floating State ---
        private bool _hasActiveSelection;
        private bool _isSelecting;
        private bool _isFloating;
        private bool _isDraggingSelection;

        private int _selectionStartIdx = -1;
        private int _selectionEndIdx = -1;
        private int _selMinX = -1, _selMaxX = -1, _selMinY = -1, _selMaxY = -1;

        private List<Point> _lassoPoints = new List<Point>();
        private int _lassoOriginalMinX = -1;
        private int _lassoOriginalMinY = -1;

        private bool[,] _floatingPixels;
        private int _floatingX, _floatingY, _floatingWidth, _floatingHeight;

        private Point _dragStartMousePos;
        private int _dragStartFloatingX, _dragStartFloatingY;

        // --- Panning State ---
        private Point _panStartMouse;
        private Point _panStartScroll;
        private bool _isPanning;

        // --- Brushes ---
        private SolidColorBrush _colorOn => (SolidColorBrush)Application.Current.Resources["Theme.PrimaryAccentBrush"];
        private SolidColorBrush _colorOff => (SolidColorBrush)Application.Current.Resources["Theme.PanelBackgroundBrush"];
        private SolidColorBrush _previewOn => (SolidColorBrush)Application.Current.Resources["Theme.OledOnBrush"];
        private SolidColorBrush _previewOff => (SolidColorBrush)Application.Current.Resources["Theme.OledOffBrush"];

        #endregion

        #region Constructor & Initialization

        public MainWindow(ViewModels.MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));

            // Listen for Undo/Redo jumps to drop any view-specific selection states
            vm.HistoryRestored += (s, e) =>
            {
                if (_hasActiveSelection || _isFloating)
                {
                    _hasActiveSelection = false;
                    _isSelecting = false;
                    _isFloating = false;
                    _isDraggingSelection = false;
                    _floatingPixels = null;
                    ClearSelectionVisuals();

                    // Release mouse capture if we were dragging a selection when Undo was pressed
                    if (Mouse.Captured == PixelGridContainer)
                    {
                        Mouse.Capture(null);
                    }
                }
            };
        }

        #endregion

        #region UI Controls & Toolbar

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null && ViewModel != null)
            {
                string tag = rb.Tag.ToString();
                ViewModel.CurrentTool = tag == "Fill" ? ToolMode.Fill :
                                        tag == "Marquee" ? ToolMode.Marquee :
                                        tag == "Lasso" ? ToolMode.Lasso :
                                        tag == "Rectangle" ? ToolMode.Rectangle :
                                        tag == "Ellipse" ? ToolMode.Ellipse :
                                        tag == "Line" ? ToolMode.Line : ToolMode.Pencil;

                if (ViewModel.CurrentTool != ToolMode.Marquee && ViewModel.CurrentTool != ToolMode.Lasso)
                {
                    CommitSelection();
                    ClearSelectionVisuals();
                }
            }
        }

        #endregion

        #region Core Canvas Interaction

        private void CanvasImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var image = (Image)sender;
            Point pos = e.GetPosition(image);
            int index = GetIndexFromMousePosition(pos, image.ActualWidth, image.ActualHeight);

            if (ViewModel.CurrentTool == ToolMode.Marquee || ViewModel.CurrentTool == ToolMode.Lasso)
            {
                HandleSelectionTool(e, index);
                return;
            }

            bool? drawState = null;
            if (e.LeftButton == MouseButtonState.Pressed) drawState = true;
            else if (e.RightButton == MouseButtonState.Pressed) drawState = false;

            bool isShiftDown = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ViewModel.CurrentTool == ToolMode.Line)
            {
                Mouse.Capture(image);
            }

            _lastHoveredIndex = index;
            ViewModel.ProcessToolInput(index, "Down", drawState, isShiftDown);
        }

        private void CanvasImage_MouseMove(object sender, MouseEventArgs e)
        {
            var image = (Image)sender;
            Point pos = e.GetPosition(image);
            int index = GetIndexFromMousePosition(pos, image.ActualWidth, image.ActualHeight);

            if (index != _lastHoveredIndex)
            {
                _lastHoveredIndex = index;
                if ((ViewModel.CurrentTool == ToolMode.Marquee || ViewModel.CurrentTool == ToolMode.Lasso) && Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    HandleSelectionTool(e, index);
                    return;
                }

                bool? drawState = null;
                if (e.LeftButton == MouseButtonState.Pressed) drawState = true;
                else if (e.RightButton == MouseButtonState.Pressed) drawState = false;

                bool isShiftDown = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                if (drawState.HasValue)
                {
                    ViewModel.ProcessToolInput(index, "Enter", drawState, isShiftDown);
                }
            }
        }

        private int _lastHoveredIndex = -1; // Prevents spamming the viewmodel on the same pixel

        private int GetIndexFromMousePosition(Point pos, double actualWidth, double actualHeight)
        {
            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;
            if (actualWidth == 0 || actualHeight == 0) return 0;

            int x = (int)(pos.X / actualWidth * w);
            int y = (int)(pos.Y / actualHeight * h);

            x = Math.Max(0, Math.Min(w - 1, x));
            y = Math.Max(0, Math.Min(h - 1, y));

            return (y * w) + x;
        }

        #endregion

        #region Marquee Tool & Selection Logic

        private void HandleSelectionTool(MouseEventArgs e, int index)
        {
            int w = ViewModel.SpriteState.Width;
            int x = index % w;
            int y = index / w;

            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                if (e is MouseButtonEventArgs)
                {
                    bool clickingInside = false;
                    if (_hasActiveSelection && x >= _selMinX && x <= _selMaxX && y >= _selMinY && y <= _selMaxY)
                    {
                        if (ViewModel.CurrentTool == ToolMode.Marquee)
                        {
                            clickingInside = true;
                        }
                        else if (ViewModel.CurrentTool == ToolMode.Lasso && IsPointInPolygon(new Point(x, y), _lassoPoints))
                        {
                            clickingInside = true;
                        }
                    }

                    if (clickingInside)
                    {
                        ViewModel.SaveStateForUndo();
                        LiftSelection();
                        _isDraggingSelection = true;
                        _dragStartMousePos = e.GetPosition(PixelGridContainer);
                        _dragStartFloatingX = _floatingX;
                        _dragStartFloatingY = _floatingY;

                        if (ViewModel.CurrentTool == ToolMode.Lasso)
                        {
                            _lassoOriginalMinX = _selMinX;
                            _lassoOriginalMinY = _selMinY;
                        }

                        Mouse.Capture(PixelGridContainer);
                        return;
                    }

                    CommitSelection();
                    _isSelecting = true;
                    _selectionStartIdx = index;
                    _selectionEndIdx = index;
                    _selMinX = _selMaxX = x;
                    _selMinY = _selMaxY = y;

                    if (ViewModel.CurrentTool == ToolMode.Lasso)
                    {
                        _lassoPoints.Clear();
                        _lassoPoints.Add(new Point(x, y));
                        UpdateLassoVisuals();
                    }
                    else
                    {
                        UpdateSelectionVisualsFromBounds();
                    }
                }
                else if (_isSelecting)
                {
                    if (ViewModel.CurrentTool == ToolMode.Lasso)
                    {
                        var lastPt = _lassoPoints[_lassoPoints.Count - 1];
                        if (lastPt.X != x || lastPt.Y != y)
                        {
                            _lassoPoints.Add(new Point(x, y));
                            _selMinX = Math.Min(_selMinX, x);
                            _selMaxX = Math.Max(_selMaxX, x);
                            _selMinY = Math.Min(_selMinY, y);
                            _selMaxY = Math.Max(_selMaxY, y);
                            UpdateLassoVisuals();
                        }
                    }
                    else
                    {
                        _selectionEndIdx = index;
                        _selMinX = Math.Min(_selectionStartIdx % w, x);
                        _selMaxX = Math.Max(_selectionStartIdx % w, x);
                        _selMinY = Math.Min(_selectionStartIdx / w, y);
                        _selMaxY = Math.Max(_selectionStartIdx / w, y);
                        UpdateSelectionVisualsFromBounds();
                    }
                }
            }
        }

        private void UpdateLassoVisuals()
        {
            if (LassoOverlay == null || _lassoPoints.Count == 0) return;

            double gridWidth = (PixelGridContainer != null && PixelGridContainer.ActualWidth > 0) ? PixelGridContainer.ActualWidth : 400.0;
            double gridHeight = (PixelGridContainer != null && PixelGridContainer.ActualHeight > 0) ? PixelGridContainer.ActualHeight : 400.0;
            double cellWidth = gridWidth / ViewModel.SpriteState.Width;
            double cellHeight = gridHeight / ViewModel.SpriteState.Height;

            int dx = 0;
            int dy = 0;
            if (_isDraggingSelection)
            {
                dx = _floatingX - _lassoOriginalMinX;
                dy = _floatingY - _lassoOriginalMinY;
            }

            bool[,] mask = null;
            int w, h, currentMinX, currentMinY;

            if (_isDraggingSelection && _floatingPixels != null)
            {
                mask = _floatingPixels;
                w = _floatingWidth;
                h = _floatingHeight;
                currentMinX = _lassoOriginalMinX;
                currentMinY = _lassoOriginalMinY;
            }
            else
            {
                w = (_selMaxX - _selMinX) + 1;
                h = (_selMaxY - _selMinY) + 1;
                mask = new bool[w, h];
                for (int y = _selMinY; y <= _selMaxY; y++)
                {
                    for (int x = _selMinX; x <= _selMaxX; x++)
                    {
                        mask[x - _selMinX, y - _selMinY] = IsPointInPolygon(new Point(x, y), _lassoPoints);
                    }
                }
                ViewModel.SetSelectionBounds(true, _selMinX, _selMaxX, _selMinY, _selMaxY, mask);
                _hasActiveSelection = true;
                currentMinX = _selMinX;
                currentMinY = _selMinY;
            }

            Geometry combined = null;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (mask[x, y])
                    {
                        double rectX = (x + currentMinX + dx) * cellWidth;
                        double rectY = (y + currentMinY + dy) * cellHeight;
                        var rectGeom = new RectangleGeometry(new Rect(rectX, rectY, cellWidth, cellHeight));

                        if (combined == null)
                            combined = rectGeom;
                        else
                            combined = Geometry.Combine(combined, rectGeom, GeometryCombineMode.Union, null);
                    }
                }
            }

            LassoOverlay.Data = combined;
            LassoOverlay.Visibility = Visibility.Visible;
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
        }

        private void UpdateSelectionVisualsFromBounds()
        {
            if (MarqueeOverlay == null) return;
            double gridWidth = (PixelGridContainer != null && PixelGridContainer.ActualWidth > 0) ? PixelGridContainer.ActualWidth : 400.0;
            double gridHeight = (PixelGridContainer != null && PixelGridContainer.ActualHeight > 0) ? PixelGridContainer.ActualHeight : 400.0;
            double cellWidth = gridWidth / ViewModel.SpriteState.Width;
            double cellHeight = gridHeight / ViewModel.SpriteState.Height;

            Canvas.SetLeft(MarqueeOverlay, _selMinX * cellWidth);
            Canvas.SetTop(MarqueeOverlay, _selMinY * cellHeight);
            MarqueeOverlay.Width = ((_selMaxX - _selMinX) + 1) * cellWidth;
            MarqueeOverlay.Height = ((_selMaxY - _selMinY) + 1) * cellHeight;
            System.Windows.Controls.Panel.SetZIndex(MarqueeOverlay, 100);

            MarqueeOverlay.Visibility = Visibility.Visible;
            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;

            _hasActiveSelection = true;
            ViewModel.SetSelectionBounds(true, _selMinX, _selMaxX, _selMinY, _selMaxY, null);
        }

        private void ClearSelectionVisuals()
        {
            _isSelecting = false;
            _selectionStartIdx = -1;
            _selectionEndIdx = -1;
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;
            _lassoPoints.Clear();
            ViewModel.SetSelectionBounds(false, -1, -1, -1, -1, null);
        }

        private void LiftSelection()
        {
            if (!_hasActiveSelection || _isFloating) return;

            int w = ViewModel.SpriteState.Width;
            _floatingWidth = (_selMaxX - _selMinX) + 1;
            _floatingHeight = (_selMaxY - _selMinY) + 1;
            _floatingX = _selMinX;
            _floatingY = _selMinY;

            _floatingPixels = new bool[_floatingWidth, _floatingHeight];

            for (int y = _selMinY; y <= _selMaxY; y++)
            {
                for (int x = _selMinX; x <= _selMaxX; x++)
                {
                    bool includePixel = ViewModel.CurrentTool == ToolMode.Marquee ||
                                        (ViewModel.CurrentTool == ToolMode.Lasso && IsPointInPolygon(new Point(x, y), _lassoPoints));

                    if (includePixel)
                    {
                        int idx = (y * w) + x;
                        if (ViewModel.SpriteState.Pixels[idx])
                        {
                            _floatingPixels[x - _selMinX, y - _selMinY] = true;
                            ViewModel.SpriteState.Pixels[idx] = false;
                        }
                    }
                }
            }

            _isFloating = true;
            ViewModel.SyncFloatingState(_isFloating, _floatingPixels, _floatingX, _floatingY, _floatingWidth, _floatingHeight);
        }

        private void CommitSelection()
        {
            if (!_hasActiveSelection) return;

            if (_isFloating && _floatingPixels != null)
            {
                int w = ViewModel.SpriteState.Width;
                int h = ViewModel.SpriteState.Height;
                for (int y = 0; y < _floatingHeight; y++)
                {
                    for (int x = 0; x < _floatingWidth; x++)
                    {
                        if (_floatingPixels[x, y])
                        {
                            int gridX = _floatingX + x;
                            int gridY = _floatingY + y;

                            if (gridX >= 0 && gridX < w && gridY >= 0 && gridY < h)
                            {
                                ViewModel.SpriteState.Pixels[(gridY * w) + gridX] = true;
                            }
                        }
                    }
                }

                _isFloating = false;
                _floatingPixels = null;
            }

            _hasActiveSelection = false;
            _selMinX = _selMaxX = _selMinY = _selMaxY = -1;

            ViewModel.SyncFloatingState(false, null, 0, 0, 0, 0);
            ClearSelectionVisuals();
            ViewModel.RedrawGridFromMemory();
            ViewModel.UpdateTextOutputs();
        }
        #endregion

        #region Zoom & Panning Logic

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.2);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.2);

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 1.0;

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Always prevent the default vertical scrolling behavior
            e.Handled = true;

            // Apply the zoom
            double zoomChange = e.Delta > 0 ? 0.2 : -0.2;
            ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + zoomChange));
        }

        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Trigger pan if Middle-Clicked OR if holding Spacebar + Left-Click (Adobe style)
            if (e.ChangedButton == MouseButton.Middle ||
               (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
            {
                var scrollViewer = (ScrollViewer)sender;
                _isPanning = true;

                // Track mouse relative to the window so the math doesn't jitter when the scrollviewer moves
                _panStartMouse = e.GetPosition(this);
                _panStartScroll = new Point(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);

                scrollViewer.CaptureMouse();
                scrollViewer.Cursor = Cursors.SizeAll; // Gives visual feedback that you are grabbing the canvas
                e.Handled = true; // Crucial: Prevents the pencil tool from drawing while panning!
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var scrollViewer = (ScrollViewer)sender;
                Point currentMouse = e.GetPosition(this);
                Vector delta = currentMouse - _panStartMouse;

                // Move the scrollbars exactly opposite to the mouse movement
                scrollViewer.ScrollToHorizontalOffset(_panStartScroll.X - delta.X);
                scrollViewer.ScrollToVerticalOffset(_panStartScroll.Y - delta.Y);
                e.Handled = true;
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
            {
                var scrollViewer = (ScrollViewer)sender;
                _isPanning = false;
                scrollViewer.ReleaseMouseCapture();
                scrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        #endregion

        #region Global Input Overrides (Keys & Marquee Dragging)

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
            {
                Point pos = e.GetPosition(CanvasImage);
                int hoverIndex = GetIndexFromMousePosition(pos, CanvasImage.ActualWidth, CanvasImage.ActualHeight);
                ViewModel.ProcessToolInput(hoverIndex, "Enter", null, false);
            }

            if (_isDraggingSelection && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(PixelGridContainer);
                double deltaX = currentPos.X - _dragStartMousePos.X;
                double deltaY = currentPos.Y - _dragStartMousePos.Y;
                double gridWidth = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
                double gridHeight = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
                double cellWidth = gridWidth / ViewModel.SpriteState.Width;
                double cellHeight = gridHeight / ViewModel.SpriteState.Height;

                int cellsMovedX = (int)Math.Round(deltaX / cellWidth);
                int cellsMovedY = (int)Math.Round(deltaY / cellHeight);
                int newX = _dragStartFloatingX + cellsMovedX;
                int newY = _dragStartFloatingY + cellsMovedY;

                if (newX != _floatingX || newY != _floatingY)
                {
                    _floatingX = newX;
                    _floatingY = newY;
                    _selMinX = _floatingX;
                    _selMaxX = _floatingX + _floatingWidth - 1;
                    _selMinY = _floatingY;
                    _selMaxY = _floatingY + _floatingHeight - 1;

                    if (ViewModel.CurrentTool == ToolMode.Lasso)
                        UpdateLassoVisuals();
                    else
                        UpdateSelectionVisualsFromBounds();

                    ViewModel.SyncFloatingState(_isFloating, _floatingPixels, _floatingX, _floatingY, _floatingWidth, _floatingHeight);
                    ViewModel.RedrawGridFromMemory();
                }
            }
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);

            // --- UPDATED TOOL COMMIT ---
            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
            {
                ViewModel.ProcessToolInput(-1, "Up", null, false);
                Mouse.Capture(null); // Release the mouse lock
            }
            else if (ViewModel.CurrentTool == ToolMode.Pencil)
            {
                ViewModel.ProcessToolInput(-1, "Up", null, false);
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                // Release the mouse capture if we were holding it
                if (Mouse.Captured == PixelGridContainer)
                {
                    Mouse.Capture(null);
                }

                _isSelecting = false;
                _isDraggingSelection = false;
                ViewModel.RedrawGridFromMemory();
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // 1. Catch Ctrl+Arrow keys BEFORE the ScrollViewer swallows them
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Up) { ViewModel.ShiftGrid(0, -1); e.Handled = true; }
                else if (e.Key == Key.Down) { ViewModel.ShiftGrid(0, 1); e.Handled = true; }
                else if (e.Key == Key.Left) { ViewModel.ShiftGrid(-1, 0); e.Handled = true; }
                else if (e.Key == Key.Right) { ViewModel.ShiftGrid(1, 0); e.Handled = true; }

                return; // Exit early
            }

            // 2. Handle raw letter keys for tools (only if not typing in a text box)
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Keyboard.FocusedElement is TextBox) return;

                if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    if (ViewModel.DeleteSelectionCommand.CanExecute(null))
                    {
                        // 1. Execute the deletion in the ViewModel
                        ViewModel.DeleteSelectionCommand.Execute(null);

                        // 2. NEW: Destroy the View's local cache of the floating pixels
                        _isFloating = false;
                        _floatingPixels = null;

                        _hasActiveSelection = false;
                        ClearSelectionVisuals();

                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.M) { if (RbMarquee != null) RbMarquee.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.S) { if (RbLasso != null) RbLasso.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.P) { if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.F) { if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.L) { if (RbLine != null) RbLine.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.R) { if (RbRectangle != null) RbRectangle.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.E) { if (RbEllipse != null) RbEllipse.IsChecked = true; e.Handled = true; }
            }
        }

        #endregion

        private bool IsPointInPolygon(Point p, List<Point> polygon)
        {
            if (polygon == null || polygon.Count == 0) return false;
            if (polygon.Count == 1) return p.X == polygon[0].X && p.Y == polygon[0].Y;
            if (polygon.Count == 2) return (p.X == polygon[0].X && p.Y == polygon[0].Y) || (p.X == polygon[1].X && p.Y == polygon[1].Y);

            // If the mouse hovered exactly over a vertex, guarantee it's counted
            foreach (var v in polygon)
            {
                if (v.X == p.X && v.Y == p.Y) return true;
            }

            bool isInside = false;
            Point testPoint = new Point(p.X + 0.01, p.Y + 0.01); // Offset to avoid boundary line overlaps

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                if (((polygon[i].Y > testPoint.Y) != (polygon[j].Y > testPoint.Y)) &&
                    (testPoint.X < (polygon[j].X - polygon[i].X) * (testPoint.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                {
                    isInside = !isInside;
                }
            }
            return isInside;
        }
    }
}