using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpriteGenerator
{
    public partial class MainWindow : Window
    {
        private int _gridSize = 16;
        private bool[] _pixels;
        private enum ToolMode { Pencil, Fill }
        private ToolMode _currentTool = ToolMode.Pencil;
        private int _lastClickedIndex = -1;

        // NEW: State Stacks for Undo/Redo
        private Stack<bool[]> _undoStack = new Stack<bool[]>();
        private Stack<bool[]> _redoStack = new Stack<bool[]>();

        // UI Colors
        private readonly SolidColorBrush _colorOff = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        private readonly SolidColorBrush _colorOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));

        // Preview Colors (OLED style)
        private readonly SolidColorBrush _previewOff = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _previewOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFFF")); // Cyan OLED

        private bool _isUpdatingProgrammatically = false;

        public MainWindow()
        {
            InitializeComponent();
            BuildGrid();
        }

        private void CmbGridSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbGridSize.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                _gridSize = int.Parse(item.Tag.ToString());
                BuildGrid();
            }
        }

        private void BuildGrid()
        {
            if (PixelGrid == null || PreviewGrid == null) return;

            _pixels = new bool[_gridSize * _gridSize];

            // Setup Main Interactive Grid
            PixelGrid.Rows = _gridSize;
            PixelGrid.Columns = _gridSize;
            PixelGrid.Children.Clear();

            // Setup Preview Grid
            PreviewGrid.Rows = _gridSize;
            PreviewGrid.Columns = _gridSize;

            // NEW: Lock the physical size of the preview so pixels remain a constant size.
            // Multiplied by 2 to match the 2x scaled OLED screen border (256x128).
            PreviewGrid.Width = _gridSize * 2;
            PreviewGrid.Height = _gridSize * 2;
            PreviewGrid.Children.Clear();

            for (int i = 0; i < _gridSize * _gridSize; i++)
            {
                // Main Interactive Cell
                Border pixelCell = new Border
                {
                    Background = _colorOff,
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222222")),
                    BorderThickness = new Thickness(1),
                    Tag = i
                };

                pixelCell.MouseDown += Pixel_Interaction;
                pixelCell.MouseEnter += Pixel_Interaction;
                PixelGrid.Children.Add(pixelCell);

                // Preview Cell (Lightweight Rectangle)
                // Since the UniformGrid has a strict Width/Height, these rectangles naturally scale to exactly 2x2 pixels
                Rectangle previewCell = new Rectangle
                {
                    Fill = _previewOff
                };
                PreviewGrid.Children.Add(previewCell);
            }

            UpdateTextFromGrid();
        }

        private void Pixel_Interaction(object sender, MouseEventArgs e)
        {
            if (sender is Border cell && cell.Tag is int index)
            {
                if (e is MouseButtonEventArgs)
                {
                    SaveStateForUndo(); // Always save state on the initial click

                    // --- EXISTING: Handle Fill Tool Logic ---
                    if (_currentTool == ToolMode.Fill)
                    {
                        bool targetState = _pixels[index];
                        bool newState = targetState;

                        if (Mouse.LeftButton == MouseButtonState.Pressed) newState = true;
                        else if (Mouse.RightButton == MouseButtonState.Pressed) newState = false;

                        ApplyFloodFill(index, targetState, newState);
                        _lastClickedIndex = index; // Update anchor point
                        return;
                    }

                    // --- NEW: Handle Shift-Click for Lines ---
                    if (_currentTool == ToolMode.Pencil && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        // Ensure we have a valid starting point
                        if (_lastClickedIndex != -1)
                        {
                            bool newState = true;
                            if (Mouse.RightButton == MouseButtonState.Pressed) newState = false;

                            DrawLine(_lastClickedIndex, index, newState);
                            _lastClickedIndex = index; // Update anchor point for the next line segment
                            return;
                        }
                    }
                }

                // --- EXISTING: Standard Pencil Tool Logic ---
                if (_currentTool == ToolMode.Pencil)
                {
                    bool stateChanged = false;

                    if (Mouse.LeftButton == MouseButtonState.Pressed && !_pixels[index])
                    {
                        _pixels[index] = true;
                        stateChanged = true;
                    }
                    else if (Mouse.RightButton == MouseButtonState.Pressed && _pixels[index])
                    {
                        _pixels[index] = false;
                        stateChanged = true;
                    }

                    if (stateChanged)
                    {
                        cell.Background = _pixels[index] ? _colorOn : _colorOff;

                        if (PreviewGrid.Children[index] is Rectangle previewRect)
                        {
                            previewRect.Fill = _pixels[index] ? _previewOn : _previewOff;
                        }

                        UpdateTextFromGrid();

                        // Track this as the last edited pixel so lines can connect to it
                        _lastClickedIndex = index;
                    }
                }
            }
        }

        private void UpdateTextFromGrid()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            List<string> binList = new List<string>();
            List<string> hexList = new List<string>();
            int bytesPerRow = (int)Math.Ceiling(_gridSize / 8.0);

            for (int row = 0; row < _gridSize; row++)
            {
                string fullRowBinary = "";

                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    string byteString = "";
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        if (col < _gridSize)
                        {
                            int index = (row * _gridSize) + col;
                            byteString += _pixels[index] ? "1" : "0";
                        }
                        else
                        {
                            byteString += "0";
                        }
                    }

                    fullRowBinary += byteString + " ";
                    int hexValue = Convert.ToInt32(byteString, 2);
                    hexList.Add($"0x{hexValue:X2}");
                }
                binList.Add($"// Row {row,2}: {fullRowBinary}");
            }

            string hexFormatted = "";
            for (int i = 0; i < hexList.Count; i++)
            {
                hexFormatted += hexList[i] + ", ";
                if ((i + 1) % bytesPerRow == 0) hexFormatted += "\n  ";
            }

            TxtBinary.Text = string.Join("\n", binList);
            TxtHex.Text = $"const unsigned char sprite_{_gridSize}x{_gridSize}[] PROGMEM = {{\n  " +
                          hexFormatted.TrimEnd(' ', ',', '\n') +
                          "\n};";

            _isUpdatingProgrammatically = false;
        }

        private void TxtBinary_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            string[] lines = TxtBinary.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;

            foreach (string line in lines)
            {
                if (row >= _gridSize) break;

                string dataPart = line.Contains(":") ? line.Substring(line.IndexOf(':') + 1) : line;
                dataPart = dataPart.Replace(" ", "");

                for (int col = 0; col < _gridSize; col++)
                {
                    _pixels[(row * _gridSize) + col] = (col < dataPart.Length) && (dataPart[col] == '1');
                }
                row++;
            }

            RedrawGridFromMemory();
            _isUpdatingProgrammatically = false;
        }

        private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            MatchCollection matches = Regex.Matches(TxtHex.Text, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = (int)Math.Ceiling(_gridSize / 8.0);
            int matchIndex = 0;

            for (int row = 0; row < _gridSize; row++)
            {
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    if (matchIndex < matches.Count)
                    {
                        byte b = Convert.ToByte(matches[matchIndex].Groups[1].Value, 16);
                        matchIndex++;

                        for (int bit = 7; bit >= 0; bit--)
                        {
                            int col = (chunk * 8) + (7 - bit);
                            if (col < _gridSize)
                            {
                                _pixels[(row * _gridSize) + col] = ((b >> bit) & 1) == 1;
                            }
                        }
                    }
                }
            }

            RedrawGridFromMemory();
            _isUpdatingProgrammatically = false;
        }

        private void RedrawGridFromMemory()
        {
            for (int i = 0; i < _gridSize * _gridSize; i++)
            {
                // Update Main Grid
                if (PixelGrid.Children[i] is Border cell)
                {
                    cell.Background = _pixels[i] ? _colorOn : _colorOff;
                }

                // Update Preview Grid
                if (PreviewGrid.Children[i] is Rectangle previewRect)
                {
                    previewRect.Fill = _pixels[i] ? _previewOn : _previewOff;
                }
            }
        }

        private void DrawLine(int startIdx, int endIdx, bool state)
        {
            int x0 = startIdx % _gridSize;
            int y0 = startIdx / _gridSize;
            int x1 = endIdx % _gridSize;
            int y1 = endIdx / _gridSize;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                int currentIndex = (y0 * _gridSize) + x0;
                _pixels[currentIndex] = state;

                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }

            // Update the UI and text once the full line is calculated in memory
            RedrawGridFromMemory();
            UpdateTextFromGrid();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo(); // NEW: Save state before clearing
            Array.Clear(_pixels, 0, _pixels.Length);
            RedrawGridFromMemory();
            UpdateTextFromGrid();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(TxtHex.Text);
            MessageBox.Show("HEX Array copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveStateForUndo()
        {
            // Push a clone of the current array so we don't pass a reference
            _undoStack.Push((bool[])_pixels.Clone());

            // Once a new action is taken, the redo history is no longer valid
            _redoStack.Clear();
        }

        private void Undo()
        {
            if (_undoStack.Count > 0)
            {
                // Save current state to Redo stack before rolling back
                _redoStack.Push((bool[])_pixels.Clone());

                // Pop the last saved state
                _pixels = _undoStack.Pop();

                RedrawGridFromMemory();
                UpdateTextFromGrid();
            }
        }

        private void Redo()
        {
            if (_redoStack.Count > 0)
            {
                // Save current state to Undo stack before rolling forward
                _undoStack.Push((bool[])_pixels.Clone());

                // Pop the next saved state
                _pixels = _redoStack.Pop();

                RedrawGridFromMemory();
                UpdateTextFromGrid();
            }
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            Undo();
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            Redo();
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                _currentTool = rb.Tag.ToString() == "Fill" ? ToolMode.Fill : ToolMode.Pencil;
            }
        }

        // REPLACED: Changed from OnKeyDown to OnPreviewKeyDown to capture Arrow Keys reliably
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Z)
                {
                    Undo();
                    e.Handled = true;
                }
                else if (e.Key == Key.Y)
                {
                    Redo();
                    e.Handled = true;
                }
                // NEW: Grid Shifting shortcuts (Ctrl + Arrow Keys)
                else if (e.Key == Key.Up)
                {
                    ShiftGrid(0, -1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    ShiftGrid(0, 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    ShiftGrid(-1, 0);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    ShiftGrid(1, 0);
                    e.Handled = true;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                // Prevent tool shortcuts from triggering if the user is typing in the Hex/Binary text boxes
                if (Keyboard.FocusedElement is TextBox) return;

                if (e.Key == Key.P)
                {
                    if (RbPencil != null) RbPencil.IsChecked = true;
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    if (RbFill != null) RbFill.IsChecked = true;
                    e.Handled = true;
                }
            }
        }

        private void ApplyFloodFill(int startIndex, bool targetState, bool newState)
        {
            // If clicking on a pixel that is already the desired color, do nothing
            if (targetState == newState) return;

            Queue<int> queue = new Queue<int>();
            queue.Enqueue(startIndex);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();

                // Process if the pixel matches the target background we are trying to fill
                if (_pixels[current] == targetState)
                {
                    _pixels[current] = newState;

                    int x = current % _gridSize;
                    int y = current / _gridSize;

                    // Check Left
                    if (x > 0) queue.Enqueue(current - 1);
                    // Check Right
                    if (x < _gridSize - 1) queue.Enqueue(current + 1);
                    // Check Up
                    if (y > 0) queue.Enqueue(current - _gridSize);
                    // Check Down
                    if (y < _gridSize - 1) queue.Enqueue(current + _gridSize);
                }
            }

            // Because Fill modifies many pixels at once, we redraw the whole grid
            RedrawGridFromMemory();
            UpdateTextFromGrid();
        }

        private void ShiftGrid(int offsetX, int offsetY)
        {
            // Save the current state so the user can Undo
            SaveStateForUndo();

            bool[] newPixels = new bool[_gridSize * _gridSize];

            for (int y = 0; y < _gridSize; y++)
            {
                for (int x = 0; x < _gridSize; x++)
                {
                    // We only need to move pixels that are turned ON (true)
                    if (_pixels[(y * _gridSize) + x])
                    {
                        // Calculate new X, using modulo to wrap around
                        int newX = (x + offsetX) % _gridSize;
                        // C# modulo can return negative numbers, so we force it positive
                        if (newX < 0) newX += _gridSize;

                        // Calculate new Y, using modulo to wrap around
                        int newY = (y + offsetY) % _gridSize;
                        if (newY < 0) newY += _gridSize;

                        // Set the pixel at the new wrapped coordinate to true
                        newPixels[(newY * _gridSize) + newX] = true;
                    }
                }
            }

            _pixels = newPixels;
            RedrawGridFromMemory();
            UpdateTextFromGrid();
        }
    }
}