using Hexel.Core;

namespace Hexel.Services
{
    public interface IClipboardService
    {
        void SetText(string text);
    }

    public interface IDialogService
    {
        void ShowMessage(string message);

        /// <summary>
        /// Shows the "New Canvas" dialog and returns (width, height) if the user
        /// clicks Create, or null if they cancel.
        /// </summary>
        (int Width, int Height)? ShowNewCanvasDialog();

        /// <summary>
        /// Shows the "Resize Canvas" dialog seeded with the current dimensions.
        /// Returns (width, height, anchor) if accepted, null if cancelled.
        /// </summary>
        (int Width, int Height, ResizeAnchor Anchor)? ShowResizeCanvasDialog(int currentWidth, int currentHeight);

        /// <summary>
        /// Shows an "Open File" dialog. Returns the selected file path, or null if cancelled.
        /// </summary>
        string? ShowOpenFileDialog(string filter, string title);

        /// <summary>
        /// Shows a "Save File" dialog. Returns the selected file path, or null if cancelled.
        /// </summary>
        string? ShowSaveFileDialog(string filter, string title, string defaultExt);

        /// <summary>
        /// Shows an "Unsaved changes" confirmation. Returns true (save), false (discard),
        /// or null (cancel/abort the operation).
        /// </summary>
        bool? ShowUnsavedChangesDialog(string documentName);
    }

    public interface IFileService
    {
        void SaveSprite(SpriteState state);
        SpriteState? LoadSprite();
    }
}
