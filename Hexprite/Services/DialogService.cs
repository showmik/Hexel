using System.Windows;
using Hexprite.Core;
using Hexprite.Views;
using Microsoft.Win32;

namespace Hexprite.Services
{
    public class DialogService : IDialogService
    {
        public void ShowMessage(string message)
        {
            MessageBox.Show(message);
        }

        public (int Width, int Height)? ShowNewCanvasDialog()
        {
            var dlg = new NewCanvasDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && dlg.Result.HasValue)
                return dlg.Result.Value;
            return null;
        }

        public (int Width, int Height, ResizeAnchor Anchor)? ShowResizeCanvasDialog(int currentWidth, int currentHeight)
        {
            var dlg = new ResizeCanvasDialog(currentWidth, currentHeight)
            {
                Owner = Application.Current.MainWindow
            };
            if (dlg.ShowDialog() == true && dlg.Result.HasValue)
                return dlg.Result.Value;
            return null;
        }

        public string? ShowOpenFileDialog(string filter, string title)
        {
            var dialog = new OpenFileDialog
            {
                Filter = filter,
                Title = title
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? ShowSaveFileDialog(string filter, string title, string defaultExt)
        {
            var dialog = new SaveFileDialog
            {
                Filter = filter,
                Title = title,
                DefaultExt = defaultExt
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public bool? ShowUnsavedChangesDialog(string documentName)
        {
            var result = MessageBox.Show(
                $"Save changes to \"{documentName}\"?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            return result switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No => false,
                _ => null // Cancel
            };
        }

        public void ShowAboutDialog()
        {
            var dlg = new AboutDialog { Owner = Application.Current.MainWindow };
            dlg.ShowDialog();
        }

        public (int Width, int Height, string Code, string? SpriteName, bool IsXbm)? ShowImportFromCodeDialog()
        {
            var dlg = new ImportFromCodeDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && dlg.Result.HasValue)
                return dlg.Result.Value;
            return null;
        }

        public BitmapImportSettings? ShowImportBitmapDialog(string fileName, BitmapImportSettings initialSettings)
        {
            var dlg = new ImportBitmapDialog(fileName, initialSettings) { Owner = Application.Current.MainWindow };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }

        public BugReportInput? ShowBugReportDialog()
        {
            var dlg = new ReportBugDialog { Owner = Application.Current.MainWindow };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }

        public void ShowBugReportSuccessDialog(string message, string? reportId, string? successWindowTitle = null)
        {
            var dlg = new BugReportSuccessDialog(message, reportId, successWindowTitle)
            {
                Owner = Application.Current.MainWindow
            };
            dlg.ShowDialog();
        }

        public UserFeedbackInput? ShowUserFeedbackDialog()
        {
            var dlg = new UserFeedbackDialog { Owner = Application.Current.MainWindow };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }

        public bool ShowPrivacySettingsDialog()
        {
            var dlg = new PrivacySettingsDialog { Owner = Application.Current.MainWindow };
            return dlg.ShowDialog() == true;
        }
    }
}