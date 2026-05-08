using CommunityToolkit.Mvvm.ComponentModel;

namespace Hexprite.Core
{
    /// <summary>
    /// Encapsulates tool settings (brush size, shape, pixel-perfect mode, etc.)
    /// that persist per document. Extracted from MainViewModel to reduce class size
    /// and improve separation of concerns.
    /// </summary>
    public partial class ToolSettings : ObservableObject
    {
        [ObservableProperty]
        private ToolMode _currentTool = ToolMode.Pencil;

        [ObservableProperty]
        private int _brushSize = 1;

        [ObservableProperty]
        private BrushShape _brushShape = BrushShape.Circle;

        [ObservableProperty]
        private int _brushAngle = 0;

        [ObservableProperty]
        private bool _isPixelPerfectEnabled;

        /// <summary>
        /// Returns whether pixel-perfect mode is available for the current tool configuration.
        /// Pixel-perfect only works with the Pencil tool at brush size 1.
        /// </summary>
        public bool IsPixelPerfectAvailable => CurrentTool == ToolMode.Pencil && BrushSize == 1;

        partial void OnCurrentToolChanged(ToolMode value)
        {
            OnPropertyChanged(nameof(IsPixelPerfectAvailable));
        }

        partial void OnBrushSizeChanged(int value)
        {
            BrushSize = System.Math.Clamp(value, 1, 64);
            OnPropertyChanged(nameof(IsPixelPerfectAvailable));
        }

        partial void OnBrushAngleChanged(int value)
        {
            BrushAngle = ((value % 360) + 360) % 360;
        }
    }
}
