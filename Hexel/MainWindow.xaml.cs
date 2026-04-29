using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hexel.Core;

namespace Hexel
{
    public partial class MainWindow : Window
    {
        private ViewModels.MainViewModel ViewModel => (ViewModels.MainViewModel)DataContext;

        // UI State
        private int _lastClickedIndex = -1;

        // Selection & Floating State
        private bool _hasActiveSelection, _isSelecting, _isFloating, _isDraggingSelection;
        private bool _pendingTextUpdateDuringDrag = false;
        private int _selectionStartIdx = -1, _selectionEndIdx = -1;
        private int _selMinX = -1, _selMaxX = -1, _selMinY = -1, _selMaxY = -1;
        private bool[,] _floatingPixels;
        private int _floatingX, _floatingY, _floatingWidth, _floatingHeight;
        private Point _dragStartMousePos;
        private int _dragStartFloatingX, _dragStartFloatingY;

        private readonly SolidColorBrush _colorOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));
        private readonly SolidColorBrush _colorOff = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        private readonly SolidColorBrush _previewOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFFF"));
        private readonly SolidColorBrush _previewOff = new SolidColorBrush(Colors.Black);

        public MainWindow(ViewModels.MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));

            // Set the initial visual scale for the OLED preview (2x multiplier)
            PreviewItemsControl.Width = PreviewItemsControl.Height = vm.SpriteState.Size * 2;

            vm.PropertyChanged += Vm_PropertyChanged;
        }

        private void Vm_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.IsDisplayInverted))
            {
                if (ViewModel.IsDisplayInverted)
                {
                    OledBorder.Background = _previewOn;
                    PreviewGridContainer.Background = _previewOn;
                }
                else
                {
                    OledBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#050505"));
                    PreviewGridContainer.Background = new SolidColorBrush(Colors.Black);
                }
            }
        }

        private void CmbGridSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (CmbGridSize.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                int newSize = int.Parse(item.Tag.ToString());
                ViewModel.InitializeGrid(newSize);

                // Maintain the 2x visual scale for the OLED preview
                PreviewItemsControl.Width = PreviewItemsControl.Height = newSize * 2;
            }
        }

        private void Pixel_Interaction(object sender, MouseEventArgs e)
        {
            // The XAML AlternationIndex automatically assigns the correct index to the Tag property
            if (sender is Border cell && cell.Tag is int index)
            {
                if (ViewModel.CurrentTool == ToolMode.Marquee)
                {
                    HandleMarqueeTool(e, index);
                    return;
                }

                if (e is MouseButtonEventArgs)
                {
                    ViewModel.SaveStateForUndo();

                    if (ViewModel.CurrentTool == ToolMode.Fill)
                    {
                        bool newState = Mouse.LeftButton == MouseButtonState.Pressed;
                        if (Mouse.RightButton == MouseButtonState.Pressed) newState = false;
                        ViewModel.ApplyFloodFill(index, newState);
                        _lastClickedIndex = index;
                        return;
                    }

                    if (ViewModel.CurrentTool == ToolMode.Pencil && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClickedIndex != -1)
                    {
                        bool newState = Mouse.LeftButton == MouseButtonState.Pressed;
                        ViewModel.DrawLine(_lastClickedIndex, index, newState);
                        _lastClickedIndex = index;
                        return;
                    }
                }

                if (ViewModel.CurrentTool == ToolMode.Pencil)
                {
                    bool isDrawing = Mouse.LeftButton == MouseButtonState.Pressed;
                    bool isErasing = Mouse.RightButton == MouseButtonState.Pressed;

                    if (isDrawing || isErasing)
                    {
                        bool targetState = isDrawing;

                        if (e is MouseButtonEventArgs)
                        {
                            ViewModel.SetPixel(index, targetState);
                            _lastClickedIndex = index;
                            ViewModel.UpdateTextOutputs();
                        }
                        else
                        {
                            if (_lastClickedIndex != -1 && _lastClickedIndex != index)
                            {
                                ViewModel.DrawLineContinuous(_lastClickedIndex, index, targetState);
                                _lastClickedIndex = index;
                                _pendingTextUpdateDuringDrag = true;
                            }
                        }
                    }
                }
            }
        }

        // --- MARQUEE TOOL & SELECTION LOGIC ---
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
        }

        private void ClearSelectionVisuals()
        {
            _isSelecting = false;
            _selectionStartIdx = -1;
            _selectionEndIdx = -1;
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (_isDraggingSelection && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(PixelGridContainer);
                double deltaX = currentPos.X - _dragStartMousePos.X;
                double deltaY = currentPos.Y - _dragStartMousePos.Y;

                double cellWidth = 400.0 / ViewModel.SpriteState.Size;
                double cellHeight = 400.0 / ViewModel.SpriteState.Size;

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
            if (e.ChangedButton == MouseButton.Left)
            {
                // NEW: Release the mouse capture if we were holding it
                if (Mouse.Captured == PixelGridContainer)
                {
                    Mouse.Capture(null);
                    // Alternatively, you can use: PixelGridContainer.ReleaseMouseCapture();
                }

                _isSelecting = false;
                _isDraggingSelection = false;
                ViewModel.RedrawGridFromMemory();

                if (_pendingTextUpdateDuringDrag)
                {
                    ViewModel.UpdateTextOutputs();
                    _pendingTextUpdateDuringDrag = false;
                }
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
                else if (e.Key == Key.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
                else if (e.Key == Key.Up) { ViewModel.ShiftGrid(0, -1); e.Handled = true; }
                else if (e.Key == Key.Down) { ViewModel.ShiftGrid(0, 1); e.Handled = true; }
                else if (e.Key == Key.Left) { ViewModel.ShiftGrid(-1, 0); e.Handled = true; }
                else if (e.Key == Key.Right) { ViewModel.ShiftGrid(1, 0); e.Handled = true; }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Keyboard.FocusedElement is TextBox) return;

                if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    if (ViewModel.CurrentTool == ToolMode.Marquee && _hasActiveSelection)
                    {
                        ViewModel.SaveStateForUndo();
                        int size = ViewModel.SpriteState.Size;
                        for (int i = 0; i < size * size; i++)
                        {
                            int x = i % size;
                            int y = i / size;
                            if (x >= _selMinX && x <= _selMaxX && y >= _selMinY && y <= _selMaxY)
                            {
                                ViewModel.SpriteState.Pixels[i] = false;
                            }
                        }
                        ViewModel.RedrawGridFromMemory();
                        ViewModel.UpdateTextOutputs();
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.M) { if (RbMarquee != null) RbMarquee.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.P) { if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.F) { if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; }
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(TxtHex.Text); MessageBox.Show("Copied!"); }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null && ViewModel != null)
            {
                string tag = rb.Tag.ToString();
                ViewModel.CurrentTool = tag == "Fill" ? ToolMode.Fill :
                                        tag == "Marquee" ? ToolMode.Marquee : ToolMode.Pencil;

                if (ViewModel.CurrentTool != ToolMode.Marquee)
                {
                    CommitSelection();
                    ClearSelectionVisuals();
                }
            }
        }
    }
}