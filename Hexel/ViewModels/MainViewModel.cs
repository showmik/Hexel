using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using Hexel.Services;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hexel.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        // ── Services ──────────────────────────────────────────────────────
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IHistoryService _historyService;
        private readonly ISelectionService _selectionService;
        private readonly IClipboardService _clipboardService;
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly SynchronizationContext _uiContext;

        // ── Canvas size input ─────────────────────────────────────────────
        private string _inputWidth = "16";
        private string _inputHeight = "16";

        public string InputWidth
        {
            get => _inputWidth;
            set => SetProperty(ref _inputWidth, value);
        }
        public string InputHeight
        {
            get => _inputHeight;
            set => SetProperty(ref _inputHeight, value);
        }

        // ── Core state ────────────────────────────────────────────────────
        private SpriteState _spriteState = null!;
        public SpriteState SpriteState
        {
            get => _spriteState;
            private set => SetProperty(ref _spriteState, value);
        }

        private bool _isDisplayInverted;
        public bool IsDisplayInverted
        {
            get => _isDisplayInverted;
            set
            {
                if (SetProperty(ref _isDisplayInverted, value) && SpriteState != null)
                    SpriteState.IsDisplayInverted = value;
            }
        }

        private bool _showGridLines = true;
        public bool ShowGridLines
        {
            get => _showGridLines;
            set => SetProperty(ref _showGridLines, value);
        }

        // ── Tool state ────────────────────────────────────────────────────
        private ToolMode _currentTool = ToolMode.Pencil;
        public ToolMode CurrentTool
        {
            get => _currentTool;
            set => SetProperty(ref _currentTool, value);
        }

        // Flags read by the View to know whether a shape preview is in progress
        public bool IsDrawingLine { get; private set; }
        public bool IsDrawingRectangle { get; private set; }
        public bool IsDrawingEllipse { get; private set; }

        // ── Export format options ─────────────────────────────────────────
        private bool _binaryUseComma = false;
        public bool BinaryUseComma
        {
            get => _binaryUseComma;
            set { if (SetProperty(ref _binaryUseComma, value)) UpdateTextOutputs(); }
        }

        private bool _hexUseComma = true;
        public bool HexUseComma
        {
            get => _hexUseComma;
            set { if (SetProperty(ref _hexUseComma, value)) UpdateTextOutputs(); }
        }

        // ── Text output properties ────────────────────────────────────────
        // volatile so the flag is visible across threads without a full lock
        private volatile bool _isUpdatingProgrammatically;

        private string _txtBinary = string.Empty;
        public string TxtBinary
        {
            get => _txtBinary;
            set
            {
                if (_isUpdatingProgrammatically) return;
                SetProperty(ref _txtBinary, value);
                var backup = SpriteState.Clone();
                try
                {
                    _codeGen.ParseBinaryToState(value, SpriteState);
                    RedrawGridFromMemory();
                }
                catch
                {
                    SpriteState = backup;
                    RedrawGridFromMemory();
                }
                finally
                {
                    UpdateTextOutputs();
                }
            }
        }

        private string _txtHex = string.Empty;
        public string TxtHex
        {
            get => _txtHex;
            set
            {
                if (_isUpdatingProgrammatically) return;
                SetProperty(ref _txtHex, value);
                var backup = SpriteState.Clone();
                try
                {
                    _codeGen.ParseHexToState(value, SpriteState);
                    RedrawGridFromMemory();
                }
                catch
                {
                    SpriteState = backup;
                    RedrawGridFromMemory();
                }
                finally
                {
                    UpdateTextOutputs();
                }
            }
        }

        // ── Canvas display helpers ────────────────────────────────────────
        private const double CanvasTargetPx = 400.0;

        private double CellSize =>
            SpriteState != null
                ? Math.Min(CanvasTargetPx / SpriteState.Width, CanvasTargetPx / SpriteState.Height)
                : 25.0;

        public double CanvasDisplayWidth => (SpriteState?.Width ?? 16) * CellSize;
        public double CanvasDisplayHeight => (SpriteState?.Height ?? 16) * CellSize;

        public Rect GridViewport => new Rect(0, 0, CellSize, CellSize);
        public double DynamicStrokeThickness => SpriteState != null ? Math.Max(0.5, CellSize * 0.08) : 2.0;
        public int PreviewWidth => (SpriteState?.Width ?? 16) * 2;
        public int PreviewHeight => (SpriteState?.Height ?? 16) * 2;

        // ── WritableBitmaps ───────────────────────────────────────────────
        private WriteableBitmap _canvasBitmap = null!;
        public WriteableBitmap CanvasBitmap
        {
            get => _canvasBitmap;
            set => SetProperty(ref _canvasBitmap, value);
        }

        private WriteableBitmap _previewBitmap = null!;
        public WriteableBitmap PreviewBitmap
        {
            get => _previewBitmap;
            set => SetProperty(ref _previewBitmap, value);
        }

        private uint[] _canvasBuffer = Array.Empty<uint>();
        private uint[] _previewBuffer = Array.Empty<uint>();

        private uint _colorOffUint, _colorOnUint, _previewOffUint, _previewOnUint;
        private static uint ToBgra32(Color c) =>
            (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

        // ── Internal drawing tracking ─────────────────────────────────────
        private int _lineStartIdx = -1;
        private int _lineCurrentIdx = -1;
        private bool _lineDrawState = false;
        private int _lastClickedIndex = -1;
        private bool _pendingTextUpdateDuringDrag;

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>
        /// Raised after Undo/Redo so the View can clear any in-progress selection
        /// overlays that may now be invalid.
        /// </summary>
        public event EventHandler? HistoryRestored;

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand NewCanvasCommand { get; }
        public IRelayCommand UndoCommand { get; }
        public IRelayCommand RedoCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand InvertCommand { get; }
        public IRelayCommand DeleteSelectionCommand { get; }
        public IRelayCommand CopyHexCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand LoadCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────
        public MainViewModel(
            ICodeGeneratorService codeGen,
            IDrawingService drawingService,
            IHistoryService historyService,
            ISelectionService selectionService,
            IClipboardService clipboardService,
            IDialogService dialogService,
            IFileService fileService)
        {
            _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
            _codeGen = codeGen ?? throw new ArgumentNullException(nameof(codeGen));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));

            // ── Commands ──────────────────────────────────────────────────

            NewCanvasCommand = new RelayCommand(() =>
            {
                if (!int.TryParse(InputWidth, out int w) || w <= 0 ||
                    !int.TryParse(InputHeight, out int h) || h <= 0)
                {
                    InputWidth = (SpriteState?.Width ?? 16).ToString();
                    InputHeight = (SpriteState?.Height ?? 16).ToString();
                    return;
                }

                // Guard against crash-inducing sizes
                if (w > SpriteState.MaxDimension || h > SpriteState.MaxDimension)
                {
                    _dialogService.ShowMessage(
                        $"Maximum canvas size is {SpriteState.MaxDimension}×{SpriteState.MaxDimension}.");
                    return;
                }

                InitializeGrid(w, h);
            });

            SaveCommand = new RelayCommand(() =>
            {
                try { _fileService.SaveSprite(SpriteState); }
                catch (Exception ex) { _dialogService.ShowMessage($"Error saving: {ex.Message}"); }
            });

            LoadCommand = new RelayCommand(() =>
            {
                try
                {
                    var loaded = _fileService.LoadSprite();
                    if (loaded?.Pixels == null) return;

                    _historyService.SaveState(SpriteState);

                    if (InputWidth != loaded.Width.ToString() ||
                        InputHeight != loaded.Height.ToString())
                    {
                        InputWidth = loaded.Width.ToString();
                        InputHeight = loaded.Height.ToString();
                        InitializeGrid(loaded.Width, loaded.Height);
                    }

                    SpriteState.Pixels = (bool[])loaded.Pixels.Clone();
                    IsDisplayInverted = loaded.IsDisplayInverted;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                }
                catch (Exception ex) { _dialogService.ShowMessage($"Error loading: {ex.Message}"); }
            });

            // CopyHexCommand no longer shows a blocking MessageBox — the View
            // shows a non-modal status message instead via the CopyHexExecuted event.
            CopyHexCommand = new RelayCommand(() =>
            {
                _clipboardService.SetText(TxtHex);
                CopyHexExecuted?.Invoke(this, EventArgs.Empty);
            });

            UndoCommand = new RelayCommand(() => RestoreState(_historyService.Undo(SpriteState)));
            RedoCommand = new RelayCommand(() => RestoreState(_historyService.Redo(SpriteState)));

            ClearCommand = new RelayCommand(() =>
            {
                _historyService.SaveState(SpriteState);
                Array.Clear(SpriteState.Pixels, 0, SpriteState.Pixels.Length);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            InvertCommand = new RelayCommand(() =>
            {
                _historyService.SaveState(SpriteState);   // saves IsDisplayInverted = current value
                _drawingService.InvertGrid(SpriteState);
                IsDisplayInverted = !IsDisplayInverted;   // setter keeps SpriteState in sync
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            DeleteSelectionCommand = new RelayCommand(() =>
            {
                if (!_selectionService.HasActiveSelection) return;
                SaveStateForUndo();
                _selectionService.DeleteSelection(SpriteState);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            // ── Initialization ────────────────────────────────────────────
            InitializeGrid(int.Parse(InputWidth), int.Parse(InputHeight));
            InitializeBrushColors();
            UpdateTextOutputs();
        }

        /// <summary>
        /// Raised when a hex copy succeeds. The View subscribes to show a brief
        /// non-blocking status indicator instead of a MessageBox.
        /// </summary>
        public event EventHandler? CopyHexExecuted;

        // ── Public methods called by the View ─────────────────────────────

        public void InitializeGrid(int width, int height)
        {
            SpriteState = new SpriteState(width, height);

            CanvasBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            PreviewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            _canvasBuffer = new uint[width * height];
            _previewBuffer = new uint[width * height];

            UpdateTextOutputs();
            RedrawGridFromMemory();

            OnPropertyChanged(nameof(GridViewport));
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(CanvasDisplayWidth));
            OnPropertyChanged(nameof(CanvasDisplayHeight));
            OnPropertyChanged(nameof(DynamicStrokeThickness));
        }

        public void RedrawGridFromMemory()
        {
            if (CanvasBitmap == null || _canvasBuffer == null || SpriteState?.Pixels == null) return;

            int w = SpriteState.Width;
            int h = SpriteState.Height;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    bool isPixelOn = SpriteState.Pixels[i];

                    // Overlay the floating selection layer if one is active
                    if (_selectionService.IsFloating && _selectionService.FloatingPixels != null)
                    {
                        int fx = x - _selectionService.FloatingX;
                        int fy = y - _selectionService.FloatingY;
                        if (fx >= 0 && fx < _selectionService.FloatingWidth &&
                            fy >= 0 && fy < _selectionService.FloatingHeight &&
                            _selectionService.FloatingPixels[fx, fy])
                        {
                            isPixelOn = true;
                        }
                    }

                    _canvasBuffer[i] = isPixelOn ? _colorOnUint : _colorOffUint;
                    _previewBuffer[i] = isPixelOn ? _previewOnUint : _previewOffUint;
                }
            }

            var rect = new Int32Rect(0, 0, w, h);
            CanvasBitmap.WritePixels(rect, _canvasBuffer, w * 4, 0);
            PreviewBitmap.WritePixels(rect, _previewBuffer, w * 4, 0);
        }

        /// <summary>
        /// Main entry point for all non-selection tool input from the View.
        /// Uses typed enums instead of the previous magic strings and bool? tri-state.
        /// </summary>
        public void ProcessToolInput(int index, ToolAction action, DrawMode mode, bool isShiftDown)
        {
            // Marquee and Lasso are handled entirely in the View via SelectionService
            if (CurrentTool == ToolMode.Marquee || CurrentTool == ToolMode.Lasso) return;

            switch (action)
            {
                case ToolAction.Down:
                    HandleToolDown(index, mode, isShiftDown);
                    break;

                case ToolAction.Move:
                    HandleToolMove(index, mode);
                    break;

                case ToolAction.Up:
                    HandleToolUp();
                    break;
            }
        }

        public void SaveStateForUndo() => _historyService.SaveState(SpriteState);

        public void ShiftGrid(int offsetX, int offsetY)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.ShiftGrid(SpriteState, offsetX, offsetY);
            RedrawGridFromMemory();
            UpdateTextOutputs();
        }

        // ── Preview methods (used during shape drag, do not commit to state) ──

        public void PreviewLine(int startIdx, int endIdx, bool newState)
        {
            RedrawGridFromMemory();
            PlotLine(startIdx, endIdx, newState ? _colorOnUint : _colorOffUint,
                                       newState ? _previewOnUint : _previewOffUint);
            FlushBitmaps();
        }

        public void PreviewRectangle(int startIdx, int endIdx, bool newState)
        {
            RedrawGridFromMemory();
            PlotRectangle(startIdx, endIdx, newState ? _colorOnUint : _colorOffUint,
                                             newState ? _previewOnUint : _previewOffUint);
            FlushBitmaps();
        }

        public void PreviewEllipse(int startIdx, int endIdx, bool newState)
        {
            RedrawGridFromMemory();
            PlotEllipse(startIdx, endIdx, newState ? _colorOnUint : _colorOffUint,
                                           newState ? _previewOnUint : _previewOffUint);
            FlushBitmaps();
        }

        // ── Text output ───────────────────────────────────────────────────

        public void UpdateTextOutputs() => _ = UpdateTextOutputsAsync();

        public async System.Threading.Tasks.Task UpdateTextOutputsAsync()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            try
            {
                var (binary, hex) = await _codeGen.GenerateExportStringsAsync(
                    SpriteState,
                    _selectionService.IsFloating,
                    _selectionService.FloatingPixels,
                    _selectionService.FloatingX,
                    _selectionService.FloatingY,
                    _selectionService.FloatingWidth,
                    _selectionService.FloatingHeight,
                    BinaryUseComma,
                    HexUseComma);

                // Only reset the flag inside the Post callback — resetting it earlier
                // (before the callback runs) allowed a second async call to start
                // before the UI had actually been updated, causing a race condition.
                _uiContext.Post(_ =>
                {
                    _txtBinary = binary;
                    _txtHex = hex;
                    OnPropertyChanged(nameof(TxtBinary));
                    OnPropertyChanged(nameof(TxtHex));
                    _isUpdatingProgrammatically = false;
                }, null);
            }
            catch
            {
                // Always release the flag if the async work itself throws
                _isUpdatingProgrammatically = false;
            }
        }

        // ── Private: tool dispatch ────────────────────────────────────────

        private void HandleToolDown(int index, DrawMode mode, bool isShiftDown)
        {
            bool newState = mode == DrawMode.Draw;

            switch (CurrentTool)
            {
                case ToolMode.Fill:
                    // BUG FIX: previously SaveStateForUndo() was called here AND inside
                    // ApplyFloodFill, resulting in two undo entries per fill operation.
                    // History is now saved exactly once, here at the call site.
                    _historyService.SaveState(SpriteState);
                    _drawingService.ApplyFloodFill(SpriteState, index, newState);
                    _lastClickedIndex = index;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    break;

                case ToolMode.Line:
                    IsDrawingLine = true;
                    _lineStartIdx = index;
                    _lineCurrentIdx = index;
                    _lineDrawState = newState;
                    PreviewLine(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    break;

                case ToolMode.Rectangle:
                    IsDrawingRectangle = true;
                    _lineStartIdx = index;
                    _lineCurrentIdx = index;
                    _lineDrawState = newState;
                    PreviewRectangle(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    break;

                case ToolMode.Ellipse:
                    IsDrawingEllipse = true;
                    _lineStartIdx = index;
                    _lineCurrentIdx = index;
                    _lineDrawState = newState;
                    PreviewEllipse(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    break;

                case ToolMode.Pencil:
                    if (isShiftDown && _lastClickedIndex != -1)
                    {
                        _historyService.SaveState(SpriteState);
                        _drawingService.DrawLine(SpriteState, _lastClickedIndex, index, newState);
                        _lastClickedIndex = index;
                        RedrawGridFromMemory();
                        UpdateTextOutputs();
                    }
                    else
                    {
                        _historyService.SaveState(SpriteState);
                        SetPixel(index, newState);
                        _lastClickedIndex = index;
                        UpdateTextOutputs();
                    }
                    break;
            }
        }

        private void HandleToolMove(int index, DrawMode mode)
        {
            bool newState = mode == DrawMode.Draw;

            switch (CurrentTool)
            {
                case ToolMode.Line when IsDrawingLine:
                    if (_lineCurrentIdx != index)
                    {
                        _lineCurrentIdx = index;
                        PreviewLine(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    }
                    break;

                case ToolMode.Rectangle when IsDrawingRectangle:
                    if (_lineCurrentIdx != index)
                    {
                        _lineCurrentIdx = index;
                        PreviewRectangle(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    }
                    break;

                case ToolMode.Ellipse when IsDrawingEllipse:
                    if (_lineCurrentIdx != index)
                    {
                        _lineCurrentIdx = index;
                        PreviewEllipse(_lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    }
                    break;

                case ToolMode.Pencil when mode != DrawMode.None:
                    if (_lastClickedIndex != -1 && _lastClickedIndex != index)
                    {
                        // Continuous pencil drag: draw line segment but don't push undo
                        // (the undo entry was already pushed on Down)
                        _drawingService.DrawLine(SpriteState, _lastClickedIndex, index, newState);
                        _lastClickedIndex = index;
                        _pendingTextUpdateDuringDrag = true;
                        RedrawGridFromMemory();
                    }
                    break;
            }
        }

        private void HandleToolUp()
        {
            if (IsDrawingLine)
            {
                IsDrawingLine = false;
                if (_lineStartIdx != -1 && _lineCurrentIdx != -1)
                {
                    _historyService.SaveState(SpriteState);
                    _drawingService.DrawLine(SpriteState, _lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    _lastClickedIndex = _lineCurrentIdx;
                    RedrawGridFromMemory();
                }
                ResetLineTracking();
                UpdateTextOutputs();
            }

            if (IsDrawingRectangle)
            {
                IsDrawingRectangle = false;
                if (_lineStartIdx != -1 && _lineCurrentIdx != -1)
                {
                    _historyService.SaveState(SpriteState);
                    _drawingService.DrawRectangle(SpriteState, _lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    _lastClickedIndex = _lineCurrentIdx;
                    RedrawGridFromMemory();
                }
                ResetLineTracking();
                UpdateTextOutputs();
            }

            if (IsDrawingEllipse)
            {
                IsDrawingEllipse = false;
                if (_lineStartIdx != -1 && _lineCurrentIdx != -1)
                {
                    _historyService.SaveState(SpriteState);
                    _drawingService.DrawEllipse(SpriteState, _lineStartIdx, _lineCurrentIdx, _lineDrawState);
                    _lastClickedIndex = _lineCurrentIdx;
                    RedrawGridFromMemory();
                }
                ResetLineTracking();
                UpdateTextOutputs();
            }

            if (CurrentTool == ToolMode.Pencil && _pendingTextUpdateDuringDrag)
            {
                _pendingTextUpdateDuringDrag = false;
                UpdateTextOutputs();
            }
        }

        private void ResetLineTracking()
        {
            _lineStartIdx = -1;
            _lineCurrentIdx = -1;
        }

        // ── Private: pixel helpers ────────────────────────────────────────

        private void SetPixel(int index, bool state)
        {
            if (SpriteState.Pixels[index] == state) return;
            SpriteState.Pixels[index] = state;
            RedrawGridFromMemory();
        }

        // ── Private: bitmap plotting (for previews) ───────────────────────

        private void PlotPixel(int x, int y, uint canvasColor, uint previewColor)
        {
            int w = SpriteState.Width;
            int h = SpriteState.Height;
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int i = (y * w) + x;
            _canvasBuffer[i] = canvasColor;
            _previewBuffer[i] = previewColor;
        }

        private void PlotLine(int startIdx, int endIdx, uint cc, uint pc)
        {
            int w = SpriteState.Width;
            int x0 = startIdx % w, y0 = startIdx / w;
            int x1 = endIdx % w, y1 = endIdx / w;
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                PlotPixel(x0, y0, cc, pc);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private void PlotRectangle(int startIdx, int endIdx, uint cc, uint pc)
        {
            int w = SpriteState.Width;
            int x0 = startIdx % w, y0 = startIdx / w;
            int x1 = endIdx % w, y1 = endIdx / w;
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            for (int x = minX; x <= maxX; x++) { PlotPixel(x, minY, cc, pc); PlotPixel(x, maxY, cc, pc); }
            for (int y = minY; y <= maxY; y++) { PlotPixel(minX, y, cc, pc); PlotPixel(maxX, y, cc, pc); }
        }

        private void PlotEllipse(int startIdx, int endIdx, uint cc, uint pc)
        {
            int w = SpriteState.Width;
            int x0 = startIdx % w, y0 = startIdx / w;
            int x1 = endIdx % w, y1 = endIdx / w;

            int a = Math.Abs(x1 - x0), b = Math.Abs(y1 - y0), b1 = b & 1;
            long dx = 4L * (1 - a) * b * b, dy = 4L * (b1 + 1) * a * a;
            long err = dx + dy + (long)b1 * a * a, e2;

            if (x0 > x1) { x0 = x1; x1 += a; }
            if (y0 > y1) y0 = y1;
            y0 += (b + 1) / 2;
            y1 = y0 - b1;
            a *= 8 * a;
            b1 = 8 * b * b;

            do
            {
                PlotPixel(x1, y0, cc, pc); PlotPixel(x0, y0, cc, pc);
                PlotPixel(x0, y1, cc, pc); PlotPixel(x1, y1, cc, pc);
                e2 = 2 * err;
                if (e2 <= dy) { y0++; y1--; err += dy += a; }
                if (e2 >= dx || 2 * err > dy) { x0++; x1--; err += dx += b1; }
            }
            while (x0 <= x1);

            while (y0 - y1 < b)
            {
                PlotPixel(x0 - 1, y0, cc, pc); PlotPixel(x1 + 1, y0, cc, pc);
                PlotPixel(x0 - 1, y1, cc, pc); PlotPixel(x1 + 1, y1, cc, pc);
                y0++; y1--;
            }
        }

        private void FlushBitmaps()
        {
            var rect = new Int32Rect(0, 0, SpriteState.Width, SpriteState.Height);
            CanvasBitmap.WritePixels(rect, _canvasBuffer, SpriteState.Width * 4, 0);
            PreviewBitmap.WritePixels(rect, _previewBuffer, SpriteState.Width * 4, 0);
        }

        // ── Private: history restore ──────────────────────────────────────

        private void RestoreState(SpriteState state)
        {
            if (state == null || ReferenceEquals(state, SpriteState)) return;

            // Cancel any in-progress selection or floating layer
            _selectionService.Cancel();

            if (InputWidth != state.Width.ToString() ||
                InputHeight != state.Height.ToString())
            {
                InputWidth = state.Width.ToString();
                InputHeight = state.Height.ToString();
                InitializeGrid(state.Width, state.Height);
            }

            SpriteState = state;
            IsDisplayInverted = state.IsDisplayInverted; // restores the visual invert flag
            RedrawGridFromMemory();
            UpdateTextOutputs();
            HistoryRestored?.Invoke(this, EventArgs.Empty);
        }

        // ── Private: color initialization ─────────────────────────────────

        private void InitializeBrushColors()
        {
            var res = Application.Current.Resources;
            var colorOff = ((SolidColorBrush)res["Theme.PanelBackgroundBrush"]).Color;
            var colorOn = ((SolidColorBrush)res["Theme.PrimaryAccentBrush"]).Color;
            var prevOff = ((SolidColorBrush)res["Theme.OledOffBrush"]).Color;
            var prevOn = ((SolidColorBrush)res["Theme.OledOnBrush"]).Color;

            _colorOffUint = ToBgra32(colorOff);
            _colorOnUint = ToBgra32(colorOn);
            _previewOffUint = ToBgra32(prevOff);
            _previewOnUint = ToBgra32(prevOn);
        }
    }
}
