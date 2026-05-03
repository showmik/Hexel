using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Hexel.Rendering
{
    /// <summary>
    /// Groups the canvas UI element accessors needed by
    /// <see cref="BrushCursorManager"/> and <see cref="SelectionOverlayRenderer"/>.
    /// Replaces the fragile multi-Func constructor parameters with a single
    /// injectable object, making the wiring in MainWindow much cleaner.
    /// </summary>
    public class CanvasElementProvider
    {
        public Func<Image?> GetCanvasImage { get; }
        public Func<FrameworkElement> GetPixelGridContainer { get; }
        public Func<Image?> GetBrushCursorOverlay { get; }
        public Func<Line?> GetCrosshairH { get; }
        public Func<Line?> GetCrosshairV { get; }
        public Func<Rectangle?> GetMarqueeOverlay { get; }
        public Func<Path?> GetLassoOverlay { get; }

        public CanvasElementProvider(
            Func<Image?> getCanvasImage,
            Func<FrameworkElement> getPixelGridContainer,
            Func<Image?> getBrushCursorOverlay,
            Func<Line?> getCrosshairH,
            Func<Line?> getCrosshairV,
            Func<Rectangle?> getMarqueeOverlay,
            Func<Path?> getLassoOverlay)
        {
            GetCanvasImage = getCanvasImage ?? throw new ArgumentNullException(nameof(getCanvasImage));
            GetPixelGridContainer = getPixelGridContainer ?? throw new ArgumentNullException(nameof(getPixelGridContainer));
            GetBrushCursorOverlay = getBrushCursorOverlay ?? throw new ArgumentNullException(nameof(getBrushCursorOverlay));
            GetCrosshairH = getCrosshairH ?? throw new ArgumentNullException(nameof(getCrosshairH));
            GetCrosshairV = getCrosshairV ?? throw new ArgumentNullException(nameof(getCrosshairV));
            GetMarqueeOverlay = getMarqueeOverlay ?? throw new ArgumentNullException(nameof(getMarqueeOverlay));
            GetLassoOverlay = getLassoOverlay ?? throw new ArgumentNullException(nameof(getLassoOverlay));
        }
    }
}
