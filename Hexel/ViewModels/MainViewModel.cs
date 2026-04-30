using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using Hexel.Services;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hexel.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        #region Services & Dependencies
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IHistoryService _historyService;
        private readonly System.Threading.SynchronizationContext _uiContext;
        private readonly IClipboardService _clipboardService;
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        #endregion

        #region Private Fields
        // Brushes used for UI representation
        private readonly SolidColorBrush _colorOff;
        private readonly SolidColorBrush _colorOn;
        private readonly SolidColorBrush _previewOff;
        private readonly SolidColorBrush _previewOn;
        private bool _showGridLines = true;
        private bool _binaryUseComma = false;
        private bool _hexUseComma = true; // Checked by default for Hex is usually preferred, but you can change to false!

        // Core state backing fields
        private bool _isUpdatingProgrammatically = false;
        private SpriteState _spriteState;
        private int _gridSize = 16;
        private bool _isDisplayInverted = false;

        // Text backing fields
        private string _txtBinary = string.Empty;
        private string _txtHex = string.Empty;

        // Tool & interaction backing fields
        private ToolMode _currentTool = ToolMode.Pencil;
        private int _lastClickedIndex = -1;

        // Tool tracking state
        private int _lineStartIdx = -1;
        private int _lineCurrentIdx = -1;
        private bool _lineDrawState = false;
        private bool _pendingTextUpdateDuringDrag = false;
        #endregion

        #region WritableBitmaps & Pixel Collections
        private WriteableBitmap _canvasBitmap;
        public WriteableBitmap CanvasBitmap
        {
            get => _canvasBitmap;
            set => SetProperty(ref _canvasBitmap, value);
        }

        private WriteableBitmap _previewBitmap;
        public WriteableBitmap PreviewBitmap
        {
            get => _previewBitmap;
            set => SetProperty(ref _previewBitmap, value);
        }

        // Buffers and color representations
        private uint[] _canvasBuffer;
        private uint[] _previewBuffer;
        private uint _colorOffUint, _colorOnUint, _previewOffUint, _previewOnUint;

        // Helper to convert WPF Color to BGRA32 uint for the WriteableBitmap
        private uint ToBgra32(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
        #endregion

        #region Properties
        // -- Core Properties --
        public bool BinaryUseComma
        {
            get => _binaryUseComma;
            set
            {
                if (SetProperty(ref _binaryUseComma, value)) UpdateTextOutputs();
            }
        }

        public bool HexUseComma
        {
            get => _hexUseComma;
            set
            {
                if (SetProperty(ref _hexUseComma, value)) UpdateTextOutputs();
            }
        }


        public bool ShowGridLines
        {
            get => _showGridLines;
            set => SetProperty(ref _showGridLines, value);
        }

        public SpriteState SpriteState
        {
            get => _spriteState;
            set => SetProperty(ref _spriteState, value);
        }

        public int GridSize
        {
            get => _gridSize;
            set
            {
                if (SetProperty(ref _gridSize, value))
                {
                    InitializeGrid(value);
                    OnPropertyChanged(nameof(PreviewSize));
                    OnPropertyChanged(nameof(GridViewport)); // <-- Add this!
                }
            }
        }

        public Rect GridViewport => new Rect(0, 0, 400.0 / GridSize, 400.0 / GridSize);

        // Replaces the "vm.SpriteState.Size * 2" math from your code-behind
        public int PreviewSize => GridSize * 2;

        public bool IsDisplayInverted
        {
            get => _isDisplayInverted;
            set => SetProperty(ref _isDisplayInverted, value);
        }

        // -- Text Code Properties --
        public string TxtBinary
        {
            get => _txtBinary;
            set
            {
                if (_isUpdatingProgrammatically) return;
                SetProperty(ref _txtBinary, value);

                // 1. Create a backup of the current state
                var backupState = SpriteState.Clone();

                try
                {
                    // 2. Attempt to parse the new text
                    _codeGen.ParseBinaryToState(value, SpriteState);
                    RedrawGridFromMemory();
                }
                catch (Exception)
                {
                    // 3. If parsing fails (e.g., malformed text), revert to the backup
                    SpriteState = backupState;
                    RedrawGridFromMemory(); // Ensure UI matches the reverted state
                }
                finally
                {
                    // Always update the text outputs to reflect the final state
                    UpdateTextOutputs();
                }
            }
        }

        public string TxtHex
        {
            get => _txtHex;
            set
            {
                if (_isUpdatingProgrammatically) return;
                SetProperty(ref _txtHex, value);

                var backupState = SpriteState.Clone();

                try
                {
                    _codeGen.ParseHexToState(value, SpriteState);
                    RedrawGridFromMemory();
                }
                catch (Exception)
                {
                    SpriteState = backupState;
                    RedrawGridFromMemory();
                }
                finally
                {
                    UpdateTextOutputs();
                }
            }
        }

        // -- Tool & Selection Properties --
        public ToolMode CurrentTool
        {
            get => _currentTool;
            set => SetProperty(ref _currentTool, value);
        }

        public bool IsDrawingLine { get; private set; }

        // Selection fields (public so view can bind/observe)
        public bool HasActiveSelection { get; private set; }
        public bool IsSelecting { get; private set; }
        public bool IsFloating { get; private set; }
        public int SelectionStartIdx { get; private set; } = -1;
        public int SelectionEndIdx { get; private set; } = -1;
        public int SelMinX { get; private set; } = -1;
        public int SelMaxX { get; private set; } = -1;
        public int SelMinY { get; private set; } = -1;
        public int SelMaxY { get; private set; } = -1;
        public bool[,] FloatingPixels { get; private set; }
        public int FloatingX { get; private set; }
        public int FloatingY { get; private set; }
        public int FloatingWidth { get; private set; }
        public int FloatingHeight { get; private set; }
        #endregion

        #region Commands
        public IRelayCommand UndoCommand { get; }
        public IRelayCommand RedoCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand InvertCommand { get; }
        public IRelayCommand DeleteSelectionCommand { get; }
        public IRelayCommand CopyHexCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand LoadCommand { get; }
        #endregion

        #region Constructor
        public MainViewModel(ICodeGeneratorService codeGen,
                             IDrawingService drawingService,
                             IHistoryService historyService,
                             IClipboardService clipboardService,
                             IDialogService dialogService,
                             IFileService fileService)
        {
            _uiContext = System.Threading.SynchronizationContext.Current ?? new System.Threading.SynchronizationContext();
            _codeGen = codeGen ?? throw new ArgumentNullException(nameof(codeGen));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));

            // Initialize commands
            SaveCommand = new RelayCommand(() =>
            {
                try
                {
                    _fileService.SaveSprite(SpriteState);
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"Error saving file: {ex.Message}");
                }
            });
            
            LoadCommand = new RelayCommand(() =>
            {
                try
                {
                    var loadedState = _fileService.LoadSprite();
                    if (loadedState != null)
                    {
                        _historyService.SaveState(SpriteState); // Save current state for undo

                        // Changing GridSize automatically re-initializes bitmaps and the SpriteState wrapper
                        GridSize = loadedState.Size;

                        // Inject the loaded pixels into the freshly initialized state
                        SpriteState.Pixels = (bool[])loadedState.Pixels.Clone();

                        RedrawGridFromMemory();
                        UpdateTextOutputs();
                    }
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"Error loading file: {ex.Message}");
                }
            });



            CopyHexCommand = new RelayCommand(() =>
            {
                _clipboardService.SetText(TxtHex);
                _dialogService.ShowMessage("Copied!");
            });

            UndoCommand = new RelayCommand(() =>
            {
                SpriteState = _historyService.Undo(SpriteState);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            RedoCommand = new RelayCommand(() =>
            {
                SpriteState = _historyService.Redo(SpriteState);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            ClearCommand = new RelayCommand(() =>
            {
                _historyService.SaveState(SpriteState);
                Array.Clear(SpriteState.Pixels, 0, SpriteState.Pixels.Length);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            InvertCommand = new RelayCommand(() =>
            {
                _historyService.SaveState(SpriteState);
                _drawingService.InvertGrid(SpriteState);
                IsDisplayInverted = !IsDisplayInverted;
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            DeleteSelectionCommand = new RelayCommand(DeleteSelection);

            // Setup state & brushes
            SpriteState = new SpriteState(16);
            InitializeGrid(16);

            _colorOff = (SolidColorBrush)System.Windows.Application.Current.Resources["Theme.PanelBackgroundBrush"];
            _colorOn = (SolidColorBrush)System.Windows.Application.Current.Resources["Theme.PrimaryAccentBrush"];
            _previewOff = (SolidColorBrush)System.Windows.Application.Current.Resources["Theme.OledOffBrush"];
            _previewOn = (SolidColorBrush)System.Windows.Application.Current.Resources["Theme.OledOnBrush"];

            _colorOffUint = ToBgra32(_colorOff.Color);
            _colorOnUint = ToBgra32(_colorOn.Color);
            _previewOffUint = ToBgra32(_previewOff.Color);
            _previewOnUint = ToBgra32(_previewOn.Color);

            // Try to freeze shared brushes for minor perf/thread-safety benefit
            try
            {
                if (_colorOff.CanFreeze) _colorOff.Freeze();
                if (_colorOn.CanFreeze) _colorOn.Freeze();
                if (_previewOff.CanFreeze) _previewOff.Freeze();
                if (_previewOn.CanFreeze) _previewOn.Freeze();
            }
            catch
            {
                // ignore
            }

            UpdateTextOutputs();
        }
        #endregion

        #region Public Methods
        public void InitializeGrid(int size)
        {
            SpriteState = new SpriteState(size);

            // Create new bitmaps and buffers matching the new size
            CanvasBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            PreviewBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);

            _canvasBuffer = new uint[size * size];
            _previewBuffer = new uint[size * size];

            UpdateTextOutputs();
            RedrawGridFromMemory();
        }

        public void RedrawGridFromMemory()
        {
            if (CanvasBitmap == null || _canvasBuffer == null) return;
            int size = SpriteState.Size;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size) + x;
                    bool isPixelOn = SpriteState.Pixels[i];

                    if (IsFloating && FloatingPixels != null)
                    {
                        int floatLocalX = x - FloatingX;
                        int floatLocalY = y - FloatingY;
                        if (floatLocalX >= 0 && floatLocalX < FloatingWidth && floatLocalY >= 0 && floatLocalY < FloatingHeight)
                            if (FloatingPixels[floatLocalX, floatLocalY]) isPixelOn = true;
                    }

                    _canvasBuffer[i] = isPixelOn ? _colorOnUint : _colorOffUint;
                    _previewBuffer[i] = isPixelOn ? _previewOnUint : _previewOffUint;
                }
            }

            // Write the flat arrays directly to the bitmap textures in one pass
            CanvasBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), _canvasBuffer, size * 4, 0);
            PreviewBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), _previewBuffer, size * 4, 0);
        }

        public void ProcessToolInput(int index, string actionType, bool? drawState, bool isShiftDown)
        {
            if (CurrentTool == ToolMode.Marquee) return; // Marquee is handled separately

            if (actionType == "Down" && drawState.HasValue)
            {
                if (CurrentTool == ToolMode.Line)
                {
                    IsDrawingLine = true;
                    _lineStartIdx = index;
                    _lineCurrentIdx = index;
                    _lineDrawState = drawState.Value;
                    PreviewLine(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                }
                else if (CurrentTool == ToolMode.Fill)
                {
                    SaveStateForUndo();
                    ApplyFloodFill(index, drawState.Value);
                    _lastClickedIndex = index;
                }
                else if (CurrentTool == ToolMode.Pencil)
                {
                    SaveStateForUndo();
                    if (isShiftDown && _lastClickedIndex != -1)
                        DrawLine(_lastClickedIndex, index, drawState.Value);
                    else
                        SetPixel(index, drawState.Value);

                    _lastClickedIndex = index;
                    UpdateTextOutputs();
                }
            }
            else if (actionType == "Enter")
            {
                if (CurrentTool == ToolMode.Line && IsDrawingLine)
                {
                    if (_lineCurrentIdx != index)
                    {
                        _lineCurrentIdx = index;
                        PreviewLine(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    }
                }
                else if (CurrentTool == ToolMode.Pencil && drawState.HasValue)
                {
                    if (_lastClickedIndex != -1 && _lastClickedIndex != index)
                    {
                        DrawLineContinuous(_lastClickedIndex, index, drawState.Value);
                        _lastClickedIndex = index;
                        _pendingTextUpdateDuringDrag = true;
                    }
                }
            }
            else if (actionType == "Up")
            {
                if (CurrentTool == ToolMode.Line && IsDrawingLine)
                {
                    IsDrawingLine = false;
                    if (_lineStartIdx != -1 && _lineCurrentIdx != -1)
                    {
                        DrawLine(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                        _lastClickedIndex = _lineCurrentIdx;
                    }
                    _lineStartIdx = -1;
                    _lineCurrentIdx = -1;
                    UpdateTextOutputs();
                }

                if (CurrentTool == ToolMode.Pencil && _pendingTextUpdateDuringDrag)
                {
                    UpdateTextOutputs();
                    _pendingTextUpdateDuringDrag = false;
                }
            }
        }

        public void PreviewLine(int startIdx, int endIdx, bool newState)
        {
            // Reset to current committed state to clear any previous preview line
            RedrawGridFromMemory();

            // Calculate the preview line (Bresenham's line algorithm)
            int size = SpriteState.Size;
            int x0 = startIdx % size;
            int y0 = startIdx / size;
            int x1 = endIdx % size;
            int y1 = endIdx / size;

            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            // Use the new uint color representations
            var previewColor = newState ? _colorOnUint : _colorOffUint;
            var previewPrev = newState ? _previewOnUint : _previewOffUint;

            while (true)
            {
                int i = (y0 * size) + x0;

                // Write directly to the uint arrays instead of the deleted ObservableCollections
                _canvasBuffer[i] = previewColor;
                _previewBuffer[i] = previewPrev;

                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }

            // Push the updated buffers to the WriteableBitmaps to show the preview
            CanvasBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), _canvasBuffer, size * 4, 0);
            PreviewBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), _previewBuffer, size * 4, 0);
        }

        public void SaveStateForUndo()
        {
            _historyService.SaveState(SpriteState);
        }

        public void ApplyFloodFill(int index, bool newState)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.ApplyFloodFill(SpriteState, index, newState);
            _lastClickedIndex = index;
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        public void DrawLine(int startIdx, int endIdx, bool newState)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.DrawLine(SpriteState, startIdx, endIdx, newState);
            _lastClickedIndex = endIdx;
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        public void DrawLineContinuous(int startIdx, int endIdx, bool newState)
        {
            // Draws the line without saving to the Undo history stack
            _drawingService.DrawLine(SpriteState, startIdx, endIdx, newState);

            // Updates the visual brushes (using the optimized diffing we added earlier)
            RedrawGridFromMemory();
        }

        public void ShiftGrid(int offsetX, int offsetY)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.ShiftGrid(SpriteState, offsetX, offsetY);
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        public void SetPixel(int index, bool state)
        {
            if (SpriteState.Pixels[index] != state)
            {
                SpriteState.Pixels[index] = state;
                RedrawGridFromMemory();
            }
        }

        public void ParseBinaryToState(string text)
        {
            _codeGen.ParseBinaryToState(text, SpriteState);
            UpdateTextOutputs();
        }

        public void ParseHexToState(string text)
        {
            _codeGen.ParseHexToState(text, SpriteState);
            UpdateTextOutputs();
        }

        public (string Binary, string Hex) GenerateExportStrings(bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH)
        {
            return _codeGen.GenerateExportStrings(SpriteState, isFloating, floatingPixels, floatX, floatY, floatW, floatH, BinaryUseComma, HexUseComma);
        }

        public async System.Threading.Tasks.Task UpdateTextOutputsAsync()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            var (binary, hex) = await _codeGen.GenerateExportStringsAsync(
                SpriteState, IsFloating, FloatingPixels, FloatingX, FloatingY, FloatingWidth, FloatingHeight, BinaryUseComma, HexUseComma);

            // Marshal back to the UI thread generically
            _uiContext.Post(_ =>
            {
                _txtBinary = binary;
                _txtHex = hex;
                OnPropertyChanged(nameof(TxtBinary));
                OnPropertyChanged(nameof(TxtHex));
            }, null);

            _isUpdatingProgrammatically = false;
        }

        // Backwards-compatible synchronous wrapper
        public void UpdateTextOutputs()
        {
            // Fire-and-forget is now safe because the inner method explicitly handles UI thread marshaling
            _ = UpdateTextOutputsAsync();
        }

        public void SyncFloatingState(bool isFloating, bool[,] pixels, int x, int y, int w, int h)
        {
            IsFloating = isFloating;
            FloatingPixels = pixels;
            FloatingX = x;
            FloatingY = y;
            FloatingWidth = w;
            FloatingHeight = h;
        }

        public void SetSelectionBounds(bool hasSelection, int minX, int maxX, int minY, int maxY)
        {
            HasActiveSelection = hasSelection;
            SelMinX = minX;
            SelMaxX = maxX;
            SelMinY = minY;
            SelMaxY = maxY;
        }
        #endregion

        #region Private Methods
        private void DeleteSelection()
        {
            if (CurrentTool == ToolMode.Marquee && HasActiveSelection)
            {
                SaveStateForUndo();
                for (int i = 0; i < SpriteState.Pixels.Length; i++)
                {
                    int x = i % SpriteState.Size;
                    int y = i / SpriteState.Size;

                    if (x >= SelMinX && x <= SelMaxX && y >= SelMinY && y <= SelMaxY)
                    {
                        SpriteState.Pixels[i] = false;
                    }
                }
                RedrawGridFromMemory();
                UpdateTextOutputs();
            }
        }
        #endregion
    }
}