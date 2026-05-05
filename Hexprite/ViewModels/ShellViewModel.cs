using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexprite.Core;
using Hexprite.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Hexprite.ViewModels
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
        private readonly IPixelClipboardService _pixelClipboard;
        private readonly IDialogService _dialogService;
        private readonly IThemeService _themeService;

        private const string FileFilter = "Hexprite Sprite (*.hexprite)|*.hexprite|JSON Files (*.json)|*.json|All Files (*.*)|*.*";

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
        public IRelayCommand NewCanvasCommand { get; }  // accepts optional "WxH" string param
        public IRelayCommand OpenCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand SaveAsCommand { get; }
        public IRelayCommand<MainViewModel> CloseTabCommand { get; }
        public IRelayCommand ResizeCanvasCommand { get; }
        public IRelayCommand OpenDocumentationCommand { get; }
        public IRelayCommand ShowAboutCommand { get; }
        /// <summary>Opens the Import from Code dialog and creates a new tab.</summary>
        public IRelayCommand ImportFromCodeMenuCommand { get; }
        /// <summary>Copies the active document's exported code to the clipboard.</summary>
        public IRelayCommand CopyExportCodeMenuCommand { get; }
        /// <summary>Switches the theme between Dark and Light.</summary>
        public IRelayCommand<string> SwitchThemeCommand { get; }

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>Raised when a tab is added so the View can wire events.</summary>
        public event EventHandler<MainViewModel>? TabAdded;
        /// <summary>Raised when a tab is about to be removed.</summary>
        public event EventHandler<MainViewModel>? TabRemoved;
        public event EventHandler? ActiveTabChanged;
        public event EventHandler? ThemeChanged;

        /// <summary>
        /// Gets whether the current theme is Dark. Used for menu radio-button binding.
        /// </summary>
        public bool IsDarkTheme => _themeService.CurrentTheme == "Dark";

        /// <summary>
        /// Gets whether the current theme is Light. Used for menu radio-button binding.
        /// </summary>
        public bool IsLightTheme => _themeService.CurrentTheme == "Light";

        /// <summary>
        /// Gets whether the current theme is Dim. Used for menu radio-button binding.
        /// </summary>
        public bool IsDimTheme => _themeService.CurrentTheme == "Dim";

        public ShellViewModel(
            ICodeGeneratorService codeGen,
            IDrawingService drawingService,
            IClipboardService clipboardService,
            IDialogService dialogService,
            IThemeService themeService)
        {
            _codeGen = codeGen;
            _drawingService = drawingService;
            _clipboardService = clipboardService;
            _pixelClipboard = new PixelClipboardService();
            _dialogService = dialogService;
            _themeService = themeService;

            NewCanvasCommand = new RelayCommand<object?>(ExecuteNewCanvas);
            OpenCommand = new RelayCommand(ExecuteOpen);
            SaveCommand = new RelayCommand(ExecuteSave, () => HasOpenDocument);
            SaveAsCommand = new RelayCommand(ExecuteSaveAs, () => HasOpenDocument);
            CloseTabCommand = new RelayCommand<MainViewModel>(ExecuteCloseTab);
            ResizeCanvasCommand = new RelayCommand(ExecuteResizeCanvas, () => HasOpenDocument);
            OpenDocumentationCommand = new RelayCommand(ExecuteOpenDocumentation);
            ShowAboutCommand = new RelayCommand(ExecuteShowAbout);
            ImportFromCodeMenuCommand = new RelayCommand(ExecuteImportFromCode);
            CopyExportCodeMenuCommand = new RelayCommand(
                () => ActiveDocument?.CopyExportedCodeCommand.Execute(null),
                () => HasOpenDocument);
            SwitchThemeCommand = new RelayCommand<string>(ExecuteSwitchTheme);

            _themeService.ThemeChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDimTheme));

                // Re-read pixel colors from the new theme and redraw all open canvases
                foreach (var doc in OpenDocuments)
                    doc.RefreshCanvasColors();

                ThemeChanged?.Invoke(this, EventArgs.Empty);
            };

            // No auto-created document — the welcome screen is shown instead
        }

        // ── New Canvas ────────────────────────────────────────────────────

        private void ExecuteNewCanvas(object? parameter)
        {
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                return;
            }

            // Quick-start: parameter is a "WxH" string (e.g. "16x16")
            if (parameter is string sizeStr && sizeStr.Contains('x'))
            {
                var parts = sizeStr.Split('x');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int qw) &&
                    int.TryParse(parts[1], out int qh) &&
                    qw > 0 && qh > 0)
                {
                    AddNewDocument(qw, qh);
                    return;
                }
            }

            var result = _dialogService.ShowNewCanvasDialog();
            if (result.HasValue)
                AddNewDocument(result.Value.Width, result.Value.Height);
        }

        // ── Open ──────────────────────────────────────────────────────────

        private void ExecuteOpen()
        {
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                return;
            }

            var path = _dialogService.ShowOpenFileDialog(FileFilter, "Open Sprite");
            if (path == null) return;

            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<SpriteState>(json);
                if (loaded?.Pixels == null) return;

                var doc = CreateDocument(loaded.Width, loaded.Height);
                doc.SpriteState.Pixels = (bool[])loaded.Pixels.Clone();
                doc.SpriteState.IsDisplayInverted = loaded.IsDisplayInverted;
                doc.IsDisplayInverted = loaded.IsDisplayInverted;
                doc.FilePath = path;
                doc.IsDirty = false;
                doc.RedrawGridFromMemory();

                // Restore last-used export settings (null-safe for old files)
                doc.ApplyExportSettings(loaded.ExportSettings);

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

            var path = _dialogService.ShowSaveFileDialog(FileFilter, "Save Sprite", ".hexprite");
            if (path != null)
                SaveToPath(ActiveDocument, path);
        }

        private void SaveToPath(MainViewModel doc, string path)
        {
            try
            {
                // Persist the current export settings alongside the pixel data
                doc.SpriteState.ExportSettings = doc.ExportSettings;

                string json = JsonSerializer.Serialize(doc.SpriteState,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                doc.FilePath = path;
                doc.IsDirty = false;

                // Offer a sensible default sprite name derived from the filename
                doc.UpdateSpriteNameFromFile();
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
                var saveChoice = _dialogService.ShowUnsavedChangesDialog(doc.Title.TrimStart('*'));

                if (saveChoice == null) return; // user cancelled

                if (saveChoice == true)
                {
                    if (doc.FilePath != null)
                    {
                        SaveToPath(doc, doc.FilePath);
                    }
                    else
                    {
                        var path = _dialogService.ShowSaveFileDialog(
                            "Hexprite Sprite (*.hexprite)|*.hexprite|All Files (*.*)|*.*",
                            "Save Sprite", ".hexprite");
                        if (path != null)
                            SaveToPath(doc, path);
                        else
                            return; // user cancelled the save dialog
                    }
                }
                // saveChoice == false → discard changes, continue closing
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

            var result = _dialogService.ShowResizeCanvasDialog(
                ActiveDocument.SpriteState.Width,
                ActiveDocument.SpriteState.Height);

            if (result.HasValue)
            {
                var (w, h, anchor) = result.Value;
                if (w == ActiveDocument.SpriteState.Width &&
                    h == ActiveDocument.SpriteState.Height) return;

                ActiveDocument.ResizeCanvas(w, h, anchor);
            }
        }

        // ── Import from Code ──────────────────────────────────────────────

        private void ExecuteImportFromCode()
        {
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                return;
            }

            var result = _dialogService.ShowImportFromCodeDialog();
            if (result == null) return;

            var (w, h, code, spriteName, isXbm) = result.Value;

            try
            {
                var doc = CreateDocument(w, h);
                if (isXbm)
                    _codeGen.ParseXbmToState(code, doc.SpriteState);
                else
                    _codeGen.ParseAdafruitGfxToState(code, doc.SpriteState);

                doc.RedrawGridFromMemory();

                // Set the detected sprite name so the export panel picks it up
                if (!string.IsNullOrWhiteSpace(spriteName))
                    doc.SpriteName = spriteName;

                doc.UpdateTextOutputs();

                OpenDocuments.Add(doc);
                ActiveDocument = doc;
                TabAdded?.Invoke(this, doc);
                RaiseActiveTabChanged();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"Error importing: {ex.Message}");
            }
        }

        // ── Help ──────────────────────────────────────────────────────────

        private void ExecuteOpenDocumentation()
        {
            Process.Start(new ProcessStartInfo("https://hexprite.com") { UseShellExecute = true });
        }

        private void ExecuteShowAbout()
        {
            _dialogService.ShowAboutDialog();
        }

        // ── Theme ─────────────────────────────────────────────────────────

        private void ExecuteSwitchTheme(string? themeName)
        {
            if (themeName != null)
                _themeService.ApplyTheme(themeName);
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

            var doc = new MainViewModel(
                _codeGen, _drawingService, history, selection,
                _clipboardService, _pixelClipboard, _dialogService);

            doc.InitializeGrid(width, height);
            return doc;
        }

        private void RaiseActiveTabChanged()
        {
            OnPropertyChanged(nameof(HasOpenDocument));
            ((RelayCommand)SaveCommand).NotifyCanExecuteChanged();
            ((RelayCommand)SaveAsCommand).NotifyCanExecuteChanged();
            ((RelayCommand)ResizeCanvasCommand).NotifyCanExecuteChanged();
            ((RelayCommand)CopyExportCodeMenuCommand).NotifyCanExecuteChanged();
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
