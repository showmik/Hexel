using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using Hexel.Services;
using Hexel.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Hexel.ViewModels
{
    /// <summary>
    /// Application-shell ViewModel that owns the tab collection.
    /// Each tab is a <see cref="MainViewModel"/> instance with its own
    /// undo history, selection state, and file path.
    /// </summary>
    public class ShellViewModel : ObservableObject
    {
        // ── Services (shared across all documents) ────────────────────────
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IClipboardService _clipboardService;
        private readonly IDialogService _dialogService;

        public const int MaxTabs = 10;

        // ── Tab collection ────────────────────────────────────────────────
        public ObservableCollection<MainViewModel> OpenDocuments { get; } = new();

        private MainViewModel? _activeDocument;
        public MainViewModel? ActiveDocument
        {
            get => _activeDocument;
            set
            {
                var old = _activeDocument;
                if (SetProperty(ref _activeDocument, value))
                {
                    if (old != null) old.IsActive = false;
                    if (value != null) value.IsActive = true;
                    OnPropertyChanged(nameof(HasOpenDocument));
                }
            }
        }

        public bool HasOpenDocument => ActiveDocument != null;

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand NewCanvasCommand { get; }
        public IRelayCommand OpenCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand SaveAsCommand { get; }
        public IRelayCommand<MainViewModel> CloseTabCommand { get; }
        public IRelayCommand ResizeCanvasCommand { get; }

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>Raised when a tab is added so the View can wire events.</summary>
        public event EventHandler<MainViewModel>? TabAdded;
        /// <summary>Raised when a tab is about to be removed.</summary>
        public event EventHandler<MainViewModel>? TabRemoved;
        /// <summary>Raised when the active tab changes.</summary>
        public event EventHandler? ActiveTabChanged;

        public ShellViewModel(
            ICodeGeneratorService codeGen,
            IDrawingService drawingService,
            IClipboardService clipboardService,
            IDialogService dialogService)
        {
            _codeGen = codeGen;
            _drawingService = drawingService;
            _clipboardService = clipboardService;
            _dialogService = dialogService;

            NewCanvasCommand = new RelayCommand(ExecuteNewCanvas);
            OpenCommand = new RelayCommand(ExecuteOpen);
            SaveCommand = new RelayCommand(ExecuteSave, () => HasOpenDocument);
            SaveAsCommand = new RelayCommand(ExecuteSaveAs, () => HasOpenDocument);
            CloseTabCommand = new RelayCommand<MainViewModel>(ExecuteCloseTab);
            ResizeCanvasCommand = new RelayCommand(ExecuteResizeCanvas, () => HasOpenDocument);

            // Start with one blank document
            AddNewDocument(16, 16);
        }

        // ── New Canvas ────────────────────────────────────────────────────

        private void ExecuteNewCanvas()
        {
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                return;
            }

            var dlg = new NewCanvasDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && dlg.Result.HasValue)
            {
                var (w, h) = dlg.Result.Value;
                AddNewDocument(w, h);
            }
        }

        // ── Open ──────────────────────────────────────────────────────────

        private void ExecuteOpen()
        {
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Hexel Sprite (*.hexel)|*.hexel|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Open Sprite"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                string json = File.ReadAllText(dialog.FileName);
                var loaded = JsonSerializer.Deserialize<SpriteState>(json);
                if (loaded?.Pixels == null) return;

                var doc = CreateDocument(loaded.Width, loaded.Height);
                doc.SpriteState.Pixels = (bool[])loaded.Pixels.Clone();
                doc.SpriteState.IsDisplayInverted = loaded.IsDisplayInverted;
                doc.IsDisplayInverted = loaded.IsDisplayInverted;
                doc.FilePath = dialog.FileName;
                doc.IsDirty = false;
                doc.RedrawGridFromMemory();
                doc.UpdateTextOutputs();

                OpenDocuments.Add(doc);
                ActiveDocument = doc;
                TabAdded?.Invoke(this, doc);
                RaiseActiveTabChanged();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"Error opening file: {ex.Message}");
            }
        }

        // ── Save / Save As ────────────────────────────────────────────────

        private void ExecuteSave()
        {
            if (ActiveDocument == null) return;

            if (ActiveDocument.FilePath != null)
                SaveToPath(ActiveDocument, ActiveDocument.FilePath);
            else
                ExecuteSaveAs();
        }

        private void ExecuteSaveAs()
        {
            if (ActiveDocument == null) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Hexel Sprite (*.hexel)|*.hexel|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".hexel",
                Title = "Save Sprite"
            };

            if (dialog.ShowDialog() == true)
                SaveToPath(ActiveDocument, dialog.FileName);
        }

        private void SaveToPath(MainViewModel doc, string path)
        {
            try
            {
                string json = JsonSerializer.Serialize(doc.SpriteState,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                doc.FilePath = path;
                doc.IsDirty = false;
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"Error saving: {ex.Message}");
            }
        }

        // ── Close Tab ─────────────────────────────────────────────────────

        private void ExecuteCloseTab(MainViewModel? doc)
        {
            doc ??= ActiveDocument;
            if (doc == null) return;

            if (doc.IsDirty)
            {
                var result = MessageBox.Show(
                    $"Save changes to \"{doc.Title.TrimStart('*')}\"?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                switch (result)
                {
                    case MessageBoxResult.Yes:
                        if (doc.FilePath != null)
                            SaveToPath(doc, doc.FilePath);
                        else
                        {
                            var dlg = new Microsoft.Win32.SaveFileDialog
                            {
                                Filter = "Hexel Sprite (*.hexel)|*.hexel|All Files (*.*)|*.*",
                                DefaultExt = ".hexel",
                                Title = "Save Sprite"
                            };
                            if (dlg.ShowDialog() == true)
                                SaveToPath(doc, dlg.FileName);
                            else
                                return; // user cancelled the save dialog
                        }
                        break;
                    case MessageBoxResult.Cancel:
                        return;
                    // MessageBoxResult.No → discard changes, continue closing
                }
            }

            int idx = OpenDocuments.IndexOf(doc);
            TabRemoved?.Invoke(this, doc);
            OpenDocuments.Remove(doc);

            if (OpenDocuments.Count == 0)
            {
                ActiveDocument = null;
                RaiseActiveTabChanged();
            }
            else
            {
                ActiveDocument = OpenDocuments[Math.Min(idx, OpenDocuments.Count - 1)];
                RaiseActiveTabChanged();
            }
        }

        // ── Resize Canvas ─────────────────────────────────────────────────

        private void ExecuteResizeCanvas()
        {
            if (ActiveDocument == null) return;

            var dlg = new ResizeCanvasDialog(
                ActiveDocument.SpriteState.Width,
                ActiveDocument.SpriteState.Height)
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true && dlg.Result.HasValue)
            {
                var (w, h, anchor) = dlg.Result.Value;
                if (w == ActiveDocument.SpriteState.Width &&
                    h == ActiveDocument.SpriteState.Height) return;

                ActiveDocument.ResizeCanvas(w, h, anchor);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void AddNewDocument(int width, int height)
        {
            var doc = CreateDocument(width, height);
            OpenDocuments.Add(doc);
            ActiveDocument = doc;
            TabAdded?.Invoke(this, doc);
            RaiseActiveTabChanged();
        }

        private MainViewModel CreateDocument(int width, int height)
        {
            var history = new HistoryService();
            var selection = new SelectionService();
            var fileService = new FileService();

            var doc = new MainViewModel(
                _codeGen, _drawingService, history, selection,
                _clipboardService, _dialogService, fileService);

            doc.InitializeGrid(width, height);
            return doc;
        }

        private void RaiseActiveTabChanged()
        {
            OnPropertyChanged(nameof(HasOpenDocument));
            ((RelayCommand)SaveCommand).NotifyCanExecuteChanged();
            ((RelayCommand)SaveAsCommand).NotifyCanExecuteChanged();
            ((RelayCommand)ResizeCanvasCommand).NotifyCanExecuteChanged();
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
