using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexprite.Controllers;
using Hexprite.Core;
using Hexprite.Rendering;
using Hexprite.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
        ePaper,
        FlipperZero
    }

    public enum DisplaySimulationPreset
    {
        Flat = 0,
        GenericLcd = 1,
        Ssd1306OledBlue = 2,
        Ssd1306OledGreen = 3,
        EPaper = 4,
    }

    public enum PreviewQuality
    {
        Fast = 0,
        Balanced = 1,
        High = 2
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
            private set
            {
                if (SetProperty(ref _spriteState, value))
                {
                    _spriteState.EnsureLayers();
                    RebuildLayerViewModels();
                }
            }
        }

        public ObservableCollection<LayerItemViewModel> Layers { get; } = new();

        private int _selectedLayerIndex;
        /// <summary>
        /// While rebuilding <see cref="Layers"/>, the ListBox two-way binds <see cref="SelectedLayerIndex"/>
        /// and may push index 0 when the collection is cleared — that would incorrectly call
        /// <see cref="SetActiveLayer"/> and reset the sprite's active layer. Ignore inbound binding
        /// updates during rebuild.
        /// </summary>
        private bool _suppressSelectedLayerBindingToState;

        public int SelectedLayerIndex
        {
            get => _selectedLayerIndex;
            set
            {
                if (_suppressSelectedLayerBindingToState)
                    return;
                if (SetProperty(ref _selectedLayerIndex, value))
                    SetActiveLayer(value, shouldRedraw: true);
            }
        }

        public bool IsActiveLayerLocked =>
            SpriteState.Layers.Count > 0 &&
            SpriteState.ActiveLayerIndex >= 0 &&
            SpriteState.ActiveLayerIndex < SpriteState.Layers.Count &&
            SpriteState.Layers[SpriteState.ActiveLayerIndex].IsLocked;

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
            set
            {
                if (SetProperty(ref _showGridLines, value))
                    SaveEditorPreferences();
            }
        }

        // ── Tool state ────────────────────────────────────────────────────
        private ToolMode _currentTool = ToolMode.Pencil;
        public ToolMode CurrentTool
        {
            get => _currentTool;
            set
            {
                if (SetProperty(ref _currentTool, value))
                {
                    OnPropertyChanged(nameof(IsPixelPerfectAvailable));
                    SaveEditorPreferences();
                }
            }
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
            set
            {
                if (SetProperty(ref _brushSize, Math.Clamp(value, 1, 64)))
                {
                    OnPropertyChanged(nameof(IsPixelPerfectAvailable));
                    SaveEditorPreferences();
                }
            }
        }

        private bool _isPixelPerfectEnabled;
        public bool IsPixelPerfectEnabled
        {
            get => _isPixelPerfectEnabled;
            set
            {
                if (SetProperty(ref _isPixelPerfectEnabled, value))
                    SaveEditorPreferences();
            }
        }

        public bool IsPixelPerfectAvailable => CurrentTool == ToolMode.Pencil && BrushSize == 1;

        private BrushShape _brushShape = BrushShape.Circle;
        public BrushShape BrushShape
        {
            get => _brushShape;
            set
            {
                if (SetProperty(ref _brushShape, value))
                    SaveEditorPreferences();
            }
        }

        private int _brushAngle = 0;
        public int BrushAngle
        {
            get => _brushAngle;
            set
            {
                if (SetProperty(ref _brushAngle, ((value % 360) + 360) % 360))
                    SaveEditorPreferences();
            }
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

        /// <summary>
        /// Sets the sprite name without triggering export code generation.
        /// Used by import paths to avoid UI stalls for large canvases.
        /// </summary>
        public void SetSpriteNameWithoutGenerating(string name)
        {
            if (_exportSettings.SpriteName != name)
            {
                _exportSettings.SpriteName = name;
                OnPropertyChanged(nameof(SpriteName));
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

        // Keep the sidebar preview usable even for large canvases.
        // Without this, `PreviewWidth/PreviewHeight` become enormous and WPF layout
        // can get very slow / overflow horizontally.
        private const int MaxPreviewDimensionPx = 280;

        private double PreviewScaleEffective
        {
            get
            {
                if (SpriteState == null) return PreviewScale;

                double requestedW = SpriteState.Width * (double)PreviewScale;
                double requestedH = SpriteState.Height * (double)PreviewScale;
                double maxRequested = Math.Max(requestedW, requestedH);

                if (maxRequested <= MaxPreviewDimensionPx) return PreviewScale;

                // Reduce effective scale to fit the sidebar preview box.
                double fitRatio = MaxPreviewDimensionPx / maxRequested;
                return PreviewScale * fitRatio;
            }
        }

        public double PreviewEffectiveScale => PreviewScaleEffective;
        public bool IsPreviewScaleCapped => PreviewScaleEffective < (PreviewScale - 0.01);
        public int MaxUsefulPreviewScale
        {
            get
            {
                if (SpriteState == null) return int.MaxValue;
                int maxDim = Math.Max(SpriteState.Width, SpriteState.Height);
                if (maxDim <= 0) return 1;
                return Math.Max(1, (int)Math.Floor(MaxPreviewDimensionPx / (double)maxDim));
            }
        }
        public bool CanIncreasePreviewScale => PreviewScale < MaxUsefulPreviewScale;
        public bool CanDecreasePreviewScale => PreviewScale > 1;
        public string PreviewScaleStatusText => IsPreviewScaleCapped
            ? $"Requested {PreviewScale}x, capped to {PreviewScaleEffective:F2}x (preview limit)."
            : !CanIncreasePreviewScale
                ? $"Max useful scale reached ({MaxUsefulPreviewScale}x) for this preview size."
            : "Requested scale is fully applied.";

        public int PreviewWidth =>
            Math.Max(1, (int)Math.Round((SpriteState?.Width ?? 16) * PreviewScaleEffective));

        public int PreviewHeight =>
            Math.Max(1, (int)Math.Round((SpriteState?.Height ?? 16) * PreviewScaleEffective));

        private int _previewScale = 2;
        public int PreviewScale
        {
            get => _previewScale;
            set
            {
                int clamped = Math.Clamp(value, 1, Math.Max(1, MaxUsefulPreviewScale));
                if (SetProperty(ref _previewScale, clamped))
                {
                    OnPropertyChanged(nameof(PreviewWidth));
                    OnPropertyChanged(nameof(PreviewHeight));
                    OnPropertyChanged(nameof(PreviewScaleText));
                    OnPropertyChanged(nameof(PreviewEffectiveScale));
                    OnPropertyChanged(nameof(IsPreviewScaleCapped));
                    OnPropertyChanged(nameof(MaxUsefulPreviewScale));
                    OnPropertyChanged(nameof(CanIncreasePreviewScale));
                    OnPropertyChanged(nameof(CanDecreasePreviewScale));
                    OnPropertyChanged(nameof(PreviewScaleStatusText));
                    EnsurePreviewSimBitmap();
                    UpdatePreviewSimulation();
                    SaveEditorPreferences();
                }
            }
        }

        public string PreviewScaleText =>
            $"({PreviewScale}× scale, fit {PreviewScaleEffective:F2}×)";

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
                    UpdatePreviewSimulation();
                    SaveEditorPreferences();
                }
            }
        }

        private bool _useRealisticPreview;
        public bool UseRealisticPreview
        {
            get => _useRealisticPreview;
            set
            {
                if (SetProperty(ref _useRealisticPreview, value))
                {
                    OnPropertyChanged(nameof(DisplayPreviewBitmap));
                    UpdatePreviewSimulation();
                    SaveEditorPreferences();
                }
            }
        }

        private int _previewRealismStrength = 65;
        public int PreviewRealismStrength
        {
            get => _previewRealismStrength;
            set
            {
                if (SetProperty(ref _previewRealismStrength, Math.Clamp(value, 0, 100)))
                {
                    UpdatePreviewSimulation();
                    SaveEditorPreferences();
                }
            }
        }

        private PreviewQuality _previewQuality = PreviewQuality.Balanced;
        public int PreviewQualityIndex
        {
            get => (int)_previewQuality;
            set
            {
                var v = (PreviewQuality)Math.Clamp(value, 0, 2);
                if (_previewQuality != v)
                {
                    _previewQuality = v;
                    OnPropertyChanged();
                    UpdatePreviewSimulation();
                    SaveEditorPreferences();
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

        private WriteableBitmap? _previewSimBitmap;
        public WriteableBitmap PreviewSimBitmap
        {
            get
            {
                _previewSimBitmap ??= new WriteableBitmap(
                    Math.Max(1, PreviewWidth),
                    Math.Max(1, PreviewHeight),
                    96, 96,
                    PixelFormats.Bgra32,
                    null);
                return _previewSimBitmap;
            }
            private set => SetProperty(ref _previewSimBitmap, value);
        }

        public ImageSource DisplayPreviewBitmap => UseRealisticPreview ? PreviewSimBitmap : PreviewBitmap;

        private uint[] _canvasBuffer = Array.Empty<uint>();
        private uint[] _previewBuffer = Array.Empty<uint>();
        private uint[] _previewSimBuffer = Array.Empty<uint>();
        private long _lastPreviewSimulationTicks;
        private const long PreviewSimulationMinIntervalTicks = TimeSpan.TicksPerMillisecond * 33;
        private bool _isStrokeRenderingActive;
        private bool _pendingPreviewSimulationAfterStroke;

#if DEBUG
        private readonly DrawPerfCollector _drawPerf = new();
#endif

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
        void IBitmapBufferContext.UpdatePreviewSimulation() => UpdatePreviewSimulation();

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
        public IRelayCommand SelectAllCommand { get; }
        public IRelayCommand<string> SelectToolCommand { get; }
        public IRelayCommand IncreasePreviewScaleCommand { get; }
        public IRelayCommand DecreasePreviewScaleCommand { get; }
        public IRelayCommand AddLayerCommand { get; }
        public IRelayCommand DeleteLayerCommand { get; }
        public IRelayCommand DuplicateLayerCommand { get; }
        public IRelayCommand MoveLayerUpCommand { get; }
        public IRelayCommand MoveLayerDownCommand { get; }
        public IRelayCommand RotateCanvasCWCommand { get; }
        public IRelayCommand RotateCanvasCCWCommand { get; }
        public IRelayCommand RotateCanvas180Command { get; }
        public IRelayCommand FlipCanvasHorizontalCommand { get; }
        public IRelayCommand FlipCanvasVerticalCommand { get; }
        public IRelayCommand FlipSelectionHorizontalCommand { get; }
        public IRelayCommand FlipSelectionVerticalCommand { get; }
        public IRelayCommand BeginSelectionTransformCommand { get; }

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
            ApplySavedEditorPreferences();

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
                        _codeGen.ParseBinaryToState(ImportCode, SpriteState);
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
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
                {
                    SpriteState = backup;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    ShowStatus("⚠ Could not parse the pasted code");
                    HandledErrorReporter.Warning(ex, "MainViewModel.ImportFromCodePanel", new { ExportFormat });
                }
                catch (Exception ex)
                {
                    SpriteState = backup;
                    RedrawGridFromMemory();
                    UpdateTextOutputs();
                    ShowStatus("⚠ Could not parse the pasted code");
                    HandledErrorReporter.Error(ex, "MainViewModel.ImportFromCodePanel", new { ExportFormat });
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
                _selectionInput.CommitIfActive(saveHistory: false);

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
                _selectionService.Cancel();
            });

            SelectAllCommand = new RelayCommand(() =>
            {
                // Preserve any "lifted" (floating) pixels by committing before replacing
                // the selection with the whole-canvas marquee.
                _selectionInput.CommitIfActive();
                _selectionService.Cancel();

                int w = SpriteState.Width;
                int h = SpriteState.Height;

                _selectionService.BeginRectangleSelection(0, 0, SelectionMode.Replace);
                _selectionService.UpdateRectangleSelection(w - 1, h - 1);
                _selectionService.FinalizeSelection();
            });

            SelectToolCommand = new RelayCommand<string>(ExecuteSelectTool);
            IncreasePreviewScaleCommand = new RelayCommand(() =>
            {
                if (CanIncreasePreviewScale) PreviewScale++;
            });
            DecreasePreviewScaleCommand = new RelayCommand(() =>
            {
                if (CanDecreasePreviewScale) PreviewScale--;
            });
            AddLayerCommand = new RelayCommand(AddLayer);
            DeleteLayerCommand = new RelayCommand(DeleteActiveLayer);
            DuplicateLayerCommand = new RelayCommand(DuplicateActiveLayer);
            MoveLayerUpCommand = new RelayCommand(MoveActiveLayerUp);
            MoveLayerDownCommand = new RelayCommand(MoveActiveLayerDown);

            RotateCanvasCWCommand = new RelayCommand(() => RotateCanvas(RotationDirection.Clockwise90));
            RotateCanvasCCWCommand = new RelayCommand(() => RotateCanvas(RotationDirection.CounterClockwise90));
            RotateCanvas180Command = new RelayCommand(() => RotateCanvas(RotationDirection.OneEighty));

            FlipCanvasHorizontalCommand = new RelayCommand(() => FlipCanvas(FlipDirection.Horizontal));
            FlipCanvasVerticalCommand = new RelayCommand(() => FlipCanvas(FlipDirection.Vertical));

            FlipSelectionHorizontalCommand = new RelayCommand(
                () => FlipSelection(FlipDirection.Horizontal),
                () => _selectionService.HasActiveSelection && _selectionService.IsFloating && !_selectionService.IsTransforming);
            FlipSelectionVerticalCommand = new RelayCommand(
                () => FlipSelection(FlipDirection.Vertical),
                () => _selectionService.HasActiveSelection && _selectionService.IsFloating && !_selectionService.IsTransforming);

            BeginSelectionTransformCommand = new RelayCommand(
                () => _selectionInput.EnterTransformMode(),
                () => _selectionService.HasActiveSelection && !_selectionService.IsTransforming);

            // Keep menu enabled/disabled state in sync with selection/floating/transform status.
            // Otherwise WPF can keep stale CanExecute values and the command won't execute.
            _selectionService.SelectionChanged += (_, _) =>
            {
                if (FlipSelectionHorizontalCommand is RelayCommand rh)
                    rh.NotifyCanExecuteChanged();
                if (FlipSelectionVerticalCommand is RelayCommand rv)
                    rv.NotifyCanExecuteChanged();
                if (BeginSelectionTransformCommand is RelayCommand rt)
                    rt.NotifyCanExecuteChanged();
            };

            // ── Initialization ────────────────────────────────────────────
            InitializeBrushColors();
            _previewRenderer = new BitmapPreviewRenderer(this);
            _toolInput = new ToolInputController(this, _drawingService, _previewRenderer);
            _selectionInput = new SelectionInputController(this, _selectionService, _drawingService);
        }

        // ── Public methods called by the View ─────────────────────────────

        public void InitializeGrid(int width, int height)
            => InitializeGrid(width, height, redrawImmediately: true);

        public void InitializeGrid(int width, int height, bool redrawImmediately)
        {
            SpriteState = new SpriteState(width, height);
            SpriteState.EnsureLayers();
            RebuildBitmaps(width, height);

            AutoSelectPreviewDisplayTypeForCanvasSize(width, height);

            MarkCodeStale();
            if (redrawImmediately)
                RedrawGridFromMemory();

            NotifyCanvasLayoutChanged();
        }

        private void AutoSelectPreviewDisplayTypeForCanvasSize(int width, int height)
        {
            // Only auto-select when the user hasn't explicitly chosen a preview style.
            // This keeps the app feeling "smart" for common presets (SSD1306 / ePaper)
            // while still respecting the user's preference if they changed it.
            if (_previewDisplayType != DisplayType.Generic_White)
                return;

            var inferred = InferPreviewDisplayType(width, height);
            if (inferred == _previewDisplayType)
                return;

            _previewDisplayType = inferred;
            OnPropertyChanged(nameof(PreviewDisplayTypeIndex));

            // Update theme-backed brushes + cached pixel colors (no redraw here; caller will redraw if needed).
            UpdatePreviewColors();
            InitializeBrushColors();
        }

        private static DisplayType InferPreviewDisplayType(int width, int height)
        {
            // SSD1306 family
            if (width == 128 && (height == 64 || height == 32))
                return DisplayType.SSD1306_Blue;

            // Common monochrome e-paper preset
            if (width == 296 && height == 128)
                return DisplayType.ePaper;

            return DisplayType.Generic_White;
        }

        public void SetActiveLayer(int index, bool shouldRedraw)
        {
            if (SpriteState == null) return;
            SpriteState.SetActiveLayer(index);
            _selectedLayerIndex = SpriteState.ActiveLayerIndex;
            OnPropertyChanged(nameof(SelectedLayerIndex));
            SyncLayerActiveFlags();
            if (shouldRedraw)
                RedrawGridFromMemory();
        }

        public void ReloadLayersFromState()
        {
            SpriteState.EnsureLayers();
            RebuildLayerViewModels();
        }

        public void MoveLayer(int fromIndex, int toIndex)
        {
            SpriteState.EnsureLayers();
            if (fromIndex == toIndex) return;
            if (fromIndex < 0 || fromIndex >= SpriteState.Layers.Count) return;
            if (toIndex < 0 || toIndex >= SpriteState.Layers.Count) return;

            ApplyLayerMutation(() =>
            {
                var moved = SpriteState.Layers[fromIndex];
                SpriteState.Layers.RemoveAt(fromIndex);
                SpriteState.Layers.Insert(toIndex, moved);

                if (SpriteState.ActiveLayerIndex == fromIndex)
                    SpriteState.ActiveLayerIndex = toIndex;
                else if (SpriteState.ActiveLayerIndex > fromIndex && SpriteState.ActiveLayerIndex <= toIndex)
                    SpriteState.ActiveLayerIndex--;
                else if (SpriteState.ActiveLayerIndex < fromIndex && SpriteState.ActiveLayerIndex >= toIndex)
                    SpriteState.ActiveLayerIndex++;

                SpriteState.SetActiveLayer(SpriteState.ActiveLayerIndex);
            });
        }

        public void BeginLayerRename(int index)
        {
            SpriteState.EnsureLayers();
            if (index < 0 || index >= Layers.Count) return;
            for (int i = 0; i < Layers.Count; i++)
                Layers[i].IsRenaming = i == index;
        }

        public void UpdateLayerName(int index, string? name)
        {
            SpriteState.EnsureLayers();
            if (index < 0 || index >= SpriteState.Layers.Count) return;
            string trimmed = string.IsNullOrWhiteSpace(name) ? $"Layer {index + 1}" : name.Trim();
            if (SpriteState.Layers[index].Name == trimmed) return;

            SaveStateForUndo();
            SpriteState.Layers[index].Name = trimmed;
            Layers[index].Name = trimmed;
            IsDirty = true;
        }

        public void SetLayerVisibility(int index, bool isVisible)
        {
            SpriteState.EnsureLayers();
            if (index < 0 || index >= SpriteState.Layers.Count) return;
            if (SpriteState.Layers[index].IsVisible == isVisible) return;
            if (!isVisible)
            {
                int visibleCount = 0;
                foreach (var layer in SpriteState.Layers)
                    if (layer.IsVisible) visibleCount++;
                if (visibleCount <= 1)
                {
                    _dialogService.ShowMessage("At least one layer must remain visible.");
                    // IsChecked TwoWay-binding may have flipped the row VM already; SpriteState stays visible.
                    if (index < Layers.Count)
                        Layers[index].IsVisible = SpriteState.Layers[index].IsVisible;
                    return;
                }
            }

            ApplyLayerMutation(() =>
            {
                SpriteState.Layers[index].IsVisible = isVisible;
                Layers[index].IsVisible = isVisible;
            }, rebuildLayerList: false);
        }

        public void SetLayerLocked(int index, bool isLocked)
        {
            SpriteState.EnsureLayers();
            if (index < 0 || index >= SpriteState.Layers.Count) return;
            if (SpriteState.Layers[index].IsLocked == isLocked) return;

            SaveStateForUndo();
            SpriteState.Layers[index].IsLocked = isLocked;
            Layers[index].IsLocked = isLocked;
            OnPropertyChanged(nameof(IsActiveLayerLocked));
        }

        /// <summary>
        /// Resizes the canvas to <paramref name="newW"/>×<paramref name="newH"/>,
        /// preserving existing pixel data positioned according to the specified
        /// <paramref name="anchor"/>. Pixels falling outside the new bounds are cropped.
        /// </summary>
        public void ResizeCanvas(int newW, int newH, ResizeAnchor anchor)
        {
            var oldState = SpriteState;
            oldState.EnsureLayers();
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
                IsDisplayInverted = oldState.IsDisplayInverted,
                ExportSettings = oldState.ExportSettings
            };
            newState.Layers.Clear();
            foreach (var oldLayer in oldState.Layers)
            {
                var resizedLayer = new LayerState
                {
                    Name = oldLayer.Name,
                    IsVisible = oldLayer.IsVisible,
                    IsLocked = oldLayer.IsLocked,
                    Pixels = new bool[newW * newH]
                };

                for (int y = 0; y < oldH; y++)
                {
                    int destY = y + offsetY;
                    if (destY < 0 || destY >= newH) continue;

                    for (int x = 0; x < oldW; x++)
                    {
                        int destX = x + offsetX;
                        if (destX < 0 || destX >= newW) continue;

                        resizedLayer.Pixels[(destY * newW) + destX] = oldLayer.Pixels[(y * oldW) + x];
                    }
                }
                newState.Layers.Add(resizedLayer);
            }
            newState.ActiveLayerIndex = oldState.ActiveLayerIndex;
            newState.EnsureLayers();

            // Re-initialize bitmaps for the new size and apply the new state
            SpriteState = newState;
            RebuildBitmaps(newW, newH);

            RedrawGridFromMemory();
            MarkCodeStale();
            NotifyCanvasLayoutChanged();
        }

        /// <summary>
        /// Rotates the entire canvas (all layers) by 90° CW/CCW or 180°. Width and height swap for 90° rotations.
        /// </summary>
        public void RotateCanvas(RotationDirection dir)
        {
            var oldState = SpriteState;
            oldState.EnsureLayers();
            int oldW = oldState.Width;
            int oldH = oldState.Height;

            if (_selectionService.IsFloating)
                _selectionService.CommitSelection(oldState);
            _selectionService.Cancel();

            oldState.SelectionSnapshot = _selectionService.CreateSnapshot();
            _historyService.SaveState(oldState);
            IsDirty = true;

            bool swapDims = dir is RotationDirection.Clockwise90 or RotationDirection.CounterClockwise90;
            int newW = swapDims ? oldH : oldW;
            int newH = swapDims ? oldW : oldH;

            var newState = new SpriteState(newW, newH)
            {
                IsDisplayInverted = oldState.IsDisplayInverted,
                ExportSettings = oldState.ExportSettings
            };
            newState.Layers.Clear();

            foreach (var oldLayer in oldState.Layers)
            {
                var rotatedPixels = _drawingService.RotatePixels(oldLayer.Pixels, oldW, oldH, dir);
                newState.Layers.Add(new LayerState
                {
                    Name = oldLayer.Name,
                    IsVisible = oldLayer.IsVisible,
                    IsLocked = oldLayer.IsLocked,
                    Pixels = rotatedPixels
                });
            }

            newState.ActiveLayerIndex = oldState.ActiveLayerIndex;
            newState.EnsureLayers();

            SpriteState = newState;
            RebuildBitmaps(newW, newH);

            RedrawGridFromMemory();
            MarkCodeStale();
            NotifyCanvasLayoutChanged();
        }

        public void FlipCanvas(FlipDirection dir)
        {
            var oldState = SpriteState;
            oldState.EnsureLayers();

            // Match Resize/Rotate UX: if there is a floating selection, commit it
            // to the canvas before flipping everything.
            if (_selectionService.IsFloating)
                _selectionService.CommitSelection(oldState);
            _selectionService.Cancel();

            // Push current state for undo (selection is now cleared/committed).
            oldState.SelectionSnapshot = _selectionService.CreateSnapshot();
            _historyService.SaveState(oldState);
            IsDirty = true;

            int w = oldState.Width;
            int h = oldState.Height;

            var newState = new SpriteState(w, h)
            {
                IsDisplayInverted = oldState.IsDisplayInverted,
                ExportSettings = oldState.ExportSettings
            };
            newState.Layers.Clear();

            foreach (var oldLayer in oldState.Layers)
            {
                var flippedPixels = _drawingService.FlipPixels(oldLayer.Pixels, w, h, dir);
                newState.Layers.Add(new LayerState
                {
                    Name = oldLayer.Name,
                    IsVisible = oldLayer.IsVisible,
                    IsLocked = oldLayer.IsLocked,
                    Pixels = flippedPixels
                });
            }

            newState.ActiveLayerIndex = oldState.ActiveLayerIndex;
            newState.EnsureLayers();

            SpriteState = newState;
            RebuildBitmaps(w, h);

            RedrawGridFromMemory();
            MarkCodeStale();
            NotifyCanvasLayoutChanged();
        }

        public void FlipSelection(FlipDirection dir)
        {
            if (!_selectionService.HasActiveSelection || !_selectionService.IsFloating)
                return;
            if (_selectionService.FloatingPixels == null)
                return;

            // Ensure undo captures the current floating pixels.
            SaveStateForUndo();

            if (dir == FlipDirection.Horizontal)
                _selectionService.FlipFloatingHorizontally();
            else if (dir == FlipDirection.Vertical)
                _selectionService.FlipFloatingVertically();

            RedrawGridFromMemory();
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
            using var perfScope = BeginDrawPerfScope("RedrawGridFromMemory");

            int w = SpriteState.Width;
            int h = SpriteState.Height;
            var visibleLayerPixels = GetVisibleLayerPixels();
            int visibleLayerCount = visibleLayerPixels.Count;
            bool[]? singleLayerPixels = visibleLayerCount == 1 ? visibleLayerPixels[0] : null;
            bool hasFloating = _selectionService.IsFloating && _selectionService.FloatingPixels != null;
            int floatingX = _selectionService.FloatingX;
            int floatingY = _selectionService.FloatingY;
            int floatingW = _selectionService.FloatingWidth;
            int floatingH = _selectionService.FloatingHeight;
            bool[,]? floatingPixels = _selectionService.FloatingPixels;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    bool isPixelOn = ComposePixelState(i, visibleLayerPixels, visibleLayerCount, singleLayerPixels);

                    // Overlay the floating selection layer if one is active
                    if (hasFloating && floatingPixels != null)
                    {
                        int fx = x - floatingX;
                        int fy = y - floatingY;
                        if (fx >= 0 && fx < floatingW &&
                            fy >= 0 && fy < floatingH &&
                            floatingPixels[fx, fy])
                        {
                            isPixelOn = true;
                        }
                    }

                    _canvasBuffer[i] = isPixelOn ? _colorOnUint : _colorOffUint;
                    _previewBuffer[i] = isPixelOn ? _previewOnUint : _previewOffUint;
                }
            }

            var rect = new Int32Rect(0, 0, w, h);
            using (BeginDrawPerfScope("WritePixels.Full.Canvas"))
            {
                CanvasBitmap.WritePixels(rect, _canvasBuffer, w * 4, 0);
            }
            using (BeginDrawPerfScope("WritePixels.Full.Preview"))
            {
                PreviewBitmap.WritePixels(rect, _previewBuffer, w * 4, 0);
            }
            UpdatePreviewSimulation();
        }

        /// <summary>
        /// Partial redraw: only updates the pixel buffers and bitmaps within the
        /// specified bounding box. Much faster than a full <see cref="RedrawGridFromMemory"/>
        /// for small brush strokes on large canvases.
        /// </summary>
        public void RedrawRegion(int minX, int minY, int maxX, int maxY, bool updatePreviewSimulation = true)
        {
            if (CanvasBitmap == null || _canvasBuffer == null || SpriteState?.Pixels == null) return;
            using var perfScope = BeginDrawPerfScope("RedrawRegion");

            int w = SpriteState.Width;
            int h = SpriteState.Height;

            // Clamp to canvas bounds
            int x0 = Math.Clamp(minX, 0, w - 1);
            int y0 = Math.Clamp(minY, 0, h - 1);
            int x1 = Math.Clamp(maxX, 0, w - 1);
            int y1 = Math.Clamp(maxY, 0, h - 1);
            if (x0 > x1 || y0 > y1) return;

            var visibleLayerPixels = GetVisibleLayerPixels();
            int visibleLayerCount = visibleLayerPixels.Count;
            bool[]? singleLayerPixels = visibleLayerCount == 1 ? visibleLayerPixels[0] : null;
            bool hasFloating = _selectionService.IsFloating && _selectionService.FloatingPixels != null;
            int floatingX = _selectionService.FloatingX;
            int floatingY = _selectionService.FloatingY;
            int floatingW = _selectionService.FloatingWidth;
            int floatingH = _selectionService.FloatingHeight;
            bool[,]? floatingPixels = _selectionService.FloatingPixels;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = (y * w) + x;
                    bool isPixelOn = ComposePixelState(i, visibleLayerPixels, visibleLayerCount, singleLayerPixels);

                    if (hasFloating && floatingPixels != null)
                    {
                        int fx = x - floatingX;
                        int fy = y - floatingY;
                        if (fx >= 0 && fx < floatingW &&
                            fy >= 0 && fy < floatingH &&
                            floatingPixels[fx, fy])
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
            using (BeginDrawPerfScope("WritePixels.Region.Canvas"))
            {
                CanvasBitmap.WritePixels(srcRect, _canvasBuffer, w * 4, x0, y0);
            }
            using (BeginDrawPerfScope("WritePixels.Region.Preview"))
            {
                PreviewBitmap.WritePixels(srcRect, _previewBuffer, w * 4, x0, y0);
            }
            if (updatePreviewSimulation)
                UpdatePreviewSimulation();
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
        /// Tries to begin a floating selection drag. Returns true if successful.
        /// </summary>
        public bool TryBeginSelectionDrag(int pixelX, int pixelY)
            => _selectionInput.TryBeginDrag(pixelX, pixelY);

        public TransformHandle HitTestSelectionHandle(double mouseImgX, double mouseImgY, double actualW, double actualH)
            => _selectionInput.HitTestHandle(mouseImgX, mouseImgY, actualW, actualH);

        public bool TryBeginSelectionTransform(TransformHandle handle)
            => _selectionInput.TryBeginTransform(handle);

        public void EnterSelectionTransformMode()
            => _selectionInput.EnterTransformMode();

        public void UpdateSelectionTransform(int deltaX, int deltaY, bool shiftAspect, bool altFromCenter)
            => _selectionInput.UpdateTransformFromDelta(deltaX, deltaY, shiftAspect, altFromCenter);

        public void CommitSelectionTransformIfActive()
            => _selectionInput.CommitTransformIfActive();

        public void CancelSelectionTransformIfActive()
            => _selectionInput.CancelTransformIfActive();

        public void SaveStateForUndo()
        {
            SpriteState.NormalizeLayerState();
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
                // Code generation is fed a pre-projected pixel buffer so the generator
                // does not need to understand layer semantics directly.
                var exportState = BuildCodeGenerationState();
                string code = await _codeGen.GenerateCodeAsync(
                    exportState,
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
                    byteCount = exportState != null
                        ? exportState.Height * (int)Math.Ceiling(exportState.Width / 8.0)
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
            catch (OperationCanceledException)
            {
                _uiContext.Post(_ => { _isUpdatingProgrammatically = false; }, null);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "MainViewModel.UpdateTextOutputsAsync");
                _uiContext.Post(_ =>
                {
                    _isUpdatingProgrammatically = false;
                    ShowStatus("⚠ Could not generate export code");
                }, null);
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

        private SpriteState BuildCodeGenerationState()
        {
            var exportState = SpriteState.Clone();
            exportState.EnsureLayers();

            // Export contract: code generation always receives the merged result of
            // all visible layers. Hidden layers are excluded.
            exportState.Pixels = SpriteState.CompositeVisiblePixels();
            return exportState;
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


        /// <summary>
        /// Cancels any in-progress shape drawing and resets tracking state.
        /// Called by the View when switching tools to prevent stale draw flags.
        /// </summary>
        public void CancelInProgressDrawing() => _toolInput.CancelInProgressDrawing();

        // ── Private: tool selection ────────────────────────────────────────

        private void ExecuteSelectTool(string? toolName)
        {
            if (toolName == null) return;

            // Commit floating pixels before switching tool, then re-apply the
            // selection bounds as a non-floating mask so drawing stays clipped.
            if (_selectionService.IsFloating)
            {
                // Snapshot BEFORE CommitIfActive wipes all state.
                var mask = _selectionService.Mask;
                var floatX = _selectionService.FloatingX;
                var floatY = _selectionService.FloatingY;
                var floatW = _selectionService.FloatingWidth;
                var floatH = _selectionService.FloatingHeight;

                _selectionInput.CommitIfActive();

                // Rebuild a mask covering the former floating area so the
                // user can only draw inside that region until they deselect.
                int maxX = floatX + floatW - 1;
                int maxY = floatY + floatH - 1;

                bool[,] residualMask;
                if (mask != null)
                {
                    residualMask = mask;
                }
                else
                {
                    residualMask = new bool[floatW, floatH];
                    for (int ry = 0; ry < floatH; ry++)
                        for (int rx = 0; rx < floatW; rx++)
                            residualMask[rx, ry] = true;
                }

                _selectionService.ApplyMask(
                    residualMask,
                    floatX, floatY,
                    maxX, maxY,
                    Core.SelectionMode.Replace);
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
            SpriteState.NormalizeLayerState();
            _selectedLayerIndex = SpriteState.ActiveLayerIndex;
            IsDisplayInverted = state.IsDisplayInverted; // restores the visual invert flag

            if (state.SelectionSnapshot != null)
                _selectionService.RestoreSnapshot(state.SelectionSnapshot);
            else
                _selectionService.Cancel();
            RedrawGridFromMemory();
            MarkCodeStale();
            HistoryRestored?.Invoke(this, EventArgs.Empty);
        }

        private void AddLayer()
        {
            SpriteState.EnsureLayers();
            ApplyLayerMutation(() =>
            {
                int layerNumber = SpriteState.Layers.Count + 1;
                SpriteState.Layers.Insert(0, new LayerState
                {
                    Name = $"Layer {layerNumber}",
                    IsVisible = true,
                    Pixels = new bool[SpriteState.Width * SpriteState.Height]
                });
                SpriteState.SetActiveLayer(0);
            }, redraw: false);
        }

        private void DeleteActiveLayer()
        {
            SpriteState.EnsureLayers();
            if (SpriteState.Layers.Count <= 1) return;

            int idx = SpriteState.ActiveLayerIndex;
            ApplyLayerMutation(() =>
            {
                SpriteState.Layers.RemoveAt(idx);
                int fallback = Math.Clamp(idx, 0, SpriteState.Layers.Count - 1);
                if (!SpriteState.Layers[fallback].IsVisible)
                {
                    for (int i = fallback; i >= 0; i--)
                    {
                        if (SpriteState.Layers[i].IsVisible)
                        {
                            fallback = i;
                            break;
                        }
                    }
                }
                SpriteState.SetActiveLayer(fallback);
            });
        }

        private void DuplicateActiveLayer()
        {
            SpriteState.EnsureLayers();
            int idx = SpriteState.ActiveLayerIndex;
            ApplyLayerMutation(() =>
            {
                var duplicated = SpriteState.Layers[idx].Clone();
                duplicated.Name = $"{duplicated.Name} Copy";
                SpriteState.Layers.Insert(idx, duplicated);
                SpriteState.SetActiveLayer(idx);
            });
        }

        private void MoveActiveLayerUp()
        {
            if (SpriteState.ActiveLayerIndex <= 0) return;
            MoveLayer(SpriteState.ActiveLayerIndex, SpriteState.ActiveLayerIndex - 1);
        }

        private void MoveActiveLayerDown()
        {
            if (SpriteState.ActiveLayerIndex >= SpriteState.Layers.Count - 1) return;
            MoveLayer(SpriteState.ActiveLayerIndex, SpriteState.ActiveLayerIndex + 1);
        }

        private void ApplyLayerMutation(Action mutation, bool redraw = true, bool rebuildLayerList = true, bool markCodeStale = true)
        {
            SaveStateForUndo();
            mutation();
            SpriteState.NormalizeLayerState();
            if (rebuildLayerList)
                RebuildLayerViewModels();
            else
                SyncLayerActiveFlags();
            if (redraw)
                RedrawGridFromMemory();
            if (markCodeStale)
                MarkCodeStale();
        }

        private void RebuildLayerViewModels()
        {
            if (SpriteState == null) return;
            _suppressSelectedLayerBindingToState = true;
            try
            {
                Layers.Clear();
                for (int i = 0; i < SpriteState.Layers.Count; i++)
                {
                    var layer = SpriteState.Layers[i];
                    Layers.Add(new LayerItemViewModel
                    {
                        Name = layer.Name,
                        IsVisible = layer.IsVisible,
                        IsLocked = layer.IsLocked,
                        IsActive = i == SpriteState.ActiveLayerIndex
                    });
                }
                _selectedLayerIndex = SpriteState.ActiveLayerIndex;
                OnPropertyChanged(nameof(SelectedLayerIndex));
            }
            finally
            {
                _suppressSelectedLayerBindingToState = false;
            }

            OnPropertyChanged(nameof(IsActiveLayerLocked));
        }

        private void SyncLayerActiveFlags()
        {
            for (int i = 0; i < Layers.Count; i++)
                Layers[i].IsActive = i == SpriteState.ActiveLayerIndex;
            OnPropertyChanged(nameof(IsActiveLayerLocked));
        }

        // ── Private: color initialization ─────────────────────────────────

        /// <summary>
        /// Reads the canvas pixel colors from the current theme resources.
        /// Called once at construction and again whenever the theme changes.
        /// </summary>
        public void InitializeBrushColors()
        {
            var res = Application.Current.Resources;
            var colorOff = ((SolidColorBrush)res["Brush.Canvas.Base"]).Color;
            var colorOn = ((SolidColorBrush)res["Brush.Canvas.Drawing"]).Color;
            var prevOff = ((SolidColorBrush)res["Brush.Preview.Base"]).Color;
            var prevOn = ((SolidColorBrush)res["Brush.Preview.Drawing"]).Color;

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
            var res = Application.Current.Resources;
            string bgKey, fgKey;

            switch (_previewDisplayType)
            {
                case DisplayType.SSD1306_Blue:
                    bgKey = "Palette.Preview.OLED.Blue.BG";
                    fgKey = "Palette.Preview.OLED.Blue.FG";
                    break;
                case DisplayType.SSD1306_Green:
                    bgKey = "Palette.Preview.OLED.Green.BG";
                    fgKey = "Palette.Preview.OLED.Green.FG";
                    break;
                case DisplayType.ePaper:
                    bgKey = "Palette.Preview.EPaper.BG";
                    fgKey = "Palette.Preview.EPaper.FG";
                    break;
                case DisplayType.FlipperZero:
                    bgKey = "Palette.Preview.FlipperZero.BG";
                    fgKey = "Palette.Preview.FlipperZero.FG";
                    break;
                case DisplayType.Generic_White:
                default:
                    // Keep Generic preview theme-independent so it remains visually
                    // consistent when the app theme changes.
                    res["Brush.Preview.Base"] = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
                    res["Brush.Preview.Drawing"] = new SolidColorBrush(Color.FromRgb(0xBC, 0xD0, 0xDD));
                    return;
            }

            if (res.Contains(bgKey) && res.Contains(fgKey))
            {
                var bg = (Color)res[bgKey];
                var fg = (Color)res[fgKey];
                res["Brush.Preview.Base"] = new SolidColorBrush(bg);
                res["Brush.Preview.Drawing"] = new SolidColorBrush(fg);
            }
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
            EnsurePreviewSimBitmap();
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
            OnPropertyChanged(nameof(PreviewEffectiveScale));
            OnPropertyChanged(nameof(IsPreviewScaleCapped));
            OnPropertyChanged(nameof(MaxUsefulPreviewScale));
            OnPropertyChanged(nameof(CanIncreasePreviewScale));
            OnPropertyChanged(nameof(CanDecreasePreviewScale));
            OnPropertyChanged(nameof(PreviewScaleStatusText));
            OnPropertyChanged(nameof(CanvasDisplayWidth));
            OnPropertyChanged(nameof(CanvasDisplayHeight));
            OnPropertyChanged(nameof(DynamicStrokeThickness));
            OnPropertyChanged(nameof(SelectionStrokeThickness));
            OnPropertyChanged(nameof(PreviewScaleText));
            OnPropertyChanged(nameof(CanvasDimensionText));
        }

        private void ApplySavedEditorPreferences()
        {
            var prefs = UserPreferencesService.Get();
            _currentTool = prefs.LastTool;
            _showGridLines = prefs.ShowGridLines;
            _brushSize = Math.Clamp(prefs.BrushSize, 1, 64);
            _brushShape = prefs.BrushShape;
            _brushAngle = ((prefs.BrushAngle % 360) + 360) % 360;
            _isPixelPerfectEnabled = prefs.IsPixelPerfectEnabled;
            _previewScale = Math.Max(1, prefs.PreviewScale);
            _previewDisplayType = (DisplayType)Math.Clamp(prefs.PreviewDisplayTypeIndex, 0, 4);
            _useRealisticPreview = prefs.UseRealisticPreview;
            _previewRealismStrength = Math.Clamp(prefs.PreviewRealismStrength, 0, 100);
            _previewQuality = (PreviewQuality)Math.Clamp(prefs.PreviewQuality, 0, 2);
            UpdatePreviewColors();
        }

        private void SaveEditorPreferences()
        {
            UserPreferencesService.Update(p =>
            {
                p.LastTool = _currentTool;
                p.ShowGridLines = _showGridLines;
                p.BrushSize = _brushSize;
                p.BrushShape = _brushShape;
                p.BrushAngle = _brushAngle;
                p.IsPixelPerfectEnabled = _isPixelPerfectEnabled;
                p.PreviewScale = _previewScale;
                p.PreviewDisplayTypeIndex = (int)_previewDisplayType;
                p.UseRealisticPreview = _useRealisticPreview;
                p.PreviewRealismStrength = _previewRealismStrength;
                p.PreviewQuality = (int)_previewQuality;
            });
        }

        private DisplaySimulationPreset GetDisplaySimulationPreset()
        {
            return _previewDisplayType switch
            {
                DisplayType.SSD1306_Blue => DisplaySimulationPreset.Ssd1306OledBlue,
                DisplayType.SSD1306_Green => DisplaySimulationPreset.Ssd1306OledGreen,
                DisplayType.ePaper => DisplaySimulationPreset.EPaper,
                _ => DisplaySimulationPreset.GenericLcd
            };
        }

        private void EnsurePreviewSimBitmap()
        {
            int w = Math.Max(1, PreviewWidth);
            int h = Math.Max(1, PreviewHeight);

            if (_previewSimBitmap == null || _previewSimBitmap.PixelWidth != w || _previewSimBitmap.PixelHeight != h)
            {
                PreviewSimBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _previewSimBuffer = new uint[w * h];
                OnPropertyChanged(nameof(DisplayPreviewBitmap));
            }
            else if (_previewSimBuffer.Length != w * h)
            {
                _previewSimBuffer = new uint[w * h];
            }
        }

        public void UpdatePreviewSimulation(bool force = false)
        {
            if (!UseRealisticPreview)
                return;

            if (SpriteState == null)
                return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (!force && (nowTicks - _lastPreviewSimulationTicks) < PreviewSimulationMinIntervalTicks)
                return;
            if (!force && _isStrokeRenderingActive)
            {
                _pendingPreviewSimulationAfterStroke = true;
                return;
            }
            using var perfScope = BeginDrawPerfScope("UpdatePreviewSimulation");

            EnsurePreviewSimBitmap();

            var res = Application.Current.Resources;
            Color bg;
            Color fg;
            if (res.Contains("Brush.Preview.Base") && res.Contains("Brush.Preview.Drawing"))
            {
                bg = ((SolidColorBrush)res["Brush.Preview.Base"]).Color;
                fg = ((SolidColorBrush)res["Brush.Preview.Drawing"]).Color;
            }
            else
            {
                bg = ((SolidColorBrush)res["Brush.Canvas.Base"]).Color;
                fg = ((SolidColorBrush)res["Brush.Canvas.Drawing"]).Color;
            }

            // Perceptual compensation: on light UI themes, the same emitted-pixel colors
            // can look darker by contrast. Apply a subtle boost to the display colors
            // (not the bezel) to keep the preview looking consistent across themes.
            if (res.Contains("Brush.Surface.Base"))
            {
                var uiBase = ((SolidColorBrush)res["Brush.Surface.Base"]).Color;
                if (GetRelativeLuminance(uiBase) > 0.55)
                {
                    fg = BoostTowardsWhite(fg, 0.10);
                    // Keep OLED blacks mostly black; only a tiny lift to reduce crushing perception.
                    bg = BoostTowardsWhite(bg, 0.02);
                }
            }

            Hexprite.Rendering.DisplaySimulationRenderer.Render(
                SpriteState,
                _selectionService,
                PreviewSimBitmap.PixelWidth,
                PreviewSimBitmap.PixelHeight,
                bg,
                fg,
                GetDisplaySimulationPreset(),
                _previewQuality,
                PreviewRealismStrength / 100.0,
                PreviewScaleEffective,
                IsPreviewScaleCapped,
                _previewSimBuffer);

            var rect = new Int32Rect(0, 0, PreviewSimBitmap.PixelWidth, PreviewSimBitmap.PixelHeight);
            using (BeginDrawPerfScope("WritePixels.PreviewSim"))
            {
                PreviewSimBitmap.WritePixels(rect, _previewSimBuffer, PreviewSimBitmap.PixelWidth * 4, 0);
            }
            _lastPreviewSimulationTicks = nowTicks;
        }

        public void BeginStrokeRenderSession()
        {
            _isStrokeRenderingActive = true;
        }

        public void EndStrokeRenderSession()
        {
            _isStrokeRenderingActive = false;
            if (_pendingPreviewSimulationAfterStroke)
            {
                _pendingPreviewSimulationAfterStroke = false;
                UpdatePreviewSimulation(force: true);
            }
        }

        public IDisposable BeginMovePerfScope() => BeginDrawPerfScope("HandleToolMove");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ComposePixelState(int pixelIndex, List<bool[]> visibleLayerPixels, int visibleLayerCount, bool[]? singleLayerPixels)
        {
            if (visibleLayerCount == 0)
                return false;
            if (visibleLayerCount == 1)
                return singleLayerPixels![pixelIndex];

            for (int li = 0; li < visibleLayerPixels.Count; li++)
            {
                if (visibleLayerPixels[li][pixelIndex])
                    return true;
            }
            return false;
        }

        private List<bool[]> GetVisibleLayerPixels()
        {
            var layers = SpriteState.Layers;
            var visible = new List<bool[]>(layers.Count);
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].IsVisible)
                    visible.Add(layers[i].Pixels);
            }
            return visible;
        }

        private IDisposable BeginDrawPerfScope(string scope)
        {
#if DEBUG
            return _drawPerf.Begin(scope);
#else
            return NoopDisposable.Instance;
#endif
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }

#if DEBUG
        private sealed class DrawPerfCollector
        {
            private const int SampleWindow = 180;
            private readonly Dictionary<string, SampleBucket> _buckets = new(StringComparer.Ordinal);

            public IDisposable Begin(string scope) => new Scope(this, scope, Stopwatch.GetTimestamp());

            private void Commit(string scope, long startTimestamp)
            {
                long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
                if (!_buckets.TryGetValue(scope, out var bucket))
                {
                    bucket = new SampleBucket();
                    _buckets[scope] = bucket;
                }
                bucket.Add(elapsedTicks);
                if (bucket.Count >= SampleWindow)
                {
                    Log.Debug("DrawPerf {Scope}: avg={AvgMs:F3}ms p95={P95Ms:F3}ms max={MaxMs:F3}ms n={Count}",
                        scope, bucket.GetAverageMs(), bucket.GetP95Ms(), bucket.GetMaxMs(), bucket.Count);
                    bucket.Reset();
                }
            }

            private sealed class Scope : IDisposable
            {
                private readonly DrawPerfCollector _owner;
                private readonly string _scope;
                private readonly long _startTimestamp;
                private bool _disposed;

                public Scope(DrawPerfCollector owner, string scope, long startTimestamp)
                {
                    _owner = owner;
                    _scope = scope;
                    _startTimestamp = startTimestamp;
                }

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    _owner.Commit(_scope, _startTimestamp);
                }
            }

            private sealed class SampleBucket
            {
                private readonly List<long> _samples = new(SampleWindow);
                private long _sumTicks;
                private long _maxTicks;
                public int Count => _samples.Count;

                public void Add(long ticks)
                {
                    _samples.Add(ticks);
                    _sumTicks += ticks;
                    if (ticks > _maxTicks) _maxTicks = ticks;
                }

                public double GetAverageMs() => TicksToMs(_sumTicks / (double)Math.Max(1, _samples.Count));

                public double GetP95Ms()
                {
                    if (_samples.Count == 0) return 0;
                    _samples.Sort();
                    int index = (int)Math.Ceiling(_samples.Count * 0.95) - 1;
                    index = Math.Clamp(index, 0, _samples.Count - 1);
                    return TicksToMs(_samples[index]);
                }

                public double GetMaxMs() => TicksToMs(_maxTicks);

                public void Reset()
                {
                    _samples.Clear();
                    _sumTicks = 0;
                    _maxTicks = 0;
                }

                private static double TicksToMs(double ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            }
        }
#endif

        private static double GetRelativeLuminance(Color c)
        {
            static double SrgbToLinear(double v)
                => v <= 0.04045 ? (v / 12.92) : Math.Pow((v + 0.055) / 1.055, 2.4);

            double r = SrgbToLinear(c.R / 255.0);
            double g = SrgbToLinear(c.G / 255.0);
            double b = SrgbToLinear(c.B / 255.0);
            return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        }

        private static Color BoostTowardsWhite(Color c, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            byte r = (byte)Math.Clamp((int)Math.Round(c.R + (255 - c.R) * t), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(c.G + (255 - c.G) * t), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(c.B + (255 - c.B) * t), 0, 255);
            return Color.FromArgb(c.A, r, g, b);
        }
    }
}
