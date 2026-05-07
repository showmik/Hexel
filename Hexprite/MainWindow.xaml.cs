using Hexprite.Controllers;
using Hexprite.Core;
using Hexprite.Rendering;
using Hexprite.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Hexprite
{
    public partial class MainWindow : Window
    {
        // ── Dependencies ──────────────────────────────────────────────────
        private readonly ViewModels.ShellViewModel _shell;
        private ViewModels.MainViewModel? ViewModel => _shell.ActiveDocument;
        private ISelectionService? _selection => ViewModel?.SelectionService;

        // ── Extracted subsystems ───────────────────────────────────────────
        private ZoomPanController _zoomPan = null!;
        private SelectionOverlayRenderer _selectionOverlay = null!;
        private BrushCursorManager _brushCursor = null!;
        private CanvasElementProvider _canvasElements = null!;

        // ── Drag tracking (screen-space, belongs in the View) ─────────────
        private Point _dragStartMousePos;
        private int _dragStartFloatingX;
        private int _dragStartFloatingY;

        private double _layersColumnWidthBeforeDrag;
        private double _rightSidebarWidthBeforeDrag;
        private double _dragAccumulator;

        // ── Panel layout state (restore widths when re-showing) ───────────
        private double _lastLayersColumnWidth = 200;
        private double _lastRightSidebarColumnWidth = 260;
        private double _layersColumnMinWidth;
        private double _rightSidebarColumnMinWidth;

        // ── Last hovered pixel (avoids spamming ViewModel on same pixel) ──
        private int _lastHoveredX = -1;
        private int _lastHoveredY = -1;

        // ── Draw mode locked for the duration of a stroke ─────────────────
        private DrawMode _activeDrawMode = DrawMode.None;
        private bool _hasPendingToolMove;
        private PendingToolMove _pendingToolMove;
        private bool _toolMoveDispatchQueued;
        private DateTime _lastToolMoveDispatchUtc = DateTime.MinValue;
        private static readonly TimeSpan ToolMoveDispatchInterval = TimeSpan.FromMilliseconds(16);

        /// <summary>Floating selection resize via transform handles (preview coordinate deltas).</summary>
        private bool _activeTransformDrag;
        private int _transformDownPixelX;
        private int _transformDownPixelY;

        // ── Currently-subscribed document (for event unwiring) ─────────────
        private ViewModels.MainViewModel? _subscribedDoc;
        private const string DebugLogPath = @"H:\dev\Hexel\debug-4d8f4c.log";

        // ── Constructor ───────────────────────────────────────────────────

        public MainWindow(ViewModels.ShellViewModel shell)
        {
            InitializeComponent();
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            DataContext = _shell;

            // Hook Win32 for proper maximize behavior with custom chrome
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            };

            // Initialize extracted subsystems
            _zoomPan = new ZoomPanController(Canvas.ZoomSlider, () => Canvas.CanvasImage, () => _shell);

            _canvasElements = new CanvasElementProvider(
                () => Canvas.CanvasImage,
                () => Canvas.PixelGridContainer,
                () => Canvas.BrushCursorOverlay,
                () => Canvas.CrosshairH,
                () => Canvas.CrosshairV,
                () => Canvas.MarqueeOverlay,
                () => Canvas.LassoOverlay,
                () => Canvas.TransformHandlesLayer);

            _selectionOverlay = new SelectionOverlayRenderer(
                _canvasElements, () => ViewModel, () => _selection);
            _brushCursor = new BrushCursorManager(
                _canvasElements, () => ViewModel,
                () => GetPixelCoordinates(_brushCursor.LastCanvasMousePos, Canvas.CanvasImage.ActualWidth, Canvas.CanvasImage.ActualHeight));

            // Wire up events
            _shell.ActiveTabChanged += (_, _) => OnActiveTabChanged();
            _shell.ThemeChanged += (_, _) => _brushCursor.Refresh();
            _shell.PropertyChanged += Shell_PropertyChanged;

            // Zoom keyboard shortcuts
            CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom,
                (_, _) => _zoomPan.ApplyZoomCentered(ZoomPanController.ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom,
                (_, _) => _zoomPan.ApplyZoomCentered(1.0 / ZoomPanController.ZoomFactor)));
            CommandBindings.Add(new CommandBinding(NavigationCommands.Zoom,
                (_, _) => Canvas.ZoomSlider.Value = 1.0));

            // Wire up Canvas events
            Canvas.MainScrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
            Canvas.MainScrollViewer.PreviewMouseDown += ScrollViewer_PreviewMouseDown;
            Canvas.MainScrollViewer.PreviewMouseMove += ScrollViewer_PreviewMouseMove;
            Canvas.MainScrollViewer.PreviewMouseUp += ScrollViewer_PreviewMouseUp;
            Canvas.CanvasImage.MouseLeave += CanvasImage_MouseLeave;
            Loaded += MainWindow_Loaded;

            // Capture initial constraints so we can restore after collapsing.
            _layersColumnMinWidth = LayersColumn.MinWidth;
            _rightSidebarColumnMinWidth = RightSidebarColumn.MinWidth;

            ApplyPanelLayoutFromShell();
            OnActiveTabChanged();
        }

        private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.ShellViewModel.IsToolSidebarVisible) ||
                e.PropertyName == nameof(ViewModels.ShellViewModel.IsLayersPanelVisible) ||
                e.PropertyName == nameof(ViewModels.ShellViewModel.IsRightSidebarVisible) ||
                e.PropertyName == nameof(ViewModels.ShellViewModel.IsTimelineVisible) ||
                e.PropertyName == nameof(ViewModels.ShellViewModel.IsStatusBarVisible))
            {
                ApplyPanelLayoutFromShell();
            }
        }

        private void ApplyPanelLayoutFromShell()
        {
            // Tool sidebar (column 0 is Auto so Visibility collapse is sufficient)
            ToolSidebar.Visibility = _shell.IsToolSidebarVisible ? Visibility.Visible : Visibility.Collapsed;

            // Layers panel (column 3 + splitter column 2)
            if (_shell.IsLayersPanelVisible)
            {
                LayersPanel.Visibility = Visibility.Visible;
                LeftPanelSplitter.Visibility = Visibility.Visible;

                LayersColumn.MinWidth = _layersColumnMinWidth;
                double widthToRestore = _lastLayersColumnWidth;
                if (widthToRestore < LayersColumn.MinWidth) widthToRestore = LayersColumn.MinWidth;
                LayersColumn.Width = new GridLength(widthToRestore);
            }
            else
            {
                if (LayersColumn.ActualWidth > 0)
                    _lastLayersColumnWidth = LayersColumn.ActualWidth;

                LayersPanel.Visibility = Visibility.Collapsed;
                LeftPanelSplitter.Visibility = Visibility.Collapsed;

                LayersColumn.MinWidth = 0;
                LayersColumn.Width = new GridLength(0);
            }

            // Right sidebar (column 5 + splitter column 4)
            if (_shell.IsRightSidebarVisible)
            {
                RightSidebarPanel.Visibility = Visibility.Visible;
                RightPanelSplitter.Visibility = Visibility.Visible;

                RightSidebarColumn.MinWidth = _rightSidebarColumnMinWidth;
                double widthToRestore = _lastRightSidebarColumnWidth;
                if (widthToRestore < RightSidebarColumn.MinWidth) widthToRestore = RightSidebarColumn.MinWidth;
                RightSidebarColumn.Width = new GridLength(widthToRestore);
            }
            else
            {
                if (RightSidebarColumn.ActualWidth > 0)
                    _lastRightSidebarColumnWidth = RightSidebarColumn.ActualWidth;

                RightSidebarPanel.Visibility = Visibility.Collapsed;
                RightPanelSplitter.Visibility = Visibility.Collapsed;

                RightSidebarColumn.MinWidth = 0;
                RightSidebarColumn.Width = new GridLength(0);
            }

            // Timeline / Status bar (simple visibility)
            // Use FindName so this stays resilient even if XAML field generation lags in the IDE.
            if (FindName("TimelinePanel") is UIElement timeline)
                timeline.Visibility = _shell.IsTimelineVisible ? Visibility.Visible : Visibility.Collapsed;

            if (FindName("StatusBarPanel") is UIElement statusBar)
                statusBar.Visibility = _shell.IsStatusBarVisible ? Visibility.Visible : Visibility.Collapsed;

            // Ensure Canvas remains fluidly sized.
            if (!CanvasColumn.Width.IsStar)
                CanvasColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            #region agent log
            LogPanelWidths("initial-layout", "H1");
            #endregion
        }

        private void PanelSplitter_DragStarted(object sender, DragStartedEventArgs e)
        {
            string splitterName = (sender as FrameworkElement)?.Name ?? "unknown";

            if (sender == RightPanelSplitter)
            {
                // Capture the starting widths and reset our manual drag accumulator
                _layersColumnWidthBeforeDrag = LayersColumn.ActualWidth;
                _rightSidebarWidthBeforeDrag = RightSidebarColumn.ActualWidth;
                _dragAccumulator = 0;
            }

            #region agent log
            LogPanelWidths($"splitter-start:{splitterName}", "H2");
            #endregion
        }

        private void PanelSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            string splitterName = (sender as FrameworkElement)?.Name ?? "unknown";

            if (sender == RightPanelSplitter)
            {
                // Track total horizontal mouse movement ourselves
                _dragAccumulator += e.HorizontalChange;

                // Dragging the splitter left results in a negative HorizontalChange.
                // Subtracting that negative value INCREASES the Right Sidebar's width.
                double newRightWidth = _rightSidebarWidthBeforeDrag - _dragAccumulator;

                // Clamp the new width to ensure it doesn't violate your XAML constraints
                newRightWidth = Math.Max(RightSidebarColumn.MinWidth, Math.Min(newRightWidth, RightSidebarColumn.MaxWidth));

                // Manually apply widths to force the correct layout behaviors
                RightSidebarColumn.Width = new GridLength(newRightWidth);
                LayersColumn.Width = new GridLength(_layersColumnWidthBeforeDrag);
            }

            // FIX: Restore Canvas star sizing so it fluidly absorbs space adjustments
            if (!CanvasColumn.Width.IsStar)
            {
                CanvasColumn.Width = new GridLength(1, GridUnitType.Star);
            }

            #region agent log
            LogPanelWidths($"splitter-delta:{splitterName}", "H3");
            #endregion
        }

        private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            string splitterName = (sender as FrameworkElement)?.Name ?? "unknown";

            // Guarantee final layout state for the Layers panel
            if (sender == RightPanelSplitter)
            {
                LayersColumn.Width = new GridLength(_layersColumnWidthBeforeDrag);
            }

            // Ensure Star sizing is securely restored after drag completion
            if (!CanvasColumn.Width.IsStar)
            {
                CanvasColumn.Width = new GridLength(1, GridUnitType.Star);
            }

            #region agent log
            LogPanelWidths($"splitter-complete:{splitterName}", "H4");
            #endregion
        }

        private void LogPanelWidths(string message, string hypothesisId)
        {
            try
            {
                var payload = new
                {
                    sessionId = "4d8f4c",
                    runId = "pre-fix",
                    hypothesisId,
                    location = "MainWindow.xaml.cs:panel-splitter",
                    message,
                    data = new
                    {
                        canvasColumnWidth = CanvasColumn.ActualWidth,
                        layersColumnWidth = LayersColumn.ActualWidth,
                        rightSidebarColumnWidth = RightSidebarColumn.ActualWidth,
                        layersPanelWidth = LayersPanel.ActualWidth,
                        rightSidebarPanelWidth = RightSidebarPanel.ActualWidth,
                        windowWidth = ActualWidth
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                #region agent log
                string line = JsonSerializer.Serialize(payload) + Environment.NewLine;
                File.AppendAllText(DebugLogPath, line);
                #endregion
            }
            catch
            {
                // Intentionally swallowed for debug-only telemetry.
            }
        }

        private void OnActiveTabChanged()
        {
            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored -= OnHistoryRestored;
                _subscribedDoc.ToolChanged -= OnToolChanged;
                _subscribedDoc.PropertyChanged -= OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged -= OnSelectionChanged;
            }

            _subscribedDoc = ViewModel;

            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored += OnHistoryRestored;
                _subscribedDoc.ToolChanged += OnToolChanged;
                _subscribedDoc.PropertyChanged += OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged += OnSelectionChanged;
            }

            // ── Reset stale view-layer state ─────────────────────────────
            _activeDrawMode = DrawMode.None;
            _lastHoveredX = -1;
            _lastHoveredY = -1;
            ReleaseDragCapture();
            if (Mouse.Captured != null) Mouse.Capture(null);

            // ── Sync visuals to the new document ─────────────────────────
            _selectionOverlay.Update();
            _brushCursor.Hide();
            SyncBrushShapeRadioButtons();

            // Sync tool sidebar radio buttons without triggering SelectToolCommand
            if (_subscribedDoc != null)
                ToolSidebar.SyncToTool(_subscribedDoc.CurrentTool);

            CenterScrollViewer();
        }

        private void CenterScrollViewer()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Canvas.MainScrollViewer.UpdateLayout();
                // If we are at the very top-left (initial state), center it
                if (Canvas.MainScrollViewer.HorizontalOffset < 10 && Canvas.MainScrollViewer.VerticalOffset < 10)
                {
                    double cx = Canvas.MainScrollViewer.ExtentWidth / 2;
                    double cy = Canvas.MainScrollViewer.ExtentHeight / 2;
                    double vx = Canvas.MainScrollViewer.ViewportWidth / 2;
                    double vy = Canvas.MainScrollViewer.ViewportHeight / 2;
                    Canvas.MainScrollViewer.ScrollToHorizontalOffset(cx - vx);
                    Canvas.MainScrollViewer.ScrollToVerticalOffset(cy - vy);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SyncBrushShapeRadioButtons()
        {
            if (ViewModel == null) return;
            switch (ViewModel.BrushShape)
            {
                case Core.BrushShape.Circle: if (Canvas.RbBrushCircle != null) Canvas.RbBrushCircle.IsChecked = true; break;
                case Core.BrushShape.Square: if (Canvas.RbBrushSquare != null) Canvas.RbBrushSquare.IsChecked = true; break;
                case Core.BrushShape.Line: if (Canvas.RbBrushLine != null) Canvas.RbBrushLine.IsChecked = true; break;
            }
        }

        private void OnHistoryRestored(object? s, EventArgs e)
        {
            _selectionOverlay.Clear();
            ReleaseDragCapture();
        }

        private void OnToolChanged(object? s, EventArgs e)
        {
            if (ViewModel != null && _selection != null)
            {
                if (_selection.IsSelecting)
                {
                    _selection.Cancel();
                }

                if (_selection.IsFloating)
                {
                    ViewModel.SaveStateForUndo();
                    _selection.CommitSelection(ViewModel.SpriteState);
                    ViewModel.RedrawGridFromMemory();
                    ViewModel.MarkCodeStale();
                }

                ViewModel.CancelInProgressDrawing();
            }

            // Pure View concern: hide brush cursor when tool isn't Pencil
            if (ViewModel != null && ViewModel.CurrentTool != ToolMode.Pencil)
                _brushCursor.Hide();

            _activeDrawMode = DrawMode.None;
            // Do NOT clear the selection overlay here — selection persists across tool switches.
            // Only refresh it so it stays visually current.
            _selectionOverlay.Update();

            // Sync the sidebar radio buttons so shortcut-triggered tool switches
            // are reflected visually (SyncToTool suppresses SelectToolCommand re-entry).
            if (ViewModel != null)
                ToolSidebar.SyncToTool(ViewModel.CurrentTool);
        }

        private void OnSelectionChanged(object? s, EventArgs e) => _selectionOverlay.Update();
        private void OnDocPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.BrushSize) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushShape) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushAngle))
                _brushCursor.Refresh();

            if (e.PropertyName == nameof(ViewModels.MainViewModel.ZoomLevel))
                _selectionOverlay.Update();
        }

        // ── Tab bar event handlers ────────────────────────────────────────

        private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.MainViewModel doc)
            {
                if (doc == _shell.ActiveDocument) return; // already active
                _shell.ActiveDocument = doc;
                OnActiveTabChanged();
            }
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ViewModels.MainViewModel doc)
                _shell.CloseTabCommand.Execute(doc);
        }

        // ── Global overrides ──────────────────────────────────────────────

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (ViewModel == null || _selection == null) return;
            if (_selection.IsDragging && e.LeftButton == MouseButtonState.Pressed)
                UpdateDragPosition(e.GetPosition(Canvas.PixelGridContainer));
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);
            if (ViewModel == null || _selection == null) return;
            FlushPendingToolMove();

            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            }
            else if (ViewModel.CurrentTool == ToolMode.Pencil)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false, false);
            }

            _activeDrawMode = DrawMode.None;

            if (e.ChangedButton == MouseButton.Left)
            {
                bool wasDragging = _selection.IsDragging;

                if (_activeTransformDrag)
                {
                    ViewModel.CommitSelectionTransformIfActive();
                    _activeTransformDrag = false;
                }
                if ((ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.Marquee || ViewModel.CurrentTool == ToolMode.MagicWand) && _selection.IsSelecting)
                    ViewModel.ProcessSelectionInput(-1, -1, ToolAction.Up, false, false);
                ReleaseDragCapture();
                _selection.EndDrag();

                if (wasDragging)
                    ViewModel.UpdatePreviewSimulation();
            }

            if (Mouse.Captured == Canvas.CanvasImage)
                Mouse.Capture(null);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (ViewModel == null || _selection == null) return;

            if (e.Key == Key.LeftShift || e.Key == Key.RightShift || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(ViewModel.CursorX, ViewModel.CursorY, ToolAction.Move, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Up) { ViewModel.ShiftGrid(0, -1); e.Handled = true; }
                else if (e.Key == Key.Down) { ViewModel.ShiftGrid(0, 1); e.Handled = true; }
                else if (e.Key == Key.Left) { ViewModel.ShiftGrid(-1, 0); e.Handled = true; }
                else if (e.Key == Key.Right) { ViewModel.ShiftGrid(1, 0); e.Handled = true; }
                // FIX: Removed redundant Ctrl+D interception. Deselection UI cleanup is 
                // strictly handled by the SelectionChanged event triggered by the KeyBinding command.
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Keyboard.FocusedElement is TextBox) return;
                switch (e.Key)
                {
                    case Key.Delete:
                    case Key.Back:
                        if (ViewModel.DeleteSelectionCommand.CanExecute(null))
                        {
                            ViewModel.DeleteSelectionCommand.Execute(null);
                            _selectionOverlay.Clear();
                            e.Handled = true;
                        }
                        break;
                    case Key.Escape:
                        if (_selection.IsTransforming)
                        {
                            ViewModel.CancelSelectionTransformIfActive();
                            _activeTransformDrag = false;
                            _selectionOverlay.Update();
                            e.Handled = true;
                        }
                        else
                        {
                            ViewModel.DeselectCommand.Execute(null);
                            _selectionOverlay.Clear();
                            e.Handled = true;
                        }
                        break;
                    case Key.P: ViewModel.SelectToolCommand.Execute("Pencil"); e.Handled = true; break;
                    case Key.L: ViewModel.SelectToolCommand.Execute("Line"); e.Handled = true; break;
                    case Key.R: ViewModel.SelectToolCommand.Execute("Rectangle"); e.Handled = true; break;
                    case Key.E: ViewModel.SelectToolCommand.Execute("Ellipse"); e.Handled = true; break;
                    case Key.F: ViewModel.SelectToolCommand.Execute("Fill"); e.Handled = true; break;
                    case Key.M: ViewModel.SelectToolCommand.Execute("Marquee"); e.Handled = true; break;
                    case Key.S: ViewModel.SelectToolCommand.Execute("Lasso"); e.Handled = true; break;
                    case Key.W: ViewModel.SelectToolCommand.Execute("MagicWand"); e.Handled = true; break;
                    case Key.OemOpenBrackets: ViewModel.BrushSize--; e.Handled = true; break;
                    case Key.OemCloseBrackets: ViewModel.BrushSize++; e.Handled = true; break;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (Keyboard.FocusedElement is TextBox) return;
                switch (e.Key)
                {
                    case Key.R: ViewModel.SelectToolCommand.Execute("FilledRectangle"); e.Handled = true; break;
                    case Key.E: ViewModel.SelectToolCommand.Execute("FilledEllipse"); e.Handled = true; break;
                }
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (ViewModel == null || _selection == null) return;

            if (e.Key == Key.LeftShift || e.Key == Key.RightShift || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(ViewModel.CursorX, ViewModel.CursorY, ToolAction.Move, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }
        }

        // ── Drag position update ──────────────────────────────────────────

        private void UpdateDragPosition(Point currentPos)
        {
            double gw = Canvas.PixelGridContainer.ActualWidth > 0 ? Canvas.PixelGridContainer.ActualWidth : 400.0;
            double gh = Canvas.PixelGridContainer.ActualHeight > 0 ? Canvas.PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel!.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            int newX = _dragStartFloatingX + (int)Math.Round((currentPos.X - _dragStartMousePos.X) / cw);
            int newY = _dragStartFloatingY + (int)Math.Round((currentPos.Y - _dragStartMousePos.Y) / ch);

            if (newX != _selection!.FloatingX || newY != _selection.FloatingY)
            {
                int oldX = _selection.FloatingX;
                int oldY = _selection.FloatingY;
                int fw = _selection.FloatingWidth;
                int fh = _selection.FloatingHeight;

                _selection.MoveFloatingTo(newX, newY);

                int minX = Math.Min(oldX, newX);
                int minY = Math.Min(oldY, newY);
                int maxX = Math.Max(oldX + fw - 1, newX + fw - 1);
                int maxY = Math.Max(oldY + fh - 1, newY + fh - 1);

                const int padding = 1;
                ViewModel.RedrawRegion(minX - padding, minY - padding, maxX + padding, maxY + padding, updatePreviewSimulation: false);
            }
        }

        private void ReleaseDragCapture()
        {
            if (Mouse.Captured == Canvas.PixelGridContainer)
                Mouse.Capture(null);
        }

        // ── Zoom & pan (delegated to ZoomPanController) ───────────────────

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => _zoomPan.ZoomIn();
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => _zoomPan.ZoomOut();
        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => _zoomPan.ZoomReset();

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
            => _zoomPan.HandleMouseWheel((ScrollViewer)sender, e, this);

        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var sv = (ScrollViewer)sender;

            if (_zoomPan.TryStartPan(sv, e, this))
            {
                e.Handled = true;
                return;
            }

            // Ignore scrollbar clicks
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = VisualTreeHelper.GetParent(dep);
                while (parent != null)
                {
                    if (parent is System.Windows.Controls.Primitives.ScrollBar) return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            if (ViewModel == null || _selection == null) return;

            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not Image)
            {
                // Only auto-commit a floating selection when clicking outside the canvas.
                // Non-floating selections persist until explicitly deselected (Ctrl+D).
                if (_selection.HasActiveSelection && _selection.IsFloating)
                {
                    // Don't deselect if the click is on a transform handle (handle may be outside canvas).
                    var image2 = Canvas.CanvasImage;
                    var imgPos2 = e.GetPosition(image2);
                    var hitHandle = ViewModel.HitTestSelectionHandle(imgPos2.X, imgPos2.Y, image2.ActualWidth, image2.ActualHeight);
                    if (hitHandle == TransformHandle.None)
                    {
                        ViewModel.DeselectCommand.Execute(null);
                        _selectionOverlay.Clear();
                    }
                }
            }

            var image = Canvas.CanvasImage;
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
            {
                image.CaptureMouse();
                var imgPos = e.GetPosition(image);
                var (x, y) = GetPixelCoordinates(imgPos, image.ActualWidth, image.ActualHeight);

                if (ViewModel.CurrentTool == ToolMode.Marquee ||
                    ViewModel.CurrentTool == ToolMode.Lasso ||
                    ViewModel.CurrentTool == ToolMode.MagicWand)
                {
                    if (e.ChangedButton == MouseButton.Left)
                    {
                        var handle = ViewModel.HitTestSelectionHandle(imgPos.X, imgPos.Y, image.ActualWidth, image.ActualHeight);
                        if (handle != TransformHandle.None && ViewModel.TryBeginSelectionTransform(handle))
                        {
                            _activeTransformDrag = true;
                            _transformDownPixelX = x;
                            _transformDownPixelY = y;
                            Mouse.Capture(Canvas.PixelGridContainer);
                            return;
                        }
                    }

                    // Check if we should start a drag instead
                    bool isReplace = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                    if (isReplace && _selection.HasActiveSelection && _selection.IsPixelInSelection(x, y))
                    {
                        if (ViewModel.TryBeginSelectionDrag(x, y))
                        {
                            _dragStartMousePos = e.GetPosition(Canvas.PixelGridContainer);
                            _dragStartFloatingX = _selection.FloatingX;
                            _dragStartFloatingY = _selection.FloatingY;
                            Mouse.Capture(Canvas.PixelGridContainer);
                            return;
                        }
                    }

                    ViewModel.ProcessSelectionInput(x, y, ToolAction.Down,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
                        e.ChangedButton == MouseButton.Right);
                    return;
                }

                _activeDrawMode = e.ChangedButton == MouseButton.Left ? DrawMode.Draw
                               : e.ChangedButton == MouseButton.Right ? DrawMode.Erase
                               : DrawMode.None;

                if (_activeDrawMode != DrawMode.None)
                {
                    // Update brush cursor immediately on press so the overlay
                    // doesn't lag until the first PreviewMouseMove event fires.
                    _brushCursor.LastCanvasMousePos = imgPos;
                    _brushCursor.IsMouseOverCanvas = true;
                    _brushCursor.Update(x, y, imgPos, image.ActualWidth, image.ActualHeight);

                    _lastHoveredX = x;
                    _lastHoveredY = y;
                    ViewModel.ProcessToolInput(x, y, ToolAction.Down, _activeDrawMode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_zoomPan.IsPanning)
            {
                _zoomPan.HandlePanMove((ScrollViewer)sender, e, this);
                _brushCursor.Hide();
                return;
            }

            var image = Canvas.CanvasImage;
            var pos = e.GetPosition(image);
            var (x, y) = GetPixelCoordinates(pos, image.ActualWidth, image.ActualHeight);

            _brushCursor.LastCanvasMousePos = pos;
            _brushCursor.IsMouseOverCanvas = pos.X >= 0 && pos.X <= image.ActualWidth &&
                                             pos.Y >= 0 && pos.Y <= image.ActualHeight;

            if (ViewModel == null || _selection == null) return;

            // Update brush cursor every mouse move for smooth visual tracking
            _brushCursor.Update(x, y, pos, image.ActualWidth, image.ActualHeight);

            if (_activeTransformDrag && e.LeftButton == MouseButtonState.Pressed)
            {
                ViewModel.UpdateSelectionTransform(
                    x - _transformDownPixelX,
                    y - _transformDownPixelY,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                _selectionOverlay.Update();
                return;
            }

            if (!_activeTransformDrag && _selection.IsFloating &&
                (ViewModel.CurrentTool == ToolMode.Marquee ||
                 ViewModel.CurrentTool == ToolMode.Lasso ||
                 ViewModel.CurrentTool == ToolMode.MagicWand))
            {
                var h = ViewModel.HitTestSelectionHandle(pos.X, pos.Y, image.ActualWidth, image.ActualHeight);
                image.Cursor = CursorForTransformHandle(h);
            }
            else if (!_zoomPan.IsPanning)
            {
                image.Cursor = Cursors.Arrow;
            }

            // Only process drawing/selection when the pixel coordinate actually changes
            if (x != _lastHoveredX || y != _lastHoveredY)
            {
                _lastHoveredX = x;
                _lastHoveredY = y;
                ViewModel.CursorX = x;
                ViewModel.CursorY = y;

                if (!_activeTransformDrag &&
                    (ViewModel.CurrentTool == ToolMode.Marquee ||
                     ViewModel.CurrentTool == ToolMode.Lasso ||
                     ViewModel.CurrentTool == ToolMode.MagicWand)
                    && e.LeftButton == MouseButtonState.Pressed)
                {
                    ViewModel.ProcessSelectionInput(x, y, ToolAction.Move,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                    return;
                }

                var mode = _activeDrawMode;
                if (mode != DrawMode.None || ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    EnqueueToolMove(x, y, mode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _zoomPan.TryEndPan((ScrollViewer)sender, e);
        }

        private void EnqueueToolMove(int x, int y, DrawMode mode, bool isShiftDown, bool isAltDown)
        {
            if (ViewModel == null) return;

            _pendingToolMove = new PendingToolMove(x, y, mode, isShiftDown, isAltDown);
            _hasPendingToolMove = true;

            if (_toolMoveDispatchQueued)
                return;

            _toolMoveDispatchQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DispatchPendingToolMove));
        }

        private void DispatchPendingToolMove()
        {
            _toolMoveDispatchQueued = false;
            if (!_hasPendingToolMove || ViewModel == null)
                return;

            var now = DateTime.UtcNow;
            var elapsed = now - _lastToolMoveDispatchUtc;
            if (elapsed < ToolMoveDispatchInterval)
            {
                _toolMoveDispatchQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DispatchPendingToolMove));
                return;
            }

            var pending = _pendingToolMove;
            _hasPendingToolMove = false;
            _lastToolMoveDispatchUtc = now;
            ViewModel.ProcessToolInput(pending.X, pending.Y, ToolAction.Move, pending.Mode, pending.IsShiftDown, pending.IsAltDown);

            if (_hasPendingToolMove && !_toolMoveDispatchQueued)
            {
                _toolMoveDispatchQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DispatchPendingToolMove));
            }
        }

        private void FlushPendingToolMove()
        {
            if (!_hasPendingToolMove || ViewModel == null)
                return;

            var pending = _pendingToolMove;
            _hasPendingToolMove = false;
            _lastToolMoveDispatchUtc = DateTime.UtcNow;
            ViewModel.ProcessToolInput(pending.X, pending.Y, ToolAction.Move, pending.Mode, pending.IsShiftDown, pending.IsAltDown);
        }

        private readonly record struct PendingToolMove(int X, int Y, DrawMode Mode, bool IsShiftDown, bool IsAltDown);

        // ── Utility ───────────────────────────────────────────────────────

        private static Cursor CursorForTransformHandle(TransformHandle h)
        {
            return h switch
            {
                TransformHandle.NW or TransformHandle.SE => Cursors.SizeNWSE,
                TransformHandle.NE or TransformHandle.SW => Cursors.SizeNESW,
                TransformHandle.N or TransformHandle.S => Cursors.SizeNS,
                TransformHandle.E or TransformHandle.W => Cursors.SizeWE,
                _ => Cursors.Arrow,
            };
        }

        private (int x, int y) GetPixelCoordinates(Point pos, double actualWidth, double actualHeight)
        {
            if (ViewModel == null) return (0, 0);

            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;
            if (actualWidth == 0 || actualHeight == 0) return (0, 0);

            int x = (int)Math.Floor(pos.X / actualWidth * w);
            int y = (int)Math.Floor(pos.Y / actualHeight * h);

            if (pos.X >= 0 && pos.X <= actualWidth) x = Math.Clamp(x, 0, w - 1);
            if (pos.Y >= 0 && pos.Y <= actualHeight) y = Math.Clamp(y, 0, h - 1);

            return (x, y);
        }

        // ── Brush cursor (delegated) ──────────────────────────────────────

        private void CanvasImage_MouseLeave(object sender, MouseEventArgs e) => _brushCursor.OnMouseLeave();

        // ── Custom chrome caption buttons ─────────────────────────────────

        private void CaptionMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CaptionMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (MaximizeIcon != null)
            {
                MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            }
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e)
            => Close();

        // ── Win32 interop for proper maximize with WindowStyle=None ────────

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    GetMonitorInfo(monitor, ref mi);
                    var work = mi.rcWork;
                    var mon = mi.rcMonitor;
                    mmi.ptMaxPosition = new POINT { x = work.left - mon.left, y = work.top - mon.top };
                    mmi.ptMaxSize = new POINT { x = work.right - work.left, y = work.bottom - work.top };
                }
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
