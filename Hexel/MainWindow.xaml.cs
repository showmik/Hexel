using Hexel.Core;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

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
                                        tag == "Rectangle" ? ToolMode.Rectangle :
                                        tag == "Ellipse" ? ToolMode.Ellipse :
                                        tag == "Line" ? ToolMode.Line : ToolMode.Pencil;

                if (ViewModel.CurrentTool != ToolMode.Marquee)
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

            if (ViewModel.CurrentTool == ToolMode.Marquee)
            {
                HandleMarqueeTool(e, index);
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

                if (ViewModel.CurrentTool == ToolMode.Marquee && Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    HandleMarqueeTool(e, index);
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
            int size = ViewModel.SpriteState.Size;
            if (actualWidth == 0 || actualHeight == 0) return 0;

            int x = (int)(pos.X / actualWidth * size);
            int y = (int)(pos.Y / actualHeight * size);

            // Clamp coordinates to grid boundaries
            x = Math.Max(0, Math.Min(size - 1, x));
            y = Math.Max(0, Math.Min(size - 1, y));

            return (y * size) + x;
        }

        #endregion

        #region Marquee Tool & Selection Logic

        private void HandleMarqueeTool(MouseEventArgs e, int index)
        {
            int size = ViewModel.SpriteState.Size;
            int x = index % size;
            int y = index / size;

            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                if (e is MouseButtonEventArgs)
                {
                    if (_hasActiveSelection && x >= _selMinX && x <= _selMaxX && y >= _selMinY && y <= _selMaxY)
                    {
                        ViewModel.SaveStateForUndo();
                        LiftSelection();
                        _isDraggingSelection = true;
                        _dragStartMousePos = e.GetPosition(PixelGridContainer);
                        _dragStartFloatingX = _floatingX;
                        _dragStartFloatingY = _floatingY;

                        // NEW: Capture the mouse to ensure we track it if it leaves the window
                        Mouse.Capture(PixelGridContainer);

                        return;
                    }

                    CommitSelection();
                    _isSelecting = true;
                    _selectionStartIdx = index;
                    _selectionEndIdx = index;
                    _selMinX = _selMaxX = x;
                    _selMinY = _selMaxY = y;

                    UpdateSelectionVisualsFromBounds();
                }
                else if (_isSelecting)
                {
                    _selectionEndIdx = index;
                    _selMinX = Math.Min(_selectionStartIdx % size, x);
                    _selMaxX = Math.Max(_selectionStartIdx % size, x);
                    _selMinY = Math.Min(_selectionStartIdx / size, y);
                    _selMaxY = Math.Max(_selectionStartIdx / size, y);

                    UpdateSelectionVisualsFromBounds();
                }
            }
        }

        private void LiftSelection()
        {
            if (!_hasActiveSelection || _isFloating) return;

            int size = ViewModel.SpriteState.Size;
            _floatingWidth = (_selMaxX - _selMinX) + 1;
            _floatingHeight = (_selMaxY - _selMinY) + 1;
            _floatingX = _selMinX;
            _floatingY = _selMinY;
            _floatingPixels = new bool[_floatingWidth, _floatingHeight];

            for (int y = _selMinY; y <= _selMaxY; y++)
            {
                for (int x = _selMinX; x <= _selMaxX; x++)
                {
                    int idx = (y * size) + x;
                    if (ViewModel.SpriteState.Pixels[idx])
                    {
                        _floatingPixels[x - _selMinX, y - _selMinY] = true;
                        ViewModel.SpriteState.Pixels[idx] = false;
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
                int size = ViewModel.SpriteState.Size;
                for (int y = 0; y < _floatingHeight; y++)
                {
                    for (int x = 0; x < _floatingWidth; x++)
                    {
                        if (_floatingPixels[x, y])
                        {
                            int gridX = _floatingX + x;
                            int gridY = _floatingY + y;

                            if (gridX >= 0 && gridX < size && gridY >= 0 && gridY < size)
                            {
                                ViewModel.SpriteState.Pixels[(gridY * size) + gridX] = true;
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

        private void UpdateSelectionVisualsFromBounds()
        {
            if (MarqueeOverlay == null) return;

            double gridWidth = (PixelGridContainer != null && PixelGridContainer.ActualWidth > 0) ? PixelGridContainer.ActualWidth : 400.0;
            double gridHeight = (PixelGridContainer != null && PixelGridContainer.ActualHeight > 0) ? PixelGridContainer.ActualHeight : 400.0;

            double cellWidth = gridWidth / ViewModel.SpriteState.Size;
            double cellHeight = gridHeight / ViewModel.SpriteState.Size;

            Canvas.SetLeft(MarqueeOverlay, _selMinX * cellWidth);
            Canvas.SetTop(MarqueeOverlay, _selMinY * cellHeight);

            MarqueeOverlay.Width = ((_selMaxX - _selMinX) + 1) * cellWidth;
            MarqueeOverlay.Height = ((_selMaxY - _selMinY) + 1) * cellHeight;

            System.Windows.Controls.Panel.SetZIndex(MarqueeOverlay, 100);
            MarqueeOverlay.Visibility = Visibility.Visible;
            _hasActiveSelection = true;

            ViewModel.SetSelectionBounds(true, _selMinX, _selMaxX, _selMinY, _selMaxY);
        }

        private void ClearSelectionVisuals()
        {
            _isSelecting = false;
            _selectionStartIdx = -1;
            _selectionEndIdx = -1;

            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;

            ViewModel.SetSelectionBounds(false, -1, -1, -1, -1);
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

            // --- NEW LINE & RECTANGLE TOOL DRAG TRACKING ---
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

                double cellWidth = gridWidth / ViewModel.SpriteState.Size;
                double cellHeight = gridHeight / ViewModel.SpriteState.Size;

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
                else if (e.Key == Key.P) { if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.F) { if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.L) { if (RbLine != null) RbLine.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.R) { if (RbRectangle != null) RbRectangle.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.E) { if (RbEllipse != null) RbEllipse.IsChecked = true; e.Handled = true; }
            }
        }

        #endregion
    }
}