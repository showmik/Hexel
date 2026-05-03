using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Controllers;
using Hexel.Core;
using Hexel.Rendering;
using Hexel.Services;
using System;
using System.Collections.Generic;
using System.IO;
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
        public IDrawingService DrawingService => _drawingService;
        private readonly IHistoryService _historyService;
        public IHistoryService HistoryService => _historyService;
        private readonly ISelectionService _selectionService;
        public ISelectionService SelectionService => _selectionService;
        private readonly IClipboardService _clipboardService;
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly SynchronizationContext _uiContext;

        // ── Document identity ─────────────────────────────────────────────
        private string? _filePath;
        public string? FilePath
        {
            get => _filePath;
            set { if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(Title)); }
        }

        private bool _isDirty;
        public bool IsDirty
        {
            get => _isDirty;
            set { if (SetProperty(ref _isDirty, value)) OnPropertyChanged(nameof(Title)); }
        }

        public string Title => IsDirty
            ? $"*{DisplayName}"
            : DisplayName;

        private string DisplayName => FilePath != null
            ? Path.GetFileNameWithoutExtension(FilePath)
            : "Untitled";

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        // ── Display preset (static, shared with dialogs) ──────────────────

        /// <summary>
        /// Common OLED/embedded display presets. Each entry is "Label|WxH".
        /// The View binds to this list to populate the preset ComboBox.
        /// </summary>
        public static IReadOnlyList<string> DisplayPresets { get; } = new[]
        {
            "Custom",
            "8×8 Icon",
            "16×16 Sprite",
            "32×32 Tile",
            "64×64 Large",
            "84×48 Nokia 5110",
            "128×32 SSD1306 Mini",
            "128×64 SSD1306",
            "160×128 ST7735",
            "240×135 ST7789 Mini",
            "240×240 ST7789",
            "296×128 e-Paper",
            "320×240 ILI9341",
        };

        public string CanvasDimensionText => $"{SpriteState?.Width ?? 16}×{SpriteState?.Height ?? 16}";

        private int _cursorX;
        public int CursorX
        {
            get => _cursorX;
            set => SetProperty(ref _cursorX, value);
        }

        private int _cursorY;
        public int CursorY
        {
            get => _cursorY;
            set => SetProperty(ref _cursorY, value);
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

        private double _zoomLevel = 1.0;
        public double ZoomLevel
        {
            get => _zoomLevel;
            set => SetProperty(ref _zoomLevel, value);
        }

        private int _brushSize = 1;
        public int BrushSize
        {
            get => _brushSize;
            set => SetProperty(ref _brushSize, Math.Clamp(value, 1, 64));
        }

        private BrushShape _brushShape = BrushShape.Circle;
        public BrushShape BrushShape
        {
            get => _brushShape;
            set => SetProperty(ref _brushShape, value);
        }

        private int _brushAngle = 0;
        public int BrushAngle
        {
            get => _brushAngle;
            set => SetProperty(ref _brushAngle, ((value % 360) + 360) % 360);
        }

        // Flags read by the View to know whether a shape preview is in progress
        public bool IsDrawingLine => _toolInput.IsDrawingLine;
        public bool IsDrawingRectangle => _toolInput.IsDrawingRectangle;
        public bool IsDrawingEllipse => _toolInput.IsDrawingEllipse;
        public bool IsDrawingFilledRectangle => _toolInput.IsDrawingFilledRectangle;
        public bool IsDrawingFilledEllipse => _toolInput.IsDrawingFilledEllipse;

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

        // ── Internal accessors for rendering subsystem ────────────────────
        internal uint[] CanvasBuffer => _canvasBuffer;
        internal uint[] PreviewBuffer => _previewBuffer;
        internal uint ColorOnUint => _colorOnUint;
        internal uint ColorOffUint => _colorOffUint;
        internal uint PreviewOnUint => _previewOnUint;
        internal uint PreviewOffUint => _previewOffUint;

        // ── Extracted subsystems ──────────────────────────────────────────
        private ToolInputController _toolInput = null!;
        private BitmapPreviewRenderer _previewRenderer = null!;

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>
        /// Raised after Undo/Redo so the View can clear any in-progress selection
        /// overlays that may now be invalid.
        /// </summary>
        public event EventHandler? HistoryRestored;

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand UndoCommand { get; }
        public IRelayCommand RedoCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand InvertCommand { get; }
        public IRelayCommand DeleteSelectionCommand { get; }
        public IRelayCommand CopyHexCommand { get; }

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
                IsDirty = true;
                Array.Clear(SpriteState.Pixels, 0, SpriteState.Pixels.Length);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            });

            InvertCommand = new RelayCommand(() =>
            {
                _historyService.SaveState(SpriteState);
                IsDirty = true;
                _drawingService.InvertGrid(SpriteState);
                IsDisplayInverted = !IsDisplayInverted;
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
            InitializeBrushColors();
            _previewRenderer = new BitmapPreviewRenderer(this);
            _toolInput = new ToolInputController(this, _drawingService, _previewRenderer);
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
            OnPropertyChanged(nameof(CanvasDimensionText));
        }

        /// <summary>
        /// Resizes the canvas to <paramref name="newW"/>×<paramref name="newH"/>,
        /// preserving existing pixel data positioned according to the specified
        /// <paramref name="anchor"/>. Pixels falling outside the new bounds are cropped.
        /// </summary>
        public void ResizeCanvas(int newW, int newH, ResizeAnchor anchor)
        {
            var oldState = SpriteState;
            int oldW = oldState.Width;
            int oldH = oldState.Height;

            // Cancel any in-progress selection before resizing
            _selectionService.Cancel();

            // Push current state for undo
            _historyService.SaveState(oldState);
            IsDirty = true;

            // Compute where the old content should be placed in the new canvas
            var (offsetX, offsetY) = ComputeAnchorOffset(oldW, oldH, newW, newH, anchor);

            // Create the new state and copy pixels
            var newState = new SpriteState(newW, newH)
            {
                IsDisplayInverted = oldState.IsDisplayInverted
            };

            for (int y = 0; y < oldH; y++)
            {
                int destY = y + offsetY;
                if (destY < 0 || destY >= newH) continue;

                for (int x = 0; x < oldW; x++)
                {
                    int destX = x + offsetX;
                    if (destX < 0 || destX >= newW) continue;

                    newState.Pixels[(destY * newW) + destX] = oldState.Pixels[(y * oldW) + x];
                }
            }

            // Re-initialize bitmaps for the new size and apply the new state
            SpriteState = newState;
            CanvasBitmap = new WriteableBitmap(newW, newH, 96, 96, PixelFormats.Bgra32, null);
            PreviewBitmap = new WriteableBitmap(newW, newH, 96, 96, PixelFormats.Bgra32, null);
            _canvasBuffer = new uint[newW * newH];
            _previewBuffer = new uint[newW * newH];

            RedrawGridFromMemory();
            UpdateTextOutputs();

            OnPropertyChanged(nameof(GridViewport));
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(CanvasDisplayWidth));
            OnPropertyChanged(nameof(CanvasDisplayHeight));
            OnPropertyChanged(nameof(DynamicStrokeThickness));
            OnPropertyChanged(nameof(CanvasDimensionText));
        }

        /// <summary>
        /// Computes the pixel offset at which the old content should be placed
        /// within the new canvas, based on the anchor position.
        /// </summary>
        private static (int offsetX, int offsetY) ComputeAnchorOffset(
            int oldW, int oldH, int newW, int newH, ResizeAnchor anchor)
        {
            int dx = newW - oldW;
            int dy = newH - oldH;

            return anchor switch
            {
                ResizeAnchor.TopLeft      => (0,      0),
                ResizeAnchor.TopCenter    => (dx / 2, 0),
                ResizeAnchor.TopRight     => (dx,     0),
                ResizeAnchor.CenterLeft   => (0,      dy / 2),
                ResizeAnchor.Center       => (dx / 2, dy / 2),
                ResizeAnchor.CenterRight  => (dx,     dy / 2),
                ResizeAnchor.BottomLeft   => (0,      dy),
                ResizeAnchor.BottomCenter => (dx / 2, dy),
                ResizeAnchor.BottomRight  => (dx,     dy),
                _ => (0, 0)
            };
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
        /// Delegates to the extracted ToolInputController.
        /// </summary>
        public void ProcessToolInput(int x, int y, ToolAction action, DrawMode mode, bool isShiftDown, bool isAltDown = false)
            => _toolInput.ProcessToolInput(x, y, action, mode, isShiftDown, isAltDown);

        public void SaveStateForUndo()
        {
            _historyService.SaveState(SpriteState);
            IsDirty = true;
        }

        public void ShiftGrid(int offsetX, int offsetY)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.ShiftGrid(SpriteState, offsetX, offsetY);
            RedrawGridFromMemory();
            UpdateTextOutputs();
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

        /// <summary>
        /// Cancels any in-progress shape drawing and resets tracking state.
        /// Called by the View when switching tools to prevent stale draw flags.
        /// </summary>
        public void CancelInProgressDrawing() => _toolInput.CancelInProgressDrawing();

        // ── Private: pixel helpers ────────────────────────────────────────

        private void SetPixel(int x, int y, bool state)
        {
            if (x < 0 || x >= SpriteState.Width || y < 0 || y >= SpriteState.Height) return;
            int index = (y * SpriteState.Width) + x;
            if (SpriteState.Pixels[index] == state) return;
            SpriteState.Pixels[index] = state;
            RedrawGridFromMemory();
        }

        // ── Private: history restore ──────────────────────────────────────

        private void RestoreState(SpriteState state)
        {
            if (state == null || ReferenceEquals(state, SpriteState)) return;

            // Cancel any in-progress selection or floating layer
            _selectionService.Cancel();

            if (SpriteState.Width != state.Width || SpriteState.Height != state.Height)
            {
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
