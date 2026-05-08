using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexprite.Core;
using Hexprite.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

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
        private static readonly Serilog.ILogger Logger = Log.ForContext<ShellViewModel>();
        private readonly ICodeGeneratorService _codeGen;
        private readonly IDrawingService _drawingService;
        private readonly IClipboardService _clipboardService;
        private readonly IPixelClipboardService _pixelClipboard;
        private readonly IDialogService _dialogService;
        private readonly IThemeService _themeService;
        private readonly IBugReportService _bugReportService;
        private readonly IUserFeedbackService _userFeedbackService;

        private const string FileFilter = "Hexprite Sprite (*.hexprite)|*.hexprite|JSON Files (*.json)|*.json|All Files (*.*)|*.*";

        public const int MaxTabs = 10;

        // ── Window layout (panel visibility) ──────────────────────────────
        // Important: compute this lazily to avoid static init order issues.
        private static string WindowLayoutSettingsFile => Path.Combine(UserSettingsDirectory, "window-layout.json");

        private bool _isLoadingWindowLayout;

        private bool _isToolSidebarVisible = true;
        public bool IsToolSidebarVisible
        {
            get => _isToolSidebarVisible;
            set => SetProperty(ref _isToolSidebarVisible, value);
        }

        private bool _isLayersPanelVisible = true;
        public bool IsLayersPanelVisible
        {
            get => _isLayersPanelVisible;
            set => SetProperty(ref _isLayersPanelVisible, value);
        }

        private bool _isRightSidebarVisible = true;
        public bool IsRightSidebarVisible
        {
            get => _isRightSidebarVisible;
            set => SetProperty(ref _isRightSidebarVisible, value);
        }

        private bool _isTimelineVisible = true;
        public bool IsTimelineVisible
        {
            get => _isTimelineVisible;
            set => SetProperty(ref _isTimelineVisible, value);
        }

        private bool _isStatusBarVisible = true;
        public bool IsStatusBarVisible
        {
            get => _isStatusBarVisible;
            set => SetProperty(ref _isStatusBarVisible, value);
        }

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
        public IRelayCommand OpenPrivacySettingsCommand { get; }
        public IRelayCommand ShowAboutCommand { get; }
        public IRelayCommand ReportBugCommand { get; }
        public IRelayCommand SendFeedbackCommand { get; }
        /// <summary>Opens the Import from Code dialog and creates a new tab.</summary>
        public IRelayCommand ImportFromCodeMenuCommand { get; }
        /// <summary>Imports a bitmap image into a new canvas tab.</summary>
        public IRelayCommand ImportBitmapMenuCommand { get; }
        /// <summary>Copies the active document's exported code to the clipboard.</summary>
        public IRelayCommand CopyExportCodeMenuCommand { get; }
        /// <summary>Switches the theme between Dark and Light.</summary>
        public IRelayCommand<string> SwitchThemeCommand { get; }
        public IRelayCommand RefreshThemeCommand { get; }

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

        /// <summary>
        /// Gets whether the current theme is Flipper. Used for menu radio-button binding.
        /// </summary>
        public bool IsFlipperTheme => _themeService.CurrentTheme == "Flipper";

        public ShellViewModel(
            ICodeGeneratorService codeGen,
            IDrawingService drawingService,
            IClipboardService clipboardService,
            IPixelClipboardService pixelClipboard,
            IDialogService dialogService,
            IThemeService themeService,
            IBugReportService bugReportService,
            IUserFeedbackService userFeedbackService)
        {
            _codeGen = codeGen;
            _drawingService = drawingService;
            _clipboardService = clipboardService;
            _pixelClipboard = pixelClipboard;
            _dialogService = dialogService;
            _themeService = themeService;
            _bugReportService = bugReportService;
            _userFeedbackService = userFeedbackService;

            LoadWindowLayoutSettings();
            PropertyChanged += OnShellPropertyChanged;

            NewCanvasCommand = new RelayCommand<object?>(ExecuteNewCanvas);
            OpenCommand = new RelayCommand(ExecuteOpen);
            SaveCommand = new RelayCommand(ExecuteSave, () => HasOpenDocument);
            SaveAsCommand = new RelayCommand(ExecuteSaveAs, () => HasOpenDocument);
            CloseTabCommand = new RelayCommand<MainViewModel>(ExecuteCloseTab);
            ResizeCanvasCommand = new RelayCommand(ExecuteResizeCanvas, () => HasOpenDocument);
            OpenDocumentationCommand = new RelayCommand(ExecuteOpenDocumentation);
            OpenPrivacySettingsCommand = new RelayCommand(ExecuteOpenPrivacySettings);
            ShowAboutCommand = new RelayCommand(ExecuteShowAbout);
            ReportBugCommand = new RelayCommand(ExecuteReportBug);
            SendFeedbackCommand = new RelayCommand(ExecuteSendFeedback);
            ImportFromCodeMenuCommand = new RelayCommand(ExecuteImportFromCode);
            ImportBitmapMenuCommand = new RelayCommand(ExecuteImportBitmap);
            CopyExportCodeMenuCommand = new RelayCommand(
                () => ActiveDocument?.CopyExportedCodeCommand.Execute(null),
                () => HasOpenDocument);
            SwitchThemeCommand = new RelayCommand<string>(ExecuteSwitchTheme);
            RefreshThemeCommand = new RelayCommand(ExecuteRefreshTheme);

            _themeService.ThemeChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDimTheme));
                OnPropertyChanged(nameof(IsFlipperTheme));

                // Re-read pixel colors from the new theme and redraw all open canvases
                foreach (var doc in OpenDocuments)
                    doc.RefreshCanvasColors();

                ThemeChanged?.Invoke(this, EventArgs.Empty);
            };

            // No auto-created document — the welcome screen is shown instead
        }

        private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isLoadingWindowLayout) return;

            if (e.PropertyName == nameof(IsToolSidebarVisible) ||
                e.PropertyName == nameof(IsLayersPanelVisible) ||
                e.PropertyName == nameof(IsRightSidebarVisible) ||
                e.PropertyName == nameof(IsTimelineVisible) ||
                e.PropertyName == nameof(IsStatusBarVisible))
            {
                SaveWindowLayoutSettings();
            }
        }

        private sealed class WindowLayoutSettings
        {
            public bool IsToolSidebarVisible { get; set; } = true;
            public bool IsLayersPanelVisible { get; set; } = true;
            public bool IsRightSidebarVisible { get; set; } = true;
            public bool IsTimelineVisible { get; set; } = true;
            public bool IsStatusBarVisible { get; set; } = true;
        }

        private void LoadWindowLayoutSettings()
        {
            _isLoadingWindowLayout = true;
            try
            {
                if (!File.Exists(WindowLayoutSettingsFile))
                    return;

                string json = File.ReadAllText(WindowLayoutSettingsFile);
                var saved = JsonSerializer.Deserialize<WindowLayoutSettings>(json);
                if (saved == null) return;

                IsToolSidebarVisible = saved.IsToolSidebarVisible;
                IsLayersPanelVisible = saved.IsLayersPanelVisible;
                IsRightSidebarVisible = saved.IsRightSidebarVisible;
                IsTimelineVisible = saved.IsTimelineVisible;
                IsStatusBarVisible = saved.IsStatusBarVisible;
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "ShellViewModel.LoadWindowLayoutSettings", new { WindowLayoutSettingsFile });
            }
            finally
            {
                _isLoadingWindowLayout = false;
            }
        }

        private void SaveWindowLayoutSettings()
        {
            try
            {
                Directory.CreateDirectory(UserSettingsDirectory);
                var payload = new WindowLayoutSettings
                {
                    IsToolSidebarVisible = IsToolSidebarVisible,
                    IsLayersPanelVisible = IsLayersPanelVisible,
                    IsRightSidebarVisible = IsRightSidebarVisible,
                    IsTimelineVisible = IsTimelineVisible,
                    IsStatusBarVisible = IsStatusBarVisible
                };
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(WindowLayoutSettingsFile, json);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "ShellViewModel.SaveWindowLayoutSettings", new { WindowLayoutSettingsFile });
            }
        }

        // ── New Canvas ────────────────────────────────────────────────────

        private void ExecuteNewCanvas(object? parameter)
        {
            using var operation = LoggingService.BeginOperation("Shell.NewCanvas", new { parameter });
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                Logger.Warning("New canvas blocked because max tabs reached. MaxTabs={MaxTabs}", MaxTabs);
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
                    Logger.Information("Created quick-start canvas {Width}x{Height}", qw, qh);
                    return;
                }
            }

            var result = _dialogService.ShowNewCanvasDialog();
            if (result.HasValue)
            {
                AddNewDocument(result.Value.Width, result.Value.Height);
                Logger.Information("Created canvas from dialog {Width}x{Height}", result.Value.Width, result.Value.Height);
            }
        }

        // ── Open ──────────────────────────────────────────────────────────

        private void ExecuteOpen()
        {
            using var operation = LoggingService.BeginOperation("Shell.OpenDocument");
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                Logger.Warning("Open blocked because max tabs reached. MaxTabs={MaxTabs}", MaxTabs);
                return;
            }

            var path = _dialogService.ShowOpenFileDialog(FileFilter, "Open Sprite");
            if (path == null) return;

            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<SpriteState>(json);
                if (loaded == null) return;
                loaded.NormalizeLayerState();

                var doc = CreateDocument(loaded.Width, loaded.Height);
                doc.SpriteState.Layers = loaded.Layers.ConvertAll(l => l.Clone());
                doc.SpriteState.ActiveLayerIndex = loaded.ActiveLayerIndex;
                doc.SpriteState.NormalizeLayerState();
                doc.ReloadLayersFromState();
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
                Logger.Information("Opened document at {Path} with size {Width}x{Height}", path, doc.SpriteState.Width, doc.SpriteState.Height);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "ShellViewModel.OpenFile", new { path });
                _dialogService.ShowMessage($"Error opening file: {ex.Message}");
            }
        }

        // ── Save / Save As ────────────────────────────────────────────────

        private void ExecuteSave()
        {
            if (ActiveDocument == null) return;
            using var operation = LoggingService.BeginOperation("Shell.Save", new { title = ActiveDocument.Title, hasFilePath = ActiveDocument.FilePath != null });

            if (ActiveDocument.FilePath != null)
                SaveToPath(ActiveDocument, ActiveDocument.FilePath);
            else
                ExecuteSaveAs();
        }

        private void ExecuteSaveAs()
        {
            if (ActiveDocument == null) return;
            using var operation = LoggingService.BeginOperation("Shell.SaveAs", new { title = ActiveDocument.Title });

            var path = _dialogService.ShowSaveFileDialog(FileFilter, "Save Sprite", ".hexprite");
            if (path != null)
                SaveToPath(ActiveDocument, path);
        }

        private void SaveToPath(MainViewModel doc, string path)
        {
            using var operation = LoggingService.BeginOperation("Shell.SaveToPath", new { path, width = doc.SpriteState.Width, height = doc.SpriteState.Height });
            try
            {
                // Persist the current export settings alongside the pixel data
                doc.SpriteState.NormalizeLayerState();
                doc.SpriteState.ExportSettings = doc.ExportSettings;

                string json = JsonSerializer.Serialize(doc.SpriteState,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                doc.FilePath = path;
                doc.IsDirty = false;

                // Offer a sensible default sprite name derived from the filename
                doc.UpdateSpriteNameFromFile();
                Logger.Information("Saved document to {Path}", path);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "ShellViewModel.SaveFile", new { path });
                _dialogService.ShowMessage($"Error saving: {ex.Message}");
            }
        }

        // ── Close Tab ─────────────────────────────────────────────────────

        private void ExecuteCloseTab(MainViewModel? doc)
        {
            doc ??= ActiveDocument;
            if (doc == null) return;
            using var operation = LoggingService.BeginOperation("Shell.CloseTab", new { title = doc.Title, isDirty = doc.IsDirty });

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
                Logger.Information("Closed tab {TabTitle}. No tabs remain open.", doc.Title);
            }
            else
            {
                ActiveDocument = OpenDocuments[Math.Min(idx, OpenDocuments.Count - 1)];
                RaiseActiveTabChanged();
                Logger.Information("Closed tab {TabTitle}. RemainingTabs={RemainingTabs}", doc.Title, OpenDocuments.Count);
            }
        }

        // ── Resize Canvas ─────────────────────────────────────────────────

        private void ExecuteResizeCanvas()
        {
            if (ActiveDocument == null) return;
            using var operation = LoggingService.BeginOperation(
                "Shell.ResizeCanvas",
                new { width = ActiveDocument.SpriteState.Width, height = ActiveDocument.SpriteState.Height });

            var result = _dialogService.ShowResizeCanvasDialog(
                ActiveDocument.SpriteState.Width,
                ActiveDocument.SpriteState.Height);

            if (result.HasValue)
            {
                var (w, h, anchor) = result.Value;
                if (w == ActiveDocument.SpriteState.Width &&
                    h == ActiveDocument.SpriteState.Height) return;

                ActiveDocument.ResizeCanvas(w, h, anchor);
                Logger.Information("Resized active canvas to {Width}x{Height} using anchor {Anchor}", w, h, anchor);
            }
        }

        // ── Import from Code ──────────────────────────────────────────────

        private void ExecuteImportFromCode()
        {
            using var operation = LoggingService.BeginOperation("Shell.ImportFromCode");
            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                Logger.Warning("Import blocked because max tabs reached. MaxTabs={MaxTabs}", MaxTabs);
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
                Logger.Information("Imported sprite from code. IsXbm={IsXbm} Size={Width}x{Height}", isXbm, w, h);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "ShellViewModel.ImportFromCode", new { w, h, isXbm });
                _dialogService.ShowMessage($"Error importing: {ex.Message}");
            }
        }

        // ── Import bitmap ─────────────────────────────────────────────────

        private const string BitmapImageFileFilter =
            "Image Files (*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff;*.gif)|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff;*.gif|All Files (*.*)|*.*";
        private static readonly string UserSettingsDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexprite");
        private static readonly string BitmapImportSettingsFile =
            Path.Combine(UserSettingsDirectory, "bitmap-import-settings.json");

        private static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    T result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return tcs.Task;
        }

        private async void ExecuteImportBitmap()
        {
            using var operation = LoggingService.BeginOperation("Shell.ImportBitmap");

            if (OpenDocuments.Count >= MaxTabs)
            {
                _dialogService.ShowMessage($"Maximum of {MaxTabs} tabs reached. Close a tab first.");
                Logger.Warning("Import blocked because max tabs reached. MaxTabs={MaxTabs}", MaxTabs);
                return;
            }

            string? selectedPath = null;

            try
            {
                selectedPath = _dialogService.ShowOpenFileDialog(BitmapImageFileFilter, "Import Bitmap");
                if (selectedPath == null) return;

                var importSettings = _dialogService.ShowImportBitmapDialog(
                    Path.GetFileName(selectedPath),
                    LoadBitmapImportSettings());
                if (importSettings == null) return;
                SaveBitmapImportSettings(importSettings);

                // WPF image decode pipelines are STA-affine; run conversion on a dedicated STA thread
                // so large imports don't block the UI thread.
                var (pixels, w, h, wasScaled) = await RunOnStaThreadAsync(() =>
                    BitmapToMonochromeConverter.ConvertTo1Bit(selectedPath, importSettings));

                var doc = CreateDocument(w, h, redrawImmediately: false);
                doc.SpriteState.SetActiveLayerPixels(pixels);

                doc.RedrawGridFromMemory();

                // Set name so export panel picks it up
                doc.SetSpriteNameWithoutGenerating(CodeGeneratorService.SanitiseName(
                    Path.GetFileNameWithoutExtension(selectedPath)));

                if (wasScaled)
                    doc.ShowStatus("Imported image (scaled to max canvas size)");

                // Generating export code for large imports can be expensive and may
                // cause UI stalls due to downstream syntax highlighting. Defer it
                // until the user explicitly clicks "Generate Code".
                doc.MarkCodeStale();

                OpenDocuments.Add(doc);
                ActiveDocument = doc;
                TabAdded?.Invoke(this, doc);
                RaiseActiveTabChanged();

                Logger.Information(
                    "Imported bitmap image. Scaled={WasScaled} Size={Width}x{Height} Dither={Dither} Threshold={Threshold}",
                    wasScaled, w, h, importSettings.DitheringAlgorithm, importSettings.Threshold);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "ShellViewModel.ImportBitmap", new { path = selectedPath });
                if (ex is NotSupportedException)
                    _dialogService.ShowMessage("This image format is not supported on your system.");
                else
                    _dialogService.ShowMessage($"Error importing image: {ex.Message}");
            }
        }

        private static BitmapImportSettings LoadBitmapImportSettings()
        {
            var defaults = new BitmapImportSettings
            {
                DitheringAlgorithm = BitmapDitheringAlgorithm.Atkinson,
                Threshold = 128,
                AlphaThreshold = 128,
                MaxDimension = SpriteState.MaxDimension
            };

            try
            {
                if (!File.Exists(BitmapImportSettingsFile))
                    return defaults;

                string json = File.ReadAllText(BitmapImportSettingsFile);
                BitmapImportSettings? saved = JsonSerializer.Deserialize<BitmapImportSettings>(json);
                if (saved == null)
                    return defaults;

                saved.Threshold = Math.Clamp(saved.Threshold, 0, 255);
                saved.AlphaThreshold = Math.Clamp(saved.AlphaThreshold, 0, 255);
                saved.MaxDimension = Math.Clamp(saved.MaxDimension, 1, SpriteState.MaxDimension);
                return saved;
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "ShellViewModel.LoadBitmapImportSettings", new { BitmapImportSettingsFile });
                return defaults;
            }
        }

        private static void SaveBitmapImportSettings(BitmapImportSettings settings)
        {
            try
            {
                Directory.CreateDirectory(UserSettingsDirectory);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(BitmapImportSettingsFile, json);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "ShellViewModel.SaveBitmapImportSettings", new { BitmapImportSettingsFile });
            }
        }

        // ── Help ──────────────────────────────────────────────────────────

        private void ExecuteOpenDocumentation()
        {
            using var operation = LoggingService.BeginOperation("Shell.OpenDocumentation", new { url = "https://hexprite.com" });
            try
            {
                Process.Start(new ProcessStartInfo("https://hexprite.com") { UseShellExecute = true });
                Logger.Information("Opened documentation URL");
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "ShellViewModel.OpenDocumentation", new { url = "https://hexprite.com" });
                _dialogService.ShowMessage("Could not open the documentation website.");
            }
        }

        private void ExecuteOpenPrivacySettings()
        {
            using var operation = LoggingService.BeginOperation("Shell.OpenPrivacySettings");
            bool saved = _dialogService.ShowPrivacySettingsDialog();
            Logger.Information("Privacy settings dialog closed. Saved={Saved}", saved);
        }

        private void ExecuteShowAbout()
        {
            Logger.Information("Opening About dialog");
            _dialogService.ShowAboutDialog();
        }

        private void ExecuteReportBug()
        {
            using var operation = LoggingService.BeginOperation("Shell.ReportBug");
            BugReportInput? input = _dialogService.ShowBugReportDialog();
            if (input == null)
            {
                Logger.Information("Bug report dialog canceled by user");
                return;
            }

            BugReportResult result = _bugReportService.SubmitReport(input);
            if (result.Success)
            {
                _dialogService.ShowBugReportSuccessDialog(result.Message, result.EventId);
                Logger.Information("Bug report submitted successfully. EventId={EventId}", result.EventId);
                return;
            }

            _dialogService.ShowMessage(result.Message);
            Log.Warning("User-facing bug report submission failed: {Message}", result.Message);
        }

        private void ExecuteSendFeedback()
        {
            using var operation = LoggingService.BeginOperation("Shell.SendFeedback");
            UserFeedbackInput? input = _dialogService.ShowUserFeedbackDialog();
            if (input == null)
            {
                Logger.Information("Feedback dialog canceled by user");
                return;
            }

            FeedbackSubmitResult result = _userFeedbackService.SubmitFeedback(input);
            if (result.Success)
            {
                _dialogService.ShowBugReportSuccessDialog(result.Message, result.EventId, "Feedback sent");
                Logger.Information("Feedback submitted successfully. EventId={EventId}", result.EventId);
                return;
            }

            _dialogService.ShowMessage(result.Message);
            Log.Warning("User-facing feedback submission failed: {Message}", result.Message);
        }

        // ── Theme ─────────────────────────────────────────────────────────

        private void ExecuteSwitchTheme(string? themeName)
        {
            if (themeName != null)
            {
                _themeService.ApplyTheme(themeName);
                Logger.Information("Theme switched to {ThemeName}", themeName);
            }
        }

        private void ExecuteRefreshTheme()
        {
            using var operation = LoggingService.BeginOperation("Shell.RefreshTheme", new { openDocuments = OpenDocuments.Count });
            // Force redraw of all open documents with current theme colors
            foreach (var doc in OpenDocuments)
                doc.RefreshCanvasColors();

            ThemeChanged?.Invoke(this, EventArgs.Empty);
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

        private MainViewModel CreateDocument(int width, int height, bool redrawImmediately = true)
        {
            var history = new HistoryService();
            var selection = new SelectionService();

            var doc = new MainViewModel(
                _codeGen, _drawingService, history, selection,
                _clipboardService, _pixelClipboard, _dialogService);

            doc.InitializeGrid(width, height, redrawImmediately);
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
