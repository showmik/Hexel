using Hexel.Controllers;
using Hexel.Core;
using Hexel.Rendering;
using Hexel.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hexel
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

        // ── Drag tracking (screen-space, belongs in the View) ─────────────
        private Point _dragStartMousePos;
        private int _dragStartFloatingX;
        private int _dragStartFloatingY;

        // ── Last hovered pixel (avoids spamming ViewModel on same pixel) ──
        private int _lastHoveredX = -1;
        private int _lastHoveredY = -1;

        // ── Draw mode locked for the duration of a stroke ─────────────────
        private DrawMode _activeDrawMode = DrawMode.None;

        // ── Status label fade timer ───────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _statusTimer;

        // ── Currently-subscribed document (for event unwiring) ─────────────
        private ViewModels.MainViewModel? _subscribedDoc;

        // ── Constructor ───────────────────────────────────────────────────

        public MainWindow(ViewModels.ShellViewModel shell)
        {
            InitializeComponent();
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            DataContext = _shell;

            // Initialize extracted subsystems
            _zoomPan = new ZoomPanController(Canvas.ZoomSlider, () => Canvas.CanvasImage, () => _shell);
            _selectionOverlay = new SelectionOverlayRenderer(
                () => Canvas.MarqueeOverlay, () => Canvas.LassoOverlay,
                () => Canvas.PixelGridContainer, () => ViewModel, () => _selection);
            _brushCursor = new BrushCursorManager(
                () => Canvas.BrushCursorOverlay, () => Canvas.CrosshairH, () => Canvas.CrosshairV,
                () => Canvas.CanvasImage, () => Canvas.PixelGridContainer, () => ViewModel,
                () => GetPixelCoordinates(_brushCursor.LastCanvasMousePos, Canvas.CanvasImage.ActualWidth, Canvas.CanvasImage.ActualHeight));

            // Wire up tab change
            _shell.ActiveTabChanged += (_, _) => OnActiveTabChanged();

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


            OnActiveTabChanged();
        }

        private void OnActiveTabChanged()
        {
            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored -= OnHistoryRestored;
                _subscribedDoc.CopyHexExecuted -= OnCopyHexExecuted;
                _subscribedDoc.PropertyChanged -= OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged -= OnSelectionChanged;
            }

            _subscribedDoc = ViewModel;

            if (_subscribedDoc != null)
            {
                _subscribedDoc.HistoryRestored += OnHistoryRestored;
                _subscribedDoc.CopyHexExecuted += OnCopyHexExecuted;
                _subscribedDoc.PropertyChanged += OnDocPropertyChanged;
                if (_subscribedDoc.SelectionService != null)
                    _subscribedDoc.SelectionService.SelectionChanged += OnSelectionChanged;
            }

            _selectionOverlay.Clear();
            _brushCursor.Hide();
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


        private void OnHistoryRestored(object? s, EventArgs e)
        {
            _selectionOverlay.Clear();
            ReleaseDragCapture();
        }
        private void OnCopyHexExecuted(object? s, EventArgs e) => ShowStatus("Copied to clipboard");
        private void OnSelectionChanged(object? s, EventArgs e) => _selectionOverlay.Update();
        private void OnDocPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.BrushSize) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushShape) ||
                e.PropertyName == nameof(ViewModels.MainViewModel.BrushAngle))
                _brushCursor.Refresh();

            if (e.PropertyName == nameof(ViewModels.MainViewModel.CurrentTool))
                OnCurrentToolChanged();
        }

        /// <summary>
        /// Reacts to tool changes originating from any source (keyboard shortcut, data binding,
        /// or programmatic). Handles view-side side-effects: draw mode reset, selection commit,
        /// and brush cursor visibility. CancelInProgressDrawing() is already called by the VM.
        /// </summary>
        private void OnCurrentToolChanged()
        {
            if (ViewModel == null) return;
            _activeDrawMode = DrawMode.None;

            var tool = ViewModel.CurrentTool;
            if (tool != ToolMode.Marquee && tool != ToolMode.Lasso)
                CommitCurrentSelection();

            if (tool != ToolMode.Pencil)
                _brushCursor.Hide();
        }

        // ── Tab bar event handlers ────────────────────────────────────────

        private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.MainViewModel doc)
                _shell.ActiveDocument = doc;
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ViewModels.MainViewModel doc)
                _shell.CloseTabCommand.Execute(doc);
        }

        // ── Tool selection ────────────────────────────────────────────────


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
                if ((ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.Marquee || ViewModel.CurrentTool == ToolMode.MagicWand) && _selection.IsSelecting)
                    _selection.FinalizeSelection();
                ReleaseDragCapture();
                _selection.EndDrag();
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
                    case Key.Escape: CommitCurrentSelection(); e.Handled = true; break;
                    case Key.P: ViewModel.CurrentTool = ToolMode.Pencil; e.Handled = true; break;
                    case Key.L: ViewModel.CurrentTool = ToolMode.Line; e.Handled = true; break;
                    case Key.R: ViewModel.CurrentTool = ToolMode.Rectangle; e.Handled = true; break;
                    case Key.E: ViewModel.CurrentTool = ToolMode.Ellipse; e.Handled = true; break;
                    case Key.F: ViewModel.CurrentTool = ToolMode.Fill; e.Handled = true; break;
                    case Key.M: ViewModel.CurrentTool = ToolMode.Marquee; e.Handled = true; break;
                    case Key.S: ViewModel.CurrentTool = ToolMode.Lasso; e.Handled = true; break;
                    case Key.W: ViewModel.CurrentTool = ToolMode.MagicWand; e.Handled = true; break;
                    case Key.OemOpenBrackets: ViewModel.BrushSize--; e.Handled = true; break;
                    case Key.OemCloseBrackets: ViewModel.BrushSize++; e.Handled = true; break;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (Keyboard.FocusedElement is TextBox) return;
                switch (e.Key)
                {
                    case Key.R: ViewModel.CurrentTool = ToolMode.FilledRectangle; e.Handled = true; break;
                    case Key.E: ViewModel.CurrentTool = ToolMode.FilledEllipse; e.Handled = true; break;
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

        // ── Selection tool handling ───────────────────────────────────────

        private int _selectionAnchorX = -1;
        private int _selectionAnchorY = -1;

        private void HandleSelectionDown(MouseButtonEventArgs e, int x, int y)
        {
            Hexel.Core.SelectionMode mode = Hexel.Core.SelectionMode.Replace;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

            if (shift && alt) mode = Hexel.Core.SelectionMode.Intersect;
            else if (shift) mode = Hexel.Core.SelectionMode.Add;
            else if (alt) mode = Hexel.Core.SelectionMode.Subtract;

            if (_selection.HasActiveSelection && _selection.IsPixelInSelection(x, y) && mode == Hexel.Core.SelectionMode.Replace)
            {
                ViewModel.SaveStateForUndo();
                _selection.LiftSelection(ViewModel.SpriteState);
                _selection.BeginDrag();
                _dragStartMousePos = e.GetPosition(Canvas.PixelGridContainer);
                _dragStartFloatingX = _selection.FloatingX;
                _dragStartFloatingY = _selection.FloatingY;
                Mouse.Capture(Canvas.PixelGridContainer);
                ViewModel.RedrawGridFromMemory();
                return;
            }

            if (mode == Hexel.Core.SelectionMode.Replace)
            {
                CommitCurrentSelection();
            }
            else if (_selection.IsFloating)
            {
                var oldMask = _selection.Mask;
                var oldMinX = _selection.MinX;
                var oldMinY = _selection.MinY;
                var oldMaxX = _selection.MaxX;
                var oldMaxY = _selection.MaxY;
                CommitCurrentSelection();
                if (oldMinX != -1)
                    _selection.ApplyMask(oldMask, oldMinX, oldMinY, oldMaxX, oldMaxY, Hexel.Core.SelectionMode.Replace);
            }

            if (ViewModel.CurrentTool == ToolMode.MagicWand)
            {
                ViewModel.SaveStateForUndo();
                var fillMask = ViewModel.DrawingService.GetFloodFillMask(ViewModel.SpriteState, x, y, out int minX, out int minY, out int maxX, out int maxY);
                _selection.ApplyMask(fillMask, minX, minY, maxX, maxY, mode);
                ViewModel.RedrawGridFromMemory();
            }
            else
            {
                _selectionAnchorX = x;
                _selectionAnchorY = y;
                if (ViewModel.CurrentTool == ToolMode.Lasso)
                    _selection.BeginLassoSelection(x, y, mode);
                else
                    _selection.BeginRectangleSelection(x, y, mode);
            }
        }

        private void HandleSelectionMove(int x, int y)
        {
            if (!_selection.IsSelecting) return;
            if (ViewModel.CurrentTool == ToolMode.Lasso)
            {
                _selection.AddLassoPoint(x, y);
            }
            else
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchorX != -1 && _selectionAnchorY != -1)
                {
                    int dx = x - _selectionAnchorX;
                    int dy = y - _selectionAnchorY;
                    int maxDist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    x = _selectionAnchorX + Math.Sign(dx) * maxDist;
                    y = _selectionAnchorY + Math.Sign(dy) * maxDist;
                }
                _selection.UpdateRectangleSelection(x, y);
            }
        }

        private void CommitCurrentSelection()
        {
            if (_selection == null || !_selection.HasActiveSelection) return;
            _selection.CommitSelection(ViewModel.SpriteState);
            ViewModel.RedrawGridFromMemory();
            ViewModel.UpdateTextOutputs();
            _selectionOverlay.Clear();
        }

        // ── Drag position update ──────────────────────────────────────────

        private void UpdateDragPosition(Point currentPos)
        {
            double gw = Canvas.PixelGridContainer.ActualWidth > 0 ? Canvas.PixelGridContainer.ActualWidth : 400.0;
            double gh = Canvas.PixelGridContainer.ActualHeight > 0 ? Canvas.PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            int newX = _dragStartFloatingX + (int)Math.Round((currentPos.X - _dragStartMousePos.X) / cw);
            int newY = _dragStartFloatingY + (int)Math.Round((currentPos.Y - _dragStartMousePos.Y) / ch);

            if (newX != _selection.FloatingX || newY != _selection.FloatingY)
            {
                _selection.MoveFloatingTo(newX, newY);
                ViewModel.RedrawGridFromMemory();
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
                if (_selection.HasActiveSelection)
                    CommitCurrentSelection();
            }

            var image = Canvas.CanvasImage;
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
            {
                image.CaptureMouse();
                var (x, y) = GetPixelCoordinates(e.GetPosition(image), image.ActualWidth, image.ActualHeight);

                if (ViewModel.CurrentTool == ToolMode.Marquee ||
                    ViewModel.CurrentTool == ToolMode.Lasso ||
                    ViewModel.CurrentTool == ToolMode.MagicWand)
                {
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionDown(e, cx, cy);
                    return;
                }

                _activeDrawMode = e.ChangedButton == MouseButton.Left ? DrawMode.Draw
                               : e.ChangedButton == MouseButton.Right ? DrawMode.Erase
                               : DrawMode.None;

                if (_activeDrawMode != DrawMode.None)
                {
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

            ViewModel.CursorX = x;
            ViewModel.CursorY = y;

            _brushCursor.Update(x, y, pos, image.ActualWidth, image.ActualHeight);

            if (x != _lastHoveredX || y != _lastHoveredY)
            {
                _lastHoveredX = x;
                _lastHoveredY = y;

                if ((ViewModel.CurrentTool == ToolMode.Marquee ||
                     ViewModel.CurrentTool == ToolMode.Lasso ||
                     ViewModel.CurrentTool == ToolMode.MagicWand)
                    && e.LeftButton == MouseButtonState.Pressed)
                {
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionMove(cx, cy);
                    return;
                }

                var mode = _activeDrawMode;
                if (mode != DrawMode.None || ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse ||
                    ViewModel.IsDrawingFilledRectangle || ViewModel.IsDrawingFilledEllipse)
                {
                    ViewModel.ProcessToolInput(x, y, ToolAction.Move, mode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _zoomPan.TryEndPan((ScrollViewer)sender, e);
        }

        // ── Status message ────────────────────────────────────────────────

        private void ShowStatus(string message)
        {
            if (StatusBar.StatusLabel == null) return;
            StatusBar.StatusLabel.Content = message;
            StatusBar.StatusLabel.Visibility = Visibility.Visible;

            _statusTimer?.Stop();
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                StatusBar.StatusLabel.Visibility = Visibility.Hidden;
                _statusTimer.Stop();
            };
            _statusTimer.Start();
        }

        // ── Utility ───────────────────────────────────────────────────────

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

        // ── Help Menu ─────────────────────────────────────────────────────

        private void MenuHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/") { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("Could not open the documentation link.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            var aboutDialog = new Views.AboutDialog
            {
                Owner = this
            };
            aboutDialog.ShowDialog();
        }
    }
}
