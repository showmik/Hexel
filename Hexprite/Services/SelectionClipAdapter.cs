namespace Hexprite.Services
{
    /// <summary>
    /// Adapter that wraps ISelectionService to implement IPixelClip.
    /// Used to pass selection bounds to DrawingService methods.
    /// </summary>
    public sealed class SelectionClipAdapter : IPixelClip
    {
        private readonly ISelectionService _selection;

        public SelectionClipAdapter(ISelectionService selection)
        {
            _selection = selection;
        }

        public bool IsPixelInClip(int x, int y) => _selection.IsPixelInSelection(x, y);
    }
}
