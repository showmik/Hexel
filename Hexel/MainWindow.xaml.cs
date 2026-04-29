using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hexel.Core;
using Hexel.Services;

namespace Hexel
{
    public partial class MainWindow : Window
    {
        // Core State
        private SpriteState _spriteState;

        // Services (depend on interfaces for cleaner architecture)
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IHistoryService _historyService;

        // UI State
        private ToolMode _currentTool = ToolMode.Pencil;
        private int _lastClickedIndex = -1;
        private bool _isDisplayInverted = false;
        private bool _isUpdatingProgrammatically = false;

        // Selection & Floating State
        private bool _hasActiveSelection, _isSelecting, _isFloating, _isDraggingSelection;
        // Flag to avoid expensive export updates while mouse is dragging
        private bool _pendingTextUpdateDuringDrag = false;
        private int _selectionStartIdx = -1, _selectionEndIdx = -1;
        private int _selMinX = -1, _selMaxX = -1, _selMinY = -1, _selMaxY = -1;
        private bool[,] _floatingPixels;
        private int _floatingX, _floatingY, _floatingWidth, _floatingHeight;
        private Point _dragStartMousePos;
        private int _dragStartFloatingX, _dragStartFloatingY;

        // Visual Brushes
        private readonly SolidColorBrush _colorOff = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        private readonly SolidColorBrush _colorOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));
        private readonly SolidColorBrush _previewOff = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _previewOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFFF"));

        // Constructor used by DI - MainViewModel is the DataContext
        public MainWindow(ViewModels.MainViewModel vm)
        {
            // initialize concrete services for code-behind operations
            _codeGen = new CodeGeneratorService();
            _drawingService = new DrawingService();
            _historyService = new HistoryService();

            InitializeComponent();
            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));
            InitializeGrid(vm.SpriteState.Size);

            // subscribe to VM brush collections so UI updates when VM redraws
            vm.PixelBrushes.CollectionChanged += (s, e) => UpdateUIFromVM();
            vm.PreviewBrushes.CollectionChanged += (s, e) => UpdateUIFromVM();
        }

        private void UpdateUIFromVM()
        {
            if (!(DataContext is ViewModels.MainViewModel vm)) return;

            // Update pixel grid brushes
            for (int i = 0; i < PixelGrid.Children.Count && i < vm.PixelBrushes.Count; i++)
            {
                if (PixelGrid.Children[i] is Border cell)
                {
                    cell.Background = vm.PixelBrushes[i];
                }
            }

            // Update preview brushes
            for (int i = 0; i < PreviewGrid.Children.Count && i < vm.PreviewBrushes.Count; i++)
            {
                if (PreviewGrid.Children[i] is Rectangle rect)
                {
                    rect.Fill = vm.PreviewBrushes[i];
                }
            }

            // Update text outputs without triggering change handlers
            _isUpdatingProgrammatically = true;
            TxtBinary.Text = vm.TxtBinary;
            TxtHex.Text = vm.TxtHex;
            _isUpdatingProgrammatically = false;
        }

        private void InitializeGrid(int size)
        {
            // prefer VM's SpriteState when available
            if (DataContext is ViewModels.MainViewModel vm)
            {
                // Ask the VM to initialize its grid to the requested size so both VM and view stay in sync
                vm.InitializeGrid(size);
                _spriteState = vm.SpriteState;
            }
            else
            {
                _spriteState = new SpriteState(size);
            }

            BuildGridUI();
            // ensure UI reflects VM data
            UpdateUIFromVM();
            UpdateTextOutputs();
        }

        private void CmbGridSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PixelGrid == null || PreviewGrid == null) return;

            if (CmbGridSize.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                InitializeGrid(int.Parse(item.Tag.ToString()));
            }
        }

        private void BuildGridUI()
        {
            PixelGrid.Rows = PixelGrid.Columns = _spriteState.Size;
            PreviewGrid.Rows = PreviewGrid.Columns = _spriteState.Size;
            PixelGrid.Children.Clear();
            PreviewGrid.Children.Clear();

            PreviewGrid.Width = PreviewGrid.Height = _spriteState.Size * 2;

            for (int i = 0; i < _spriteState.Size * _spriteState.Size; i++)
            {
                Border pixelCell = new Border { Background = _colorOff, BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222222")), BorderThickness = new Thickness(1), Tag = i };
                pixelCell.MouseDown += Pixel_Interaction;
                pixelCell.MouseEnter += Pixel_Interaction;
                PixelGrid.Children.Add(pixelCell);

                PreviewGrid.Children.Add(new Rectangle { Fill = _previewOff });
            }
            // Ensure layout measurements are up to date so overlay calculations use real sizes
            PixelGrid.UpdateLayout();
            // Place marquee overlay above the pixel grid
            if (MarqueeOverlay != null) System.Windows.Controls.Panel.SetZIndex(MarqueeOverlay, 100);
        }

        private void Pixel_Interaction(object sender, MouseEventArgs e)
        {
            if (sender is Border cell && cell.Tag is int index)
            {
                if (_currentTool == ToolMode.Marquee)
                {
                    HandleMarqueeTool(e, index);
                    return;
                }

                if (e is MouseButtonEventArgs)
                {
                    _historyService.SaveState(_spriteState);

                    if (_currentTool == ToolMode.Fill)
                    {
                        bool newState = Mouse.LeftButton == MouseButtonState.Pressed;
                        if (Mouse.RightButton == MouseButtonState.Pressed) newState = false;

                        _drawingService.ApplyFloodFill(_spriteState, index, newState);
                        _lastClickedIndex = index;
                        RedrawAndExport();
                        return;
                    }

                    if (_currentTool == ToolMode.Pencil && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClickedIndex != -1)
                    {
                        bool newState = Mouse.LeftButton == MouseButtonState.Pressed;
                        _drawingService.DrawLine(_spriteState, _lastClickedIndex, index, newState);
                        _lastClickedIndex = index;
                        RedrawAndExport();
                        return;
                    }
                }

                if (_currentTool == ToolMode.Pencil)
                {
                    bool stateChanged = false;
                    if (Mouse.LeftButton == MouseButtonState.Pressed && !_spriteState.Pixels[index])
                    {
                        _spriteState.Pixels[index] = true;
                        stateChanged = true;
                    }
                    else if (Mouse.RightButton == MouseButtonState.Pressed && _spriteState.Pixels[index])
                    {
                        _spriteState.Pixels[index] = false;
                        stateChanged = true;
                    }

                    if (stateChanged)
                    {
                        // Update only the affected pixel UI and export text to reduce redraw work
                        UpdateSinglePixelUI(index);
                        _lastClickedIndex = index;
                        // If this event is the initial mouse down, update exports immediately.
                        // For mouse-enter during dragging, defer export update until mouse up to keep UI responsive.
                        if (e is MouseButtonEventArgs)
                        {
                            UpdateTextOutputs();
                        }
                        else
                        {
                            _pendingTextUpdateDuringDrag = true;
                        }
                    }
                }
            }
        }

        // --- MARQUEE TOOL & SELECTION LOGIC ---

        private void HandleMarqueeTool(MouseEventArgs e, int index)
        {
            int x = index % _spriteState.Size;
            int y = index / _spriteState.Size;

            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                if (e is MouseButtonEventArgs)
                {
                    // If clicking inside an existing selection, lift it for dragging
                    if (_hasActiveSelection && x >= _selMinX && x <= _selMaxX && y >= _selMinY && y <= _selMaxY)
                    {
                        _historyService.SaveState(_spriteState);
                        LiftSelection();
                        _isDraggingSelection = true;
                        _dragStartMousePos = e.GetPosition(PixelGrid);
                        _dragStartFloatingX = _floatingX;
                        _dragStartFloatingY = _floatingY;
                        return;
                    }

                    // Start a new selection
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
                    _selMinX = Math.Min(_selectionStartIdx % _spriteState.Size, x);
                    _selMaxX = Math.Max(_selectionStartIdx % _spriteState.Size, x);
                    _selMinY = Math.Min(_selectionStartIdx / _spriteState.Size, y);
                    _selMaxY = Math.Max(_selectionStartIdx / _spriteState.Size, y);
                    UpdateSelectionVisualsFromBounds();
                }
            }
        }

        private void LiftSelection()
        {
            if (!_hasActiveSelection || _isFloating) return;

            _historyService.SaveState(_spriteState);

            _floatingWidth = (_selMaxX - _selMinX) + 1;
            _floatingHeight = (_selMaxY - _selMinY) + 1;
            _floatingX = _selMinX;
            _floatingY = _selMinY;
            _floatingPixels = new bool[_floatingWidth, _floatingHeight];

            for (int y = _selMinY; y <= _selMaxY; y++)
            {
                for (int x = _selMinX; x <= _selMaxX; x++)
                {
                    int idx = (y * _spriteState.Size) + x;
                    if (_spriteState.Pixels[idx])
                    {
                        _floatingPixels[x - _selMinX, y - _selMinY] = true;
                        _spriteState.Pixels[idx] = false;
                    }
                }
            }
            _isFloating = true;
        }

        private void CommitSelection()
        {
            if (!_hasActiveSelection) return;

            if (_isFloating && _floatingPixels != null)
            {
                for (int y = 0; y < _floatingHeight; y++)
                {
                    for (int x = 0; x < _floatingWidth; x++)
                    {
                        if (_floatingPixels[x, y])
                        {
                            int gridX = _floatingX + x;
                            int gridY = _floatingY + y;

                            if (gridX >= 0 && gridX < _spriteState.Size && gridY >= 0 && gridY < _spriteState.Size)
                            {
                                _spriteState.Pixels[(gridY * _spriteState.Size) + gridX] = true;
                            }
                        }
                    }
                }
                _isFloating = false;
                _floatingPixels = null;
            }

            _hasActiveSelection = false;
            _selMinX = _selMaxX = _selMinY = _selMaxY = -1;
            ClearSelectionVisuals();
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        private void UpdateSelectionVisualsFromBounds()
        {
            if (MarqueeOverlay == null) return;
            // Use the actual rendered size of the pixel grid so the overlay stays aligned
            double gridWidth = (PixelGrid != null && PixelGrid.ActualWidth > 0) ? PixelGrid.ActualWidth : 400.0;
            double gridHeight = (PixelGrid != null && PixelGrid.ActualHeight > 0) ? PixelGrid.ActualHeight : 400.0;

            double cellWidth = gridWidth / _spriteState.Size;
            double cellHeight = gridHeight / _spriteState.Size;

            Canvas.SetLeft(MarqueeOverlay, _selMinX * cellWidth);
            Canvas.SetTop(MarqueeOverlay, _selMinY * cellHeight);

            MarqueeOverlay.Width = ((_selMaxX - _selMinX) + 1) * cellWidth;
            MarqueeOverlay.Height = ((_selMaxY - _selMinY) + 1) * cellHeight;

            // Ensure overlay is above the pixels
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
                Point currentPos = e.GetPosition(PixelGrid);
                double deltaX = currentPos.X - _dragStartMousePos.X;
                double deltaY = currentPos.Y - _dragStartMousePos.Y;

                double cellWidth = 400.0 / _spriteState.Size;
                double cellHeight = 400.0 / _spriteState.Size;

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
                    RedrawGridFromMemory();
                }
            }
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);
            if (e.ChangedButton == MouseButton.Left)
            {
                _isSelecting = false;
                _isDraggingSelection = false;
                // After finishing drag, update exports once to avoid lag during continuous mouse moves
                RedrawGridFromMemory();
                if (_pendingTextUpdateDuringDrag)
                {
                    UpdateTextOutputs();
                    _pendingTextUpdateDuringDrag = false;
                }
            }
        }

        // --- KEYBOARD SHORTCUTS ---

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Z) { _spriteState = _historyService.Undo(_spriteState); RedrawAndExport(); e.Handled = true; }
                else if (e.Key == Key.Y) { _spriteState = _historyService.Redo(_spriteState); RedrawAndExport(); e.Handled = true; }
                else if (e.Key == Key.Up) { _historyService.SaveState(_spriteState); _drawingService.ShiftGrid(_spriteState, 0, -1); RedrawAndExport(); e.Handled = true; }
                else if (e.Key == Key.Down) { _historyService.SaveState(_spriteState); _drawingService.ShiftGrid(_spriteState, 0, 1); RedrawAndExport(); e.Handled = true; }
                else if (e.Key == Key.Left) { _historyService.SaveState(_spriteState); _drawingService.ShiftGrid(_spriteState, -1, 0); RedrawAndExport(); e.Handled = true; }
                else if (e.Key == Key.Right) { _historyService.SaveState(_spriteState); _drawingService.ShiftGrid(_spriteState, 1, 0); RedrawAndExport(); e.Handled = true; }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Keyboard.FocusedElement is TextBox) return;

                if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    if (_currentTool == ToolMode.Marquee && _hasActiveSelection)
                    {
                        _historyService.SaveState(_spriteState);
                        for (int i = 0; i < _spriteState.Size * _spriteState.Size; i++)
                        {
                            int x = i % _spriteState.Size;
                            int y = i / _spriteState.Size;
                            if (x >= _selMinX && x <= _selMaxX && y >= _selMinY && y <= _selMaxY)
                            {
                                _spriteState.Pixels[i] = false;
                            }
                        }
                        RedrawAndExport();
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.M) { if (RbMarquee != null) RbMarquee.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.P) { if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; }
                else if (e.Key == Key.F) { if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; }
            }
        }

        // --- DRAWING & EXPORT ---

        private void RedrawAndExport()
        {
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        private void UpdateSinglePixelUI(int index)
        {
            if (PixelGrid.Children[index] is Border cell) cell.Background = _spriteState.Pixels[index] ? _colorOn : _colorOff;
            if (PreviewGrid.Children[index] is Rectangle prev) prev.Fill = _spriteState.Pixels[index] ? _previewOn : _previewOff;
        }

        private void RedrawGridFromMemory()
        {
            for (int y = 0; y < _spriteState.Size; y++)
            {
                for (int x = 0; x < _spriteState.Size; x++)
                {
                    int i = (y * _spriteState.Size) + x;
                    bool isPixelOn = _spriteState.Pixels[i];

                    if (_isFloating)
                    {
                        int floatLocalX = x - _floatingX;
                        int floatLocalY = y - _floatingY;
                        if (floatLocalX >= 0 && floatLocalX < _floatingWidth && floatLocalY >= 0 && floatLocalY < _floatingHeight)
                            if (_floatingPixels[floatLocalX, floatLocalY]) isPixelOn = true;
                    }

                    if (PixelGrid.Children[i] is Border cell) cell.Background = isPixelOn ? _colorOn : _colorOff;
                    if (PreviewGrid.Children[i] is Rectangle previewRect) previewRect.Fill = isPixelOn ? _previewOn : _previewOff;
                }
            }
        }

        private void UpdateTextOutputs()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            var (binary, hex) = _codeGen.GenerateExportStrings(_spriteState, _isFloating, _floatingPixels, _floatingX, _floatingY, _floatingWidth, _floatingHeight);

            TxtBinary.Text = binary;
            TxtHex.Text = hex;

            _isUpdatingProgrammatically = false;
        }

        private void TxtBinary_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;
            _codeGen.ParseBinaryToState(TxtBinary.Text, _spriteState);
            RedrawGridFromMemory();
            _isUpdatingProgrammatically = false;
        }

        private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;
            _codeGen.ParseHexToState(TxtHex.Text, _spriteState);
            RedrawGridFromMemory();
            _isUpdatingProgrammatically = false;
        }

        // --- BUTTON HANDLERS ---
        private void BtnUndo_Click(object sender, RoutedEventArgs e) { _spriteState = _historyService.Undo(_spriteState); RedrawAndExport(); }
        private void BtnRedo_Click(object sender, RoutedEventArgs e) { _spriteState = _historyService.Redo(_spriteState); RedrawAndExport(); }
        private void BtnClear_Click(object sender, RoutedEventArgs e) { _historyService.SaveState(_spriteState); Array.Clear(_spriteState.Pixels, 0, _spriteState.Pixels.Length); RedrawAndExport(); }
        private void BtnInvert_Click(object sender, RoutedEventArgs e)
        {
            // 1. Save state for undo and apply the mathematical array inversion in the service
            _historyService.SaveState(_spriteState);
            _drawingService.InvertGrid(_spriteState);

            // 2. Toggle the UI display state flag
            _isDisplayInverted = !_isDisplayInverted;

            // 3. Update the OLED container backgrounds
            if (_isDisplayInverted)
            {
                // Set entire OLED screen to Cyan
                OledBorder.Background = _previewOn;
                PreviewGrid.Background = _previewOn;
            }
            else
            {
                // Revert back to the default dark OLED colors
                OledBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#050505"));
                PreviewGrid.Background = new SolidColorBrush(Colors.Black);
            }

            // 4. Redraw the pixels and update the code boxes
            RedrawAndExport();
        }
        private void BtnCopy_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(TxtHex.Text); MessageBox.Show("Copied!"); }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                string tag = rb.Tag.ToString();

                _currentTool = tag == "Fill" ? ToolMode.Fill :
                               tag == "Marquee" ? ToolMode.Marquee : ToolMode.Pencil;

                if (_currentTool != ToolMode.Marquee)
                {
                    CommitSelection();
                    ClearSelectionVisuals();
                }
            }
        }
    }
}