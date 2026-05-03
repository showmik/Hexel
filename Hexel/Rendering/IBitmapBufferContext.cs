using Hexel.Core;
using Hexel.Services;
using System.Windows.Media.Imaging;

namespace Hexel.Rendering
{
    /// <summary>
    /// Provides the bitmap buffer data needed by <see cref="BitmapPreviewRenderer"/>
    /// without exposing the full MainViewModel. This decouples the renderer from
    /// ViewModel internals, making both easier to maintain and test.
    /// </summary>
    public interface IBitmapBufferContext
    {
        SpriteState SpriteState { get; }
        uint[] CanvasBuffer { get; }
        uint[] PreviewBuffer { get; }
        uint ColorOnUint { get; }
        uint ColorOffUint { get; }
        uint PreviewOnUint { get; }
        uint PreviewOffUint { get; }
        WriteableBitmap CanvasBitmap { get; }
        WriteableBitmap PreviewBitmap { get; }
        ISelectionService SelectionService { get; }
        void RedrawGridFromMemory();
    }
}
