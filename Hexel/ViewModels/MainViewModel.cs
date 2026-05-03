using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using Hexel.Services;
using System;
using System.Collections.Generic;
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
        private readonly ISelectionService _selectionService;
        private readonly IClipboardService _clipboardService;
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly SynchronizationContext _uiContext;

        // ── Canvas size input ─────────────────────────────────────────────
        private string _inputWidth = "16";
        private string _inputHeight = "16";
        private bool _isApplyingPreset; // prevents circular reset of SelectedPreset

        public string InputWidth
        {
            get => _inputWidth;
            set
            {
                if (!SetProperty(ref _inputWidth, value)) return;
                // Manual edit → drop preset to Custom
                if (!_isApplyingPreset && _selectedPreset != "Custom")
                {
                    _selectedPreset = "Custom";
                    OnPropertyChanged(nameof(SelectedPreset));
                }
            }
        }
        public string InputHeight
        {
            get => _inputHeight;
            set
            {
                if (!SetProperty(ref _inputHeight, value)) return;
                if (!_isApplyingPreset && _selectedPreset != "Custom")
                {
                    _selectedPreset = "Custom";
                    OnPropertyChanged(nameof(SelectedPreset));
                }
            }
        }

        // ── Resize anchor ─────────────────────────────────────────────────
        private ResizeAnchor _resizeAnchor = ResizeAnchor.TopLeft;
        public ResizeAnchor ResizeAnchor
        {
            get => _resizeAnchor;
            set => SetProperty(ref _resizeAnchor, value);
        }

        // ── Display preset ────────────────────────────────────────────────
        private string _selectedPreset = "Custom";
        public string SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (!SetProperty(ref _selectedPreset, value) || value == "Custom") return;
                if (ApplyPresetCommand == null) return; // not yet initialized during construction
                ApplyPresetCommand.Execute(value);
            }
        }

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

        private int _brushSize = 1;
        public int BrushSize
        {
            get => _brushSize;
            set => SetProperty(ref _brushSize, Math.Clamp(value, 1, 16));
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
        private const int NoPosition = int.MinValue;
        private int _lineStartX = NoPosition;
        private int _lineStartY = NoPosition;
        private int _lineCurrentX = NoPosition;
        private int _lineCurrentY = NoPosition;
        private bool _lineDrawState = false;
        private int _lastClickedX = NoPosition;
        private int _lastClickedY = NoPosition;
        private bool _pendingTextUpdateDuringDrag;
        private bool _lastShiftDown;
        private bool _lastAltDown;

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>
        /// Raised after Undo/Redo so the View can clear any in-progress selection
        /// overlays that may now be invalid.
        /// </summary>
        public event EventHandler? HistoryRestored;

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand NewCanvasCommand { get; }
        public IRelayCommand ResizeCanvasCommand { get; }
        public IRelayCommand<string> ApplyPresetCommand { get; }
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

            ResizeCanvasCommand = new RelayCommand(() =>
            {
                if (!int.TryParse(InputWidth, out int w) || w <= 0 ||
                    !int.TryParse(InputHeight, out int h) || h <= 0)
                {
                    InputWidth = (SpriteState?.Width ?? 16).ToString();
                    InputHeight = (SpriteState?.Height ?? 16).ToString();
                    return;
                }

                if (w > SpriteState.MaxDimension || h > SpriteState.MaxDimension)
                {
                    _dialogService.ShowMessage(
                        $"Maximum canvas size is {SpriteState.MaxDimension}×{SpriteState.MaxDimension}.");
                    return;
                }

                // Same dimensions → nothing to do
                if (w == SpriteState.Width && h == SpriteState.Height) return;

                ResizeCanvas(w, h, ResizeAnchor);
            });

            ApplyPresetCommand = new RelayCommand<string>(preset =>
            {
                if (string.IsNullOrEmpty(preset) || preset == "Custom") return;

                // Parse "WxH" from the front of the preset string (e.g. "128×64 SSD1306")
                var label = preset.Split(' ')[0]; // "128×64"
                // Handle both '×' (Unicode multiplication sign) and 'x' (ASCII)
                var parts = label.Split(new[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int pw) &&
                    int.TryParse(parts[1], out int ph))
                {
                    _isApplyingPreset = true;
                    InputWidth = pw.ToString();
                    InputHeight = ph.ToString();
                    _isApplyingPreset = false;
                }
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

            InputWidth = newW.ToString();
            InputHeight = newH.ToString();

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
        /// Uses typed enums instead of the previous magic strings and bool? tri-state.
        /// </summary>
        public void ProcessToolInput(int x, int y, ToolAction action, DrawMode mode, bool isShiftDown, bool isAltDown = false)
        {
            // Marquee and Lasso are handled entirely in the View via SelectionService
            if (CurrentTool == ToolMode.Marquee || CurrentTool == ToolMode.Lasso) return;

            switch (action)
            {
                case ToolAction.Down:
                    HandleToolDown(x, y, mode, isShiftDown);
                    break;

                case ToolAction.Move:
                    HandleToolMove(x, y, mode, isShiftDown, isAltDown);
                    break;

                case ToolAction.Up:
                    HandleToolUp(isShiftDown, isAltDown);
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

        public void PreviewLine(int x0, int y0, int x1, int y1, bool newState)
        {
            RedrawGridFromMemory();
            PlotLine(x0, y0, x1, y1, newState ? _colorOnUint : _colorOffUint,
                                       newState ? _previewOnUint : _previewOffUint);
            FlushBitmaps();
        }

        public void PreviewRectangle(int x0, int y0, int x1, int y1, bool newState)
        {
            RedrawGridFromMemory();
            PlotRectangle(x0, y0, x1, y1, newState ? _colorOnUint : _colorOffUint,
                                             newState ? _previewOnUint : _previewOffUint);
            FlushBitmaps();
        }

        public void PreviewEllipse(int x0, int y0, int x1, int y1, bool newState)
        {
            RedrawGridFromMemory();
            PlotEllipse(x0, y0, x1, y1, newState ? _colorOnUint : _colorOffUint,
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

        private void HandleToolDown(int x, int y, DrawMode mode, bool isShiftDown)
        {
            bool newState = mode == DrawMode.Draw;

            switch (CurrentTool)
            {
                case ToolMode.Fill:
                    // BUG FIX: previously SaveStateForUndo() was called here AND inside
                    // ApplyFloodFill, resulting in two undo entries per fill operation.
                    // History is now saved exactly once, here at the call site.
                    _historyService.SaveState(SpriteState);
                    _drawingService.ApplyFloodFill(SpriteState, x, y, newState);
                    _lastClickedX = x;
                    _lastClickedY = y;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    break;

                case ToolMode.Line:
                    IsDrawingLine = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    PreviewLine(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Rectangle:
                    IsDrawingRectangle = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    PreviewRectangle(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Ellipse:
                    IsDrawingEllipse = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    PreviewEllipse(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Pencil:
                    if (isShiftDown && _lastClickedX != NoPosition)
                    {
                        _historyService.SaveState(SpriteState);
                        _drawingService.DrawLine(SpriteState, _lastClickedX, _lastClickedY, x, y, newState, BrushSize);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        RedrawGridFromMemory();
                        UpdateTextOutputs();
                    }
                    else
                    {
                        _historyService.SaveState(SpriteState);
                        _drawingService.DrawBrushStamp(SpriteState, x, y, BrushSize, newState);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        RedrawGridFromMemory();
                        UpdateTextOutputs();
                    }
                    break;
            }
        }

        private (int x0, int y0, int x1, int y1) GetConstrainedShapeBounds(int startX, int startY, int currentX, int currentY, ToolMode tool, bool isShift, bool isAlt)
        {
            int targetX = currentX;
            int targetY = currentY;

            if (tool == ToolMode.Line)
            {
                if (isShift)
                {
                    double angle = Math.Atan2(targetY - startY, targetX - startX);
                    angle = Math.Round(angle / (Math.PI / 12.0)) * (Math.PI / 12.0);
                    double dist = Math.Sqrt(Math.Pow(targetX - startX, 2) + Math.Pow(targetY - startY, 2));
                    targetX = startX + (int)Math.Round(Math.Cos(angle) * dist);
                    targetY = startY + (int)Math.Round(Math.Sin(angle) * dist);
                }

                int x0 = startX;
                int y0 = startY;
                if (isAlt)
                {
                    x0 = 2 * startX - targetX;
                    y0 = 2 * startY - targetY;
                }
                return (x0, y0, targetX, targetY);
            }
            else
            {
                if (isShift)
                {
                    int dx = currentX - startX;
                    int dy = currentY - startY;
                    int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    targetX = startX + (dx >= 0 ? side : -side);
                    targetY = startY + (dy >= 0 ? side : -side);
                }

                int x0 = startX;
                int y0 = startY;
                if (isAlt)
                {
                    x0 = 2 * startX - targetX;
                    y0 = 2 * startY - targetY;
                }
                return (x0, y0, targetX, targetY);
            }
        }

        private void HandleToolMove(int x, int y, DrawMode mode, bool isShiftDown, bool isAltDown)
        {
            bool newState = mode == DrawMode.Draw;

            switch (CurrentTool)
            {
                case ToolMode.Line when IsDrawingLine:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Line, isShiftDown, isAltDown);
                        PreviewLine(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Rectangle when IsDrawingRectangle:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Rectangle, isShiftDown, isAltDown);
                        PreviewRectangle(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Ellipse when IsDrawingEllipse:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Ellipse, isShiftDown, isAltDown);
                        PreviewEllipse(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Pencil when mode != DrawMode.None:
                    if (_lastClickedX != NoPosition && (_lastClickedX != x || _lastClickedY != y))
                    {
                        // Continuous pencil drag: draw line segment but don't push undo
                        // (the undo entry was already pushed on Down)
                        _drawingService.DrawLine(SpriteState, _lastClickedX, _lastClickedY, x, y, newState, BrushSize);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        _pendingTextUpdateDuringDrag = true;
                        RedrawGridFromMemory();
                    }
                    break;
            }
        }

        private void HandleToolUp(bool isShiftDown, bool isAltDown)
        {
            if (IsDrawingLine)
            {
                IsDrawingLine = false;
                if (_lineStartX != NoPosition)
                {
                    _historyService.SaveState(SpriteState);
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Line, isShiftDown, isAltDown);
                    _drawingService.DrawLine(SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    RedrawGridFromMemory();
                }
                ResetLineTracking();
                UpdateTextOutputs();
            }

            if (IsDrawingRectangle)
            {
                IsDrawingRectangle = false;
                if (_lineStartX != NoPosition)
                {
                    _historyService.SaveState(SpriteState);
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Rectangle, isShiftDown, isAltDown);
                    _drawingService.DrawRectangle(SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    RedrawGridFromMemory();
                }
                ResetLineTracking();
                UpdateTextOutputs();
            }

            if (IsDrawingEllipse)
            {
                IsDrawingEllipse = false;
                if (_lineStartX != NoPosition)
                {
                    _historyService.SaveState(SpriteState);
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Ellipse, isShiftDown, isAltDown);
                    _drawingService.DrawEllipse(SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
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

        /// <summary>
        /// Cancels any in-progress shape drawing and resets tracking state.
        /// Called by the View when switching tools to prevent stale draw flags.
        /// </summary>
        public void CancelInProgressDrawing()
        {
            if (IsDrawingLine || IsDrawingRectangle || IsDrawingEllipse)
            {
                IsDrawingLine = false;
                IsDrawingRectangle = false;
                IsDrawingEllipse = false;
                ResetLineTracking();
                RedrawGridFromMemory();  // remove any shape preview
            }
            _pendingTextUpdateDuringDrag = false;
        }

        private void ResetLineTracking()
        {
            _lineStartX = NoPosition;
            _lineStartY = NoPosition;
            _lineCurrentX = NoPosition;
            _lineCurrentY = NoPosition;
        }

        // ── Private: pixel helpers ────────────────────────────────────────

        private void SetPixel(int x, int y, bool state)
        {
            if (x < 0 || x >= SpriteState.Width || y < 0 || y >= SpriteState.Height) return;
            int index = (y * SpriteState.Width) + x;
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

        private void PlotLine(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
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

        private void PlotRectangle(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            for (int x = minX; x <= maxX; x++) { PlotPixel(x, minY, cc, pc); PlotPixel(x, maxY, cc, pc); }
            for (int y = minY; y <= maxY; y++) { PlotPixel(minX, y, cc, pc); PlotPixel(maxX, y, cc, pc); }
        }

        private void PlotEllipse(int x0, int y0, int x1, int y1, uint cc, uint pc)
        {
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
