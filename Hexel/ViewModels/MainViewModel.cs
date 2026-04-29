using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using Hexel.Services;
using System;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Hexel.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IHistoryService _historyService;

        private bool _isUpdatingProgrammatically = false;
        // Brushes used for UI representation
        private readonly SolidColorBrush _colorOff = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        private readonly SolidColorBrush _colorOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));
        private readonly SolidColorBrush _previewOff = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _previewOn = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFFF"));

        public ObservableCollection<SolidColorBrush> PixelBrushes { get; } = new ObservableCollection<SolidColorBrush>();
        public ObservableCollection<SolidColorBrush> PreviewBrushes { get; } = new ObservableCollection<SolidColorBrush>();

        // Tool & selection state
        private ToolMode _currentTool = ToolMode.Pencil;
        public ToolMode CurrentTool { get => _currentTool; set => SetProperty(ref _currentTool, value); }

        private int _lastClickedIndex = -1;

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

        private SpriteState _spriteState;
        public SpriteState SpriteState
        {
            get => _spriteState;
            set => SetProperty(ref _spriteState, value);
        }

        private string _txtBinary = string.Empty;
        public string TxtBinary
        {
            get => _txtBinary;
            set
            {
                if (_isUpdatingProgrammatically) return;
                SetProperty(ref _txtBinary, value);
                _codeGen.ParseBinaryToState(value, SpriteState);
                RedrawGridFromMemory();
                UpdateTextOutputs();
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
                _codeGen.ParseHexToState(value, SpriteState);
                RedrawGridFromMemory();
                UpdateTextOutputs();
            }
        }

        private bool _isDisplayInverted = false;
        public bool IsDisplayInverted
        {
            get => _isDisplayInverted;
            set => SetProperty(ref _isDisplayInverted, value);
        }

        public IRelayCommand UndoCommand { get; }
        public IRelayCommand RedoCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand InvertCommand { get; }

        public MainViewModel(ICodeGeneratorService codeGen, IDrawingService drawingService, IHistoryService historyService)
        {
            _codeGen = codeGen ?? throw new ArgumentNullException(nameof(codeGen));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));

            SpriteState = new SpriteState(16);
            InitializeGrid(16);

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

            UpdateTextOutputs();
        }

        public void InitializeGrid(int size)
        {
            SpriteState = new SpriteState(size);
            PixelBrushes.Clear();
            PreviewBrushes.Clear();
            for (int i = 0; i < size * size; i++)
            {
                PixelBrushes.Add(_colorOff);
                PreviewBrushes.Add(_previewOff);
            }
            UpdateTextOutputs();
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

        public void ShiftGrid(int offsetX, int offsetY)
        {
            _historyService.SaveState(SpriteState);
            _drawingService.ShiftGrid(SpriteState, offsetX, offsetY);
            RedrawGridFromMemory();
            UpdateTextOutputs();
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
            return _codeGen.GenerateExportStrings(SpriteState, isFloating, floatingPixels, floatX, floatY, floatW, floatH);
        }

        public void RedrawGridFromMemory()
        {
            for (int y = 0; y < SpriteState.Size; y++)
            {
                for (int x = 0; x < SpriteState.Size; x++)
                {
                    int i = (y * SpriteState.Size) + x;
                    bool isPixelOn = SpriteState.Pixels[i];

                    if (IsFloating && FloatingPixels != null)
                    {
                        int floatLocalX = x - FloatingX;
                        int floatLocalY = y - FloatingY;
                        if (floatLocalX >= 0 && floatLocalX < FloatingWidth && floatLocalY >= 0 && floatLocalY < FloatingHeight)
                            if (FloatingPixels[floatLocalX, floatLocalY]) isPixelOn = true;
                    }

                    // Inside MainViewModel.cs -> RedrawGridFromMemory()
                    var newBrush = isPixelOn ? _colorOn : _colorOff;
                    var newPrevBrush = isPixelOn ? _previewOn : _previewOff;

                    // Only trigger an ObservableCollection Replace event if the state actually changed!
                    if (PixelBrushes[i] != newBrush) PixelBrushes[i] = newBrush;
                    if (PreviewBrushes[i] != newPrevBrush) PreviewBrushes[i] = newPrevBrush;
                }
            }
        }

        public async System.Threading.Tasks.Task UpdateTextOutputsAsync()
        {
            if (_isUpdatingProgrammatically) return;
            _isUpdatingProgrammatically = true;

            // Run the generation logic on a background thread (keeps the UI unblocked)
            var (binary, hex) = await _codeGen.GenerateExportStringsAsync(
                SpriteState, IsFloating, FloatingPixels, FloatingX, FloatingY, FloatingWidth, FloatingHeight);

            // Marshal the results back to the main UI thread securely
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _txtBinary = binary;
                _txtHex = hex;
                OnPropertyChanged(nameof(TxtBinary));
                OnPropertyChanged(nameof(TxtHex));
            });

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

        public void DrawLineContinuous(int startIdx, int endIdx, bool newState)
        {
            // Draws the line without saving to the Undo history stack
            _drawingService.DrawLine(SpriteState, startIdx, endIdx, newState);

            // Updates the visual brushes (using the optimized diffing we added earlier)
            RedrawGridFromMemory();
        }
    }
}
