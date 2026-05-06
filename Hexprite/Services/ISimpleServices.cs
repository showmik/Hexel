using Hexprite.Core;

namespace Hexprite.Services
{
    public sealed class BugReportInput
    {
        public string Summary { get; set; } = string.Empty;
        public string StepsToReproduce { get; set; } = string.Empty;
        public string ExpectedBehavior { get; set; } = string.Empty;
        public string ActualBehavior { get; set; } = string.Empty;
        public string? ContactEmail { get; set; }
        public bool IncludeContactEmail { get; set; }
        public bool IncludeRecentLogs { get; set; } = true;
    }

    public sealed class UserFeedbackInput
    {
        public string Category { get; set; } = "General";
        public string Message { get; set; } = string.Empty;
        public string? ContactEmail { get; set; }
        public bool IncludeContactEmail { get; set; }
        public bool IncludeRecentLogs { get; set; }
    }

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

        /// <summary>
        /// Shows the "About Hexprite" dialog.
        /// </summary>
        void ShowAboutDialog();

        /// <summary>
        /// Shows the "Import from Code" dialog.
        /// Returns (width, height, code, spriteName, isXbm) if the user clicks Import, or null if cancelled.
        /// </summary>
        (int Width, int Height, string Code, string? SpriteName, bool IsXbm)? ShowImportFromCodeDialog();

        /// <summary>
        /// Shows the bitmap import settings dialog.
        /// Returns selected settings, or null if cancelled.
        /// </summary>
        BitmapImportSettings? ShowImportBitmapDialog(string fileName, BitmapImportSettings initialSettings);

        /// <summary>
        /// Shows the "Report a Bug" dialog and returns user-entered data, or null if cancelled.
        /// </summary>
        BugReportInput? ShowBugReportDialog();

        /// <summary>
        /// Shows confirmation after a bug report was submitted, with optional copyable reference ID.
        /// </summary>
        /// <param name="successWindowTitle">Optional window title; default is for bug reports.</param>
        void ShowBugReportSuccessDialog(string message, string? reportId, string? successWindowTitle = null);

        /// <summary>
        /// Shows the "Send Feedback" dialog and returns user-entered data, or null if cancelled.
        /// </summary>
        UserFeedbackInput? ShowUserFeedbackDialog();

        /// <summary>
        /// Shows the Privacy Settings dialog and persists changes when accepted.
        /// </summary>
        bool ShowPrivacySettingsDialog();
    }

}
