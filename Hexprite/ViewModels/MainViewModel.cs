using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexprite.Controllers;
using Hexprite.Core;
using Hexprite.Rendering;
using Hexprite.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hexprite.ViewModels
{
    public enum DisplayType
    {
        Generic_White,
        SSD1306_Blue,
        SSD1306_Green,
        ePaper
    }

    public class MainViewModel : ObservableObject, IBitmapBufferContext
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
        private readonly IPixelClipboardService _pixelClipboard;
        private readonly IDialogService _dialogService;
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

        // ── Status message (data-bound, replaces direct label manipulation) ──
        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>
        /// Shows a temporary status message that auto-clears after a delay.
        /// Replaces the old CopyHexExecuted event + DispatcherTimer approach.
        /// </summary>
        public async void ShowStatus(string message, int delayMs = 2000)
        {
            StatusMessage = message;
            await System.Threading.Tasks.Task.Delay(delayMs);
            // Only clear if no newer message replaced this one
            if (StatusMessage == message)
                StatusMessage = string.Empty;
        }

        // ── Export settings ───────────────────────────────────────────────
        private ExportSettings _exportSettings = new();
        public ExportSettings ExportSettings
        {
            get => _exportSettings;
            private set => SetProperty(ref _exportSettings, value);
        }

        // Convenience proxy: binds directly in the sidebar without deep binding paths
        public ExportFormat ExportFormat
        {
            get => _exportSettings.Format;
            set 
            { 
                if (_exportSettings.Format != value) 
                { 
                    _exportSettings.Format = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(IsCommaSeparatorEnabled));
                    UpdateTextOutputs(); 
                } 
            }
        }

        public bool IsCommaSeparatorEnabled => ExportFormat == ExportFormat.RawHex || ExportFormat == ExportFormat.RawBinary;

        public string SpriteName
        {
            get => _exportSettings.SpriteName;
            set
            {
                if (_exportSettings.SpriteName != value)
                {
                    _exportSettings.SpriteName = value;
                    OnPropertyChanged();
                    UpdateTextOutputs();
                }
            }
        }

        public bool IncludeUsageComment
        {
            get => _exportSettings.IncludeUsageComment;
            set { if (_exportSettings.IncludeUsageComment != value) { _exportSettings.IncludeUsageComment = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public bool IncludeDimensionConstants
        {
            get => _exportSettings.IncludeDimensionConstants;
            set { if (_exportSettings.IncludeDimensionConstants != value) { _exportSettings.IncludeDimensionConstants = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public bool UseCommaSeparator
        {
            get => _exportSettings.UseCommaSeparator;
            set { if (_exportSettings.UseCommaSeparator != value) { _exportSettings.UseCommaSeparator = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public int BytesPerLine
        {
            get => _exportSettings.BytesPerLine;
            set { if (_exportSettings.BytesPerLine != value) { _exportSettings.BytesPerLine = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public bool UppercaseHex
        {
            get => _exportSettings.UppercaseHex;
            set { if (_exportSettings.UppercaseHex != value) { _exportSettings.UppercaseHex = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public bool IncludeRowComments
        {
            get => _exportSettings.IncludeRowComments;
            set { if (_exportSettings.IncludeRowComments != value) { _exportSettings.IncludeRowComments = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        public bool IncludeArraySize
        {
            get => _exportSettings.IncludeArraySize;
            set { if (_exportSettings.IncludeArraySize != value) { _exportSettings.IncludeArraySize = value; OnPropertyChanged(); UpdateTextOutputs(); } }
        }

        // ── Export output ────────────────────────────────────────────────────
        private volatile bool _isUpdatingProgrammatically;

        private string _exportedCode = string.Empty;
        public string ExportedCode
        {
            get => _exportedCode;
            private set => SetProperty(ref _exportedCode, value);
        }

        private string _exportStats = string.Empty;
        public string ExportStats
        {
            get => _exportStats;
            private set => SetProperty(ref _exportStats, value);
        }

        /// <summary>
        /// Indicates that the canvas has changed since the last code generation.
        /// Bound to a stale indicator in the sidebar.
        /// </summary>
        private bool _isCodeStale;
        public bool IsCodeStale
        {
            get => _isCodeStale;
            private set => SetProperty(ref _isCodeStale, value);
        }

        // ── Import paste buffer ──────────────────────────────────────────────
        private string _importCode = string.Empty;
        public string ImportCode
        {
            get => _importCode;
            set => SetProperty(ref _importCode, value);
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
        /// <summary>
        /// Grid stroke thickness as a constant fraction of cell size (4%).
        /// No minimum floor — this keeps the grid proportionally identical
        /// at every resolution. At very high resolutions the grid naturally
        /// fades as cells become sub-pixel.
        /// </summary>
        public double DynamicStrokeThickness => SpriteState != null
            ? CellSize * 0.04
            : 1.0;

        /// <summary>
        /// Stroke thickness for selection overlays (marquee, lasso) that scales
        /// proportionally with the cell size so it remains visible at all resolutions.
        /// </summary>
        public double SelectionStrokeThickness => SpriteState != null
            ? Math.Max(0.5, CellSize * 0.08)
            : 2.0;
        public int PreviewWidth => (SpriteState?.Width ?? 16) * PreviewScale;
        public int PreviewHeight => (SpriteState?.Height ?? 16) * PreviewScale;

        private int _previewScale = 2;
        public int PreviewScale
        {
            get => _previewScale;
            set
            {
                if (SetProperty(ref _previewScale, Math.Max(1, value)))
                {
                    OnPropertyChanged(nameof(PreviewWidth));
                    OnPropertyChanged(nameof(PreviewHeight));
                    OnPropertyChanged(nameof(PreviewScaleText));
                }
            }
        }

        public string PreviewScaleText => $"({PreviewScale}× scale)";

        private DisplayType _previewDisplayType = DisplayType.Generic_White;
        public int PreviewDisplayTypeIndex
        {
            get => (int)_previewDisplayType;
            set
            {
                if (_previewDisplayType != (DisplayType)value)
                {
                    _previewDisplayType = (DisplayType)value;
                    OnPropertyChanged();
                    RefreshCanvasColors();
                }
            }
        }

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

        // ── IBitmapBufferContext implementation ────────────────────────────
        // Exposes bitmap buffer data through a narrow interface so
        // BitmapPreviewRenderer doesn't need to depend on the full ViewModel.
        uint[] IBitmapBufferContext.CanvasBuffer => _canvasBuffer;
        uint[] IBitmapBufferContext.PreviewBuffer => _previewBuffer;
        uint IBitmapBufferContext.ColorOnUint => _colorOnUint;
        uint IBitmapBufferContext.ColorOffUint => _colorOffUint;
        uint IBitmapBufferContext.PreviewOnUint => _previewOnUint;
        uint IBitmapBufferContext.PreviewOffUint => _previewOffUint;
        ISelectionService IBitmapBufferContext.SelectionService => _selectionService;

        // ── Extracted subsystems ──────────────────────────────────────────
        private ToolInputController _toolInput = null!;
        private BitmapPreviewRenderer _previewRenderer = null!;
        private SelectionInputController _selectionInput = null!;

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>
        /// Raised after Undo/Redo so the View can clear any in-progress selection
        /// overlays that may now be invalid.
        /// </summary>
        public event EventHandler? HistoryRestored;

        /// <summary>
        /// Raised after the tool changes so the View can update UI-only concerns
        /// (brush cursor visibility, cursor shape). Not a domain event.
        /// </summary>
        public event EventHandler? ToolChanged;

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand UndoCommand { get; }
        public IRelayCommand RedoCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand InvertCommand { get; }
        public IRelayCommand DeleteSelectionCommand { get; }
        public IRelayCommand CopyExportedCodeCommand { get; }
        public IRelayCommand GenerateCodeCommand { get; }
        public IRelayCommand ImportFromCodeCommand { get; }
        public IRelayCommand CopySelectionCommand { get; }
        public IRelayCommand CutSelectionCommand { get; }
        public IRelayCommand PasteCommand { get; }
        public IRelayCommand DeselectCommand { get; }
        public IRelayCommand<string> SelectToolCommand { get; }
        public IRelayCommand IncreasePreviewScaleCommand { get; }
        public IRelayCommand DecreasePreviewScaleCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────
        public MainViewModel(
            ICodeGeneratorService codeGen,
            IDrawingService drawingService,
            IHistoryService historyService,
            ISelectionService selectionService,
            IClipboardService clipboardService,
            IPixelClipboardService pixelClipboard,
            IDialogService dialogService)
        {
            _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
            _codeGen = codeGen ?? throw new ArgumentNullException(nameof(codeGen));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _pixelClipboard = pixelClipboard ?? throw new ArgumentNullException(nameof(pixelClipboard));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // ── Commands ──────────────────────────────────────────────────

            CopyExportedCodeCommand = new RelayCommand(() =>
            {
                _clipboardService.SetText(ExportedCode);
                ShowStatus("✓ Copied to clipboard");
            });

            GenerateCodeCommand = new RelayCommand(() =>
            {
                UpdateTextOutputs();
            });

            ImportFromCodeCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(ImportCode)) return;
                var backup = SpriteState.Clone();
                try
                {
                    if (ExportFormat == ExportFormat.RawBinary)
                        ParseBinaryToCanvas(ImportCode);
                    else if (ExportFormat == ExportFormat.U8g2DrawXBM)
                        _codeGen.ParseXbmToState(ImportCode, SpriteState);
                    else if (ExportFormat == ExportFormat.AdafruitGfx)
                        _codeGen.ParseAdafruitGfxToState(ImportCode, SpriteState);
                    else
                        _codeGen.ParseHexToState(ImportCode, SpriteState);

                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    ShowStatus("✓ Canvas updated from import");
                    ImportCode = string.Empty;
                }
                catch
                {
                    SpriteState = backup;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    ShowStatus("⚠ Could not parse the pasted code");
                }
            });

            UndoCommand = new RelayCommand(() => 
            {
                SpriteState.SelectionSnapshot = _selectionService.CreateSnapshot();
                RestoreState(_historyService.Undo(SpriteState));
            });
            RedoCommand = new RelayCommand(() => 
            {
                SpriteState.SelectionSnapshot = _selectionService.CreateSnapshot();
                RestoreState(_historyService.Redo(SpriteState));
            });

            ClearCommand = new RelayCommand(() =>
            {
                SaveStateForUndo();
                // Drop floating layer explicitly (user intent is clear canvas = clear everything)
                if (_selectionService.HasActiveSelection)
                    _selectionService.Cancel();
                Array.Clear(SpriteState.Pixels, 0, SpriteState.Pixels.Length);
                RedrawGridFromMemory();
                MarkCodeStale();
            });

            InvertCommand = new RelayCommand(() =>
            {
                SaveStateForUndo();
                _drawingService.InvertGrid(SpriteState);
                IsDisplayInverted = !IsDisplayInverted;
                RedrawGridFromMemory();
                MarkCodeStale();
            });

            CopySelectionCommand = new RelayCommand(() =>
            {
                if (!_selectionService.HasActiveSelection) return;
                var data = _selectionService.CopySelection(SpriteState);
                if (data != null)
                {
                    _pixelClipboard.Store(data);
                    ShowStatus("Selection copied");
                }
            });

            CutSelectionCommand = new RelayCommand(() =>
            {
                if (!_selectionService.HasActiveSelection) return;
                var data = _selectionService.CopySelection(SpriteState);
                if (data != null)
                {
                    _pixelClipboard.Store(data);
                    SaveStateForUndo();
                    _selectionService.DeleteSelection(SpriteState);
                    RedrawGridFromMemory();
                    MarkCodeStale();
                    ShowStatus("Selection cut");
                }
            });

            PasteCommand = new RelayCommand(() =>
            {
                if (!_pixelClipboard.HasData || _pixelClipboard.Data == null) return;

                SaveStateForUndo(); // Push state BEFORE flattening the current floating layer
                _selectionInput.CommitIfActive();

                _selectionService.PasteAsFloating(
                    _pixelClipboard.Data,
                    SpriteState.Width,
                    SpriteState.Height);
                RedrawGridFromMemory();
                ShowStatus("Pasted from clipboard");
            });

            DeleteSelectionCommand = new RelayCommand(() =>
            {
                if (!_selectionService.HasActiveSelection) return;

                // SaveStateForUndo only captures SpriteState.Pixels, not FloatingPixels.
                // This correctly matches Aseprite behavior: when undoing a deletion of a floating
                // selection, it discards the floating pixels and restores the hole from the original lift.
                SaveStateForUndo();

                _selectionService.DeleteSelection(SpriteState);
                RedrawGridFromMemory();
                MarkCodeStale();
            });

            DeselectCommand = new RelayCommand(() =>
            {
                _selectionInput.CommitIfActive();
            });

            SelectToolCommand = new RelayCommand<string>(ExecuteSelectTool);
            IncreasePreviewScaleCommand = new RelayCommand(() => PreviewScale++);
            DecreasePreviewScaleCommand = new RelayCommand(() => PreviewScale--);

            // ── Initialization ────────────────────────────────────────────
            InitializeBrushColors();
            _previewRenderer = new BitmapPreviewRenderer(this);
            _toolInput = new ToolInputController(this, _drawingService, _previewRenderer);
            _selectionInput = new SelectionInputController(this, _selectionService, _drawingService);
        }

        // ── Public methods called by the View ─────────────────────────────

        public void InitializeGrid(int width, int height)
        {
            SpriteState = new SpriteState(width, height);
            RebuildBitmaps(width, height);

            MarkCodeStale();
            RedrawGridFromMemory();
            NotifyCanvasLayoutChanged();
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

            // Commit any floating selection into the canvas before resizing.
            // The committed result is then included in the undo snapshot below.
            if (_selectionService.IsFloating)
                _selectionService.CommitSelection(oldState);
            _selectionService.Cancel();

            // Push current state for undo
            oldState.SelectionSnapshot = _selectionService.CreateSnapshot();
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
            RebuildBitmaps(newW, newH);

            RedrawGridFromMemory();
            MarkCodeStale();
            NotifyCanvasLayoutChanged();
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
                ResizeAnchor.TopLeft => (0, 0),
                ResizeAnchor.TopCenter => (dx / 2, 0),
                ResizeAnchor.TopRight => (dx, 0),
                ResizeAnchor.CenterLeft => (0, dy / 2),
                ResizeAnchor.Center => (dx / 2, dy / 2),
                ResizeAnchor.CenterRight => (dx, dy / 2),
                ResizeAnchor.BottomLeft => (0, dy),
                ResizeAnchor.BottomCenter => (dx / 2, dy),
                ResizeAnchor.BottomRight => (dx, dy),
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
        /// Partial redraw: only updates the pixel buffers and bitmaps within the
        /// specified bounding box. Much faster than a full <see cref="RedrawGridFromMemory"/>
        /// for small brush strokes on large canvases.
        /// </summary>
        public void RedrawRegion(int minX, int minY, int maxX, int maxY)
        {
            if (CanvasBitmap == null || _canvasBuffer == null || SpriteState?.Pixels == null) return;

            int w = SpriteState.Width;
            int h = SpriteState.Height;

            // Clamp to canvas bounds
            int x0 = Math.Clamp(minX, 0, w - 1);
            int y0 = Math.Clamp(minY, 0, h - 1);
            int x1 = Math.Clamp(maxX, 0, w - 1);
            int y1 = Math.Clamp(maxY, 0, h - 1);
            if (x0 > x1 || y0 > y1) return;

            bool hasFloating = _selectionService.IsFloating && _selectionService.FloatingPixels != null;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = (y * w) + x;
                    bool isPixelOn = SpriteState.Pixels[i];

                    if (hasFloating)
                    {
                        int fx = x - _selectionService.FloatingX;
                        int fy = y - _selectionService.FloatingY;
                        if (fx >= 0 && fx < _selectionService.FloatingWidth &&
                            fy >= 0 && fy < _selectionService.FloatingHeight &&
                            _selectionService.FloatingPixels![fx, fy])
                        {
                            isPixelOn = true;
                        }
                    }

                    _canvasBuffer[i] = isPixelOn ? _colorOnUint : _colorOffUint;
                    _previewBuffer[i] = isPixelOn ? _previewOnUint : _previewOffUint;
                }
            }

            int regionW = x1 - x0 + 1;
            int regionH = y1 - y0 + 1;
            // Use the 5-param overload: sourceRect is the region within the source buffer,
            // destX/Y is where it lands in the bitmap. This avoids the buffer-size
            // validation issue with the offset-based overload.
            var srcRect = new Int32Rect(x0, y0, regionW, regionH);
            CanvasBitmap.WritePixels(srcRect, _canvasBuffer, w * 4, x0, y0);
            PreviewBitmap.WritePixels(srcRect, _previewBuffer, w * 4, x0, y0);
        }

        /// <summary>
        /// Main entry point for all non-selection tool input from the View.
        /// Delegates to the extracted ToolInputController.
        /// </summary>
        public void ProcessToolInput(int x, int y, ToolAction action, DrawMode mode, bool isShiftDown, bool isAltDown = false)
            => _toolInput.ProcessToolInput(x, y, action, mode, isShiftDown, isAltDown);

        /// <summary>
        /// Main entry point for selection tool input from the View.
        /// Delegates to the extracted SelectionInputController.
        /// </summary>
        public void ProcessSelectionInput(int x, int y, ToolAction action, bool isShiftDown, bool isAltDown, bool isInverse = false)
            => _selectionInput.ProcessInput(x, y, action, isShiftDown, isAltDown, isInverse);

        /// <summary>
        /// Commits any active selection. Returns true if there was one to commit.
        /// </summary>
        public bool CommitCurrentSelection() => _selectionInput.CommitIfActive();

        /// <summary>
        /// Tries to begin a floating selection drag. Returns true if successful.
        /// </summary>
        public bool TryBeginSelectionDrag(int pixelX, int pixelY)
            => _selectionInput.TryBeginDrag(pixelX, pixelY);

        public void SaveStateForUndo()
        {
            SpriteState.SelectionSnapshot = _selectionService.CreateSnapshot();
            _historyService.SaveState(SpriteState);
            IsDirty = true;
        }

        public void ShiftGrid(int offsetX, int offsetY)
        {
            SaveStateForUndo();
            _drawingService.ShiftGrid(SpriteState, offsetX, offsetY);
            RedrawGridFromMemory();
            MarkCodeStale();
        }

        // ── Export output ─────────────────────────────────────────────────

        /// <summary>
        /// Marks the code output as stale after a canvas-modifying operation.
        /// Does NOT regenerate — the user must click "Generate Code" to update.
        /// </summary>
        public void MarkCodeStale() => IsCodeStale = true;

        public void UpdateTextOutputs() => _ = UpdateTextOutputsAsync();

        public async System.Threading.Tasks.Task UpdateTextOutputsAsync()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            // Snapshot settings to avoid reading them from a different thread
            var settingsSnapshot = new ExportSettings
            {
                Format = _exportSettings.Format,
                SpriteName = _exportSettings.SpriteName,
                IncludeUsageComment = _exportSettings.IncludeUsageComment,
                IncludeDimensionConstants = _exportSettings.IncludeDimensionConstants,
                UseCommaSeparator = _exportSettings.UseCommaSeparator,
                BytesPerLine = _exportSettings.BytesPerLine,
                UppercaseHex = _exportSettings.UppercaseHex,
                IncludeRowComments = _exportSettings.IncludeRowComments,
                IncludeArraySize = _exportSettings.IncludeArraySize,
            };

            try
            {
                string code = await _codeGen.GenerateCodeAsync(
                    SpriteState,
                    settingsSnapshot,
                    _selectionService.IsFloating,
                    _selectionService.FloatingPixels,
                    _selectionService.FloatingX,
                    _selectionService.FloatingY,
                    _selectionService.FloatingWidth,
                    _selectionService.FloatingHeight);

                // Compute stats on the background thread — avoid blocking UI with regex
                int byteCount;
                if (settingsSnapshot.Format == ExportFormat.RawBinary)
                    byteCount = SpriteState != null
                        ? SpriteState.Height * (int)Math.Ceiling(SpriteState.Width / 8.0)
                        : 0;
                else
                    byteCount = System.Text.RegularExpressions.Regex
                        .Matches(code, @"0[xX][0-9a-fA-F]{1,2}").Count;

                string stats = $"{byteCount} byte{(byteCount != 1 ? "s" : "")}  ·  {code.Length:N0} chars";

                _uiContext.Post(_ =>
                {
                    _exportedCode = code;
                    OnPropertyChanged(nameof(ExportedCode));

                    _exportStats = stats;
                    OnPropertyChanged(nameof(ExportStats));

                    IsCodeStale = false;
                    _isUpdatingProgrammatically = false;
                }, null);
            }
            catch
            {
                _isUpdatingProgrammatically = false;
            }
        }

        /// <summary>
        /// Applies export settings loaded from a .Hexprite file.
        /// Call after InitializeGrid so the VM proxy properties broadcast correctly.
        /// </summary>
        public void ApplyExportSettings(ExportSettings? saved)
        {
            if (saved == null) return;
            _exportSettings = saved;
            OnPropertyChanged(nameof(ExportFormat));
            OnPropertyChanged(nameof(SpriteName));
            OnPropertyChanged(nameof(IncludeUsageComment));
            OnPropertyChanged(nameof(IncludeDimensionConstants));
            OnPropertyChanged(nameof(UseCommaSeparator));
            OnPropertyChanged(nameof(BytesPerLine));
            OnPropertyChanged(nameof(UppercaseHex));
            OnPropertyChanged(nameof(IncludeRowComments));
            OnPropertyChanged(nameof(IncludeArraySize));
            UpdateTextOutputs();
        }

        /// <summary>
        /// Sets the sprite name from the filename (without extension).
        /// Called after Save So As to update the default name.
        /// </summary>
        public void UpdateSpriteNameFromFile()
        {
            if (FilePath == null) return;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(FilePath);
            string sanitised = Services.CodeGeneratorService.SanitiseName(baseName);
            if (SpriteName == "mySprite" || SpriteName == "sprite")
                SpriteName = sanitised;
        }

        // ── Private helpers ────────────────────────────────────────────────

        private void ParseBinaryToCanvas(string text)
        {
            Array.Clear(SpriteState.Pixels, 0, SpriteState.Pixels.Length);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;
            foreach (string line in lines)
            {
                if (row >= SpriteState.Height) break;
                string data = line.Contains(':') ? line[(line.IndexOf(':') + 1)..] : line;
                data = data.Replace(" ", "").Replace(",", "");
                for (int col = 0; col < SpriteState.Width; col++)
                    SpriteState.Pixels[(row * SpriteState.Width) + col] = col < data.Length && data[col] == '1';
                row++;
            }
        }

        /// <summary>
        /// Cancels any in-progress shape drawing and resets tracking state.
        /// Called by the View when switching tools to prevent stale draw flags.
        /// </summary>
        public void CancelInProgressDrawing() => _toolInput.CancelInProgressDrawing();

        // ── Private: tool selection ────────────────────────────────────────

        private void ExecuteSelectTool(string? toolName)
        {
            if (toolName == null) return;

            // Commit floating pixels before switching tool.
            // Non-floating selections are preserved — they become the draw mask.
            if (_selectionService.IsFloating)
            {
                _selectionInput.CommitIfActive();
            }

            CurrentTool = toolName switch
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

            _toolInput.CancelInProgressDrawing();

            ToolChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Private: history restore ──────────────────────────────────────

        private void RestoreState(SpriteState state)
        {
            if (state == null || ReferenceEquals(state, SpriteState)) return;

            if (SpriteState.Width != state.Width || SpriteState.Height != state.Height)
            {
                InitializeGrid(state.Width, state.Height);
            }

            SpriteState = state;
            IsDisplayInverted = state.IsDisplayInverted; // restores the visual invert flag

            if (state.SelectionSnapshot != null)
                _selectionService.RestoreSnapshot(state.SelectionSnapshot);
            else
                _selectionService.Cancel();
            RedrawGridFromMemory();
            MarkCodeStale();
            HistoryRestored?.Invoke(this, EventArgs.Empty);
        }

        // ── Private: color initialization ─────────────────────────────────

        /// <summary>
        /// Reads the canvas pixel colors from the current theme resources.
        /// Called once at construction and again whenever the theme changes.
        /// </summary>
        public void InitializeBrushColors()
        {
            var res = Application.Current.Resources;
            var colorOff = ((SolidColorBrush)res["Theme.CanvasPixelBackgroundBrush"]).Color;
            var colorOn = ((SolidColorBrush)res["Theme.CanvasDrawingBrush"]).Color;
            var prevOff = ((SolidColorBrush)res["Theme.PreviewBackgroundBrush"]).Color;
            var prevOn = ((SolidColorBrush)res["Theme.PreviewDrawingBrush"]).Color;

            _colorOffUint = ToBgra32(colorOff);
            _colorOnUint = ToBgra32(colorOn);
            _previewOffUint = ToBgra32(prevOff);
            _previewOnUint = ToBgra32(prevOn);
        }

        /// <summary>
        /// Re-reads theme colors and redraws the canvas. Call after a theme switch.
        /// </summary>
        public void RefreshCanvasColors()
        {
            UpdatePreviewColors();
            InitializeBrushColors();
            RedrawGridFromMemory();
        }

        private void UpdatePreviewColors()
        {
            Color bg, fg;
            switch (_previewDisplayType)
            {
                case DisplayType.SSD1306_Blue:
                    bg = (Color)ColorConverter.ConvertFromString("#000A1A");
                    fg = (Color)ColorConverter.ConvertFromString("#82C9FF");
                    break;
                case DisplayType.SSD1306_Green:
                    bg = (Color)ColorConverter.ConvertFromString("#051105");
                    fg = (Color)ColorConverter.ConvertFromString("#72FF72");
                    break;
                case DisplayType.ePaper:
                    bg = (Color)ColorConverter.ConvertFromString("#D3D3D3");
                    fg = (Color)ColorConverter.ConvertFromString("#1A1A1A");
                    break;
                case DisplayType.Generic_White:
                default:
                    Application.Current.Resources.Remove("Theme.PreviewBackgroundBrush");
                    Application.Current.Resources.Remove("Theme.PreviewDrawingBrush");
                    return;
            }

            Application.Current.Resources["Theme.PreviewBackgroundBrush"] = new SolidColorBrush(bg);
            Application.Current.Resources["Theme.PreviewDrawingBrush"] = new SolidColorBrush(fg);
        }

        // ── Private: bitmap lifecycle helpers ─────────────────────────────

        /// <summary>
        /// Allocates fresh WritableBitmaps and pixel buffers for the given dimensions.
        /// Called by both <see cref="InitializeGrid"/> and <see cref="ResizeCanvas"/>
        /// to avoid duplicating this initialization logic.
        /// </summary>
        private void RebuildBitmaps(int width, int height)
        {
            CanvasBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            PreviewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _canvasBuffer = new uint[width * height];
            _previewBuffer = new uint[width * height];

            PreviewScale = 2;
        }

        /// <summary>
        /// Raises PropertyChanged for all layout-dependent computed properties.
        /// Called after canvas dimensions change to keep bindings in sync.
        /// </summary>
        private void NotifyCanvasLayoutChanged()
        {
            OnPropertyChanged(nameof(GridViewport));
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(CanvasDisplayWidth));
            OnPropertyChanged(nameof(CanvasDisplayHeight));
            OnPropertyChanged(nameof(DynamicStrokeThickness));
            OnPropertyChanged(nameof(SelectionStrokeThickness));
            OnPropertyChanged(nameof(CanvasDimensionText));
        }
    }
}
