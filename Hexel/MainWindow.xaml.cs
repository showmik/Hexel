using Hexel.Core;
using Hexel.Services;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hexel
{
    public partial class MainWindow : Window
    {
        // ── Dependencies ──────────────────────────────────────────────────
        private ViewModels.MainViewModel ViewModel => (ViewModels.MainViewModel)DataContext;
        private readonly ISelectionService _selection;

        // ── Drag tracking (screen-space, belongs in the View) ─────────────
        private Point _dragStartMousePos;
        private int _dragStartFloatingX;
        private int _dragStartFloatingY;

        // ── Panning state ─────────────────────────────────────────────────
        private Point _panStartMouse;
        private Point _panStartScroll;
        private bool _isPanning;

        // ── Last hovered pixel (avoids spamming ViewModel on same pixel) ──
        private int _lastHoveredX = -1;
        private int _lastHoveredY = -1;

        // ── Status label fade timer ───────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _statusTimer;

        // ── Constructor ───────────────────────────────────────────────────

        public MainWindow(ViewModels.MainViewModel vm, ISelectionService selection)
        {
            InitializeComponent();
            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));

            // When SelectionService state changes, redraw the overlays
            _selection.SelectionChanged += (_, _) => UpdateSelectionOverlays();

            // When Undo/Redo fires, cancel any in-progress overlays
            vm.HistoryRestored += (_, _) =>
            {
                ClearSelectionOverlays();
                ReleaseDragCapture();
            };

            // Replace the old blocking MessageBox "Copied!" with a status label
            vm.CopyHexExecuted += (_, _) => ShowStatus("Copied to clipboard");
        }

        // ── Tool selection ────────────────────────────────────────────────

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is null || ViewModel is null) return;

            ViewModel.CurrentTool = rb.Tag.ToString() switch
            {
                "Fill" => ToolMode.Fill,
                "Marquee" => ToolMode.Marquee,
                "Lasso" => ToolMode.Lasso,
                "Rectangle" => ToolMode.Rectangle,
                "Ellipse" => ToolMode.Ellipse,
                "Line" => ToolMode.Line,
                _ => ToolMode.Pencil
            };

            // Switching away from a selection tool commits any floating content
            if (ViewModel.CurrentTool != ToolMode.Marquee &&
                ViewModel.CurrentTool != ToolMode.Lasso)
            {
                CommitCurrentSelection();
            }
        }

        // ── Global overrides: mouse up, mouse move, key down ─────────────

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            // Update floating layer position during drag
            if (_selection.IsDragging && e.LeftButton == MouseButtonState.Pressed)
                UpdateDragPosition(e.GetPosition(PixelGridContainer));
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);

            if (ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false);
            }
            else if (ViewModel.CurrentTool == ToolMode.Pencil)
            {
                ViewModel.ProcessToolInput(-1, -1, ToolAction.Up, DrawMode.None, false);
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                // Finalise a selection on mouse-up
                if ((ViewModel.CurrentTool == ToolMode.Lasso || ViewModel.CurrentTool == ToolMode.Marquee) && _selection.IsSelecting)
                    _selection.FinalizeSelection();

                ReleaseDragCapture();
                _selection.EndDrag();
            }

            // Always release mouse capture from the canvas if we had it
            if (Mouse.Captured == CanvasImage)
                Mouse.Capture(null);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

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
                            ClearSelectionOverlays();
                            e.Handled = true;
                        }
                        break;

                    case Key.Escape:
                        CommitCurrentSelection();
                        e.Handled = true;
                        break;

                    case Key.P: if (RbPencil != null) RbPencil.IsChecked = true; e.Handled = true; break;
                    case Key.L: if (RbLine != null) RbLine.IsChecked = true; e.Handled = true; break;
                    case Key.R: if (RbRectangle != null) RbRectangle.IsChecked = true; e.Handled = true; break;
                    case Key.E: if (RbEllipse != null) RbEllipse.IsChecked = true; e.Handled = true; break;
                    case Key.F: if (RbFill != null) RbFill.IsChecked = true; e.Handled = true; break;
                    case Key.M: if (RbMarquee != null) RbMarquee.IsChecked = true; e.Handled = true; break;
                    case Key.S: if (RbLasso != null) RbLasso.IsChecked = true; e.Handled = true; break;
                }
            }
        }

        // ── Selection tool handling ───────────────────────────────────────

        private void HandleSelectionDown(MouseButtonEventArgs e, int x, int y)
        {

            if (_selection.HasActiveSelection && _selection.IsPixelInSelection(x, y))
            {
                // Click inside an existing selection → lift it into a floating layer and begin drag
                ViewModel.SaveStateForUndo();
                _selection.LiftSelection(ViewModel.SpriteState);
                _selection.BeginDrag();

                _dragStartMousePos = e.GetPosition(PixelGridContainer);
                _dragStartFloatingX = _selection.FloatingX;
                _dragStartFloatingY = _selection.FloatingY;

                Mouse.Capture(PixelGridContainer);
                ViewModel.RedrawGridFromMemory();
            }
            else
            {
                // Click outside → commit any existing selection and start a new one
                CommitCurrentSelection();

                if (ViewModel.CurrentTool == ToolMode.Lasso)
                    _selection.BeginLassoSelection(x, y);
                else
                    _selection.BeginRectangleSelection(x, y);
            }
        }

        private void HandleSelectionMove(int x, int y)
        {
            if (!_selection.IsSelecting) return;

            if (ViewModel.CurrentTool == ToolMode.Lasso)
                _selection.AddLassoPoint(x, y);
            else
                _selection.UpdateRectangleSelection(x, y);
        }

        private void CommitCurrentSelection()
        {
            if (!_selection.HasActiveSelection) return;
            _selection.CommitSelection(ViewModel.SpriteState);
            ViewModel.RedrawGridFromMemory();
            ViewModel.UpdateTextOutputs();
            ClearSelectionOverlays();
        }

        // ── Drag position update ──────────────────────────────────────────

        private void UpdateDragPosition(Point currentPos)
        {
            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
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
            if (Mouse.Captured == PixelGridContainer)
                Mouse.Capture(null);
        }

        // ── Overlay rendering ─────────────────────────────────────────────
        // All overlay methods are pure View concerns: they read pixel-space data
        // from ISelectionService and convert to screen-space for rendering.

        private void UpdateSelectionOverlays()
        {
            if (!_selection.HasActiveSelection && !_selection.IsSelecting)
            {
                ClearSelectionOverlays();
                return;
            }

            if (ViewModel.CurrentTool == ToolMode.Lasso)
                UpdateLassoOverlay();
            else
                UpdateMarqueeOverlay();
        }

        private void UpdateMarqueeOverlay()
        {
            if (MarqueeOverlay == null) return;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            // When a floating layer is active, highlight the floating layer bounds
            int minX = _selection.IsFloating ? _selection.FloatingX : _selection.MinX;
            int minY = _selection.IsFloating ? _selection.FloatingY : _selection.MinY;
            int maxX = _selection.IsFloating ? _selection.FloatingX + _selection.FloatingWidth - 1 : _selection.MaxX;
            int maxY = _selection.IsFloating ? _selection.FloatingY + _selection.FloatingHeight - 1 : _selection.MaxY;

            Canvas.SetLeft(MarqueeOverlay, minX * cw);
            Canvas.SetTop(MarqueeOverlay, minY * ch);
            MarqueeOverlay.Width = (maxX - minX + 1) * cw;
            MarqueeOverlay.Height = (maxY - minY + 1) * ch;
            MarqueeOverlay.Visibility = Visibility.Visible;

            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;
        }

        private void UpdateLassoOverlay()
        {
            if (LassoOverlay == null) return;

            double gw = PixelGridContainer.ActualWidth > 0 ? PixelGridContainer.ActualWidth : 400.0;
            double gh = PixelGridContainer.ActualHeight > 0 ? PixelGridContainer.ActualHeight : 400.0;
            double cw = gw / ViewModel.SpriteState.Width;
            double ch = gh / ViewModel.SpriteState.Height;

            // After finalization (or while dragging), show the per-pixel filled region.
            // Use GeometryGroup to avoid the O(n²) Geometry.Combine loop that was here before.
            bool[,]? mask;
            int baseX, baseY, maskW, maskH;

            if (_selection.IsFloating && _selection.FloatingPixels != null)
            {
                mask = _selection.FloatingPixels;
                baseX = _selection.FloatingX;
                baseY = _selection.FloatingY;
                maskW = _selection.FloatingWidth;
                maskH = _selection.FloatingHeight;
            }
            else if (_selection.IsSelecting && !_selection.HasActiveSelection)
            {
                if (_selection.LassoPoints.Count < 3)
                {
                    ClearSelectionOverlays();
                    return;
                }
                baseX = _selection.MinX;
                baseY = _selection.MinY;
                maskW = _selection.MaxX - _selection.MinX + 1;
                maskH = _selection.MaxY - _selection.MinY + 1;
                mask = new bool[maskW, maskH];
                for (int fy = 0; fy < maskH; fy++)
                    for (int fx = 0; fx < maskW; fx++)
                        mask[fx, fy] = _selection.IsPointInLasso(baseX + fx, baseY + fy);
            }
            else if (_selection.Mask != null)
            {
                mask = _selection.Mask;
                baseX = _selection.MinX;
                baseY = _selection.MinY;
                maskW = _selection.MaxX - _selection.MinX + 1;
                maskH = _selection.MaxY - _selection.MinY + 1;
            }
            else
            {
                ClearSelectionOverlays();
                return;
            }

            var rects = new System.Collections.Generic.List<Geometry>();
            for (int fy = 0; fy < maskH; fy++)
            {
                int startX = -1;
                for (int fx = 0; fx <= maskW; fx++)
                {
                    bool isSet = fx < maskW && mask[fx, fy];
                    if (isSet && startX == -1) startX = fx;
                    else if (!isSet && startX != -1)
                    {
                        rects.Add(new RectangleGeometry(
                            new Rect((baseX + startX) * cw, (baseY + fy) * ch, (fx - startX) * cw, ch)));
                        startX = -1;
                    }
                }
            }

            Geometry finalGeom;
            if (rects.Count == 0)
                finalGeom = Geometry.Empty;
            else
            {
                while (rects.Count > 1)
                {
                    var nextLevel = new System.Collections.Generic.List<Geometry>();
                    for (int i = 0; i < rects.Count; i += 2)
                    {
                        if (i + 1 < rects.Count)
                            nextLevel.Add(Geometry.Combine(rects[i], rects[i + 1], GeometryCombineMode.Union, null));
                        else
                            nextLevel.Add(rects[i]);
                    }
                    rects = nextLevel;
                }
                finalGeom = rects[0];
            }

            LassoOverlay.Data = finalGeom;
            LassoOverlay.Visibility = finalGeom != Geometry.Empty ? Visibility.Visible : Visibility.Hidden;
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
        }

        private void ClearSelectionOverlays()
        {
            if (MarqueeOverlay != null) MarqueeOverlay.Visibility = Visibility.Hidden;
            if (LassoOverlay != null) LassoOverlay.Visibility = Visibility.Hidden;
        }

        // ── Zoom & pan ────────────────────────────────────────────────────

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
            => ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.2);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
            => ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.2);

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
            => ZoomSlider.Value = 1.0;

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            double delta = e.Delta > 0 ? 0.2 : -0.2;
            ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }

        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool isPanGesture = e.ChangedButton == MouseButton.Middle ||
                               (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space));

            var sv = (ScrollViewer)sender;

            if (isPanGesture)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(this);
                _panStartScroll = new Point(sv.HorizontalOffset, sv.VerticalOffset);
                sv.CaptureMouse();
                sv.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            // Ignore scrollbar clicks
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = VisualTreeHelper.GetParent(dep);
                while (parent != null)
                {
                    if (parent is System.Windows.Controls.Primitives.ScrollBar)
                        return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not Image)
            {
                // Clicked outside the canvas in the empty viewer space
                if (_selection.HasActiveSelection)
                {
                    CommitCurrentSelection();
                }
            }

            // Start drawing or selecting!
            var image = CanvasImage;
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
            {
                image.CaptureMouse();

                var (x, y) = GetPixelCoordinates(e.GetPosition(image), image.ActualWidth, image.ActualHeight);

                if (ViewModel.CurrentTool == ToolMode.Marquee ||
                    ViewModel.CurrentTool == ToolMode.Lasso)
                {
                    // Clamp for selection tools which require in-bounds coordinates
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionDown(e, cx, cy);
                    return;
                }

                var mode = e.LeftButton == MouseButtonState.Pressed ? DrawMode.Draw
                         : e.RightButton == MouseButtonState.Pressed ? DrawMode.Erase
                         : DrawMode.None;

                if (mode != DrawMode.None)
                {
                    _lastHoveredX = x;
                    _lastHoveredY = y;
                    ViewModel.ProcessToolInput(x, y, ToolAction.Down, mode,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                }
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var sv = (ScrollViewer)sender;
                var delta = e.GetPosition(this) - _panStartMouse;
                sv.ScrollToHorizontalOffset(_panStartScroll.X - delta.X);
                sv.ScrollToVerticalOffset(_panStartScroll.Y - delta.Y);
                e.Handled = true;
                return;
            }

            var image = CanvasImage;
            var (x, y) = GetPixelCoordinates(e.GetPosition(image), image.ActualWidth, image.ActualHeight);

            if (x != _lastHoveredX || y != _lastHoveredY)
            {
                _lastHoveredX = x;
                _lastHoveredY = y;

                if ((ViewModel.CurrentTool == ToolMode.Marquee ||
                     ViewModel.CurrentTool == ToolMode.Lasso)
                    && e.LeftButton == MouseButtonState.Pressed)
                {
                    // Clamp for selection tools which require in-bounds coordinates
                    int cx = Math.Clamp(x, 0, ViewModel.SpriteState.Width - 1);
                    int cy = Math.Clamp(y, 0, ViewModel.SpriteState.Height - 1);
                    HandleSelectionMove(cx, cy);
                    return;
                }

                var mode = e.LeftButton == MouseButtonState.Pressed ? DrawMode.Draw
                         : e.RightButton == MouseButtonState.Pressed ? DrawMode.Erase
                         : DrawMode.None;

                if (mode != DrawMode.None || ViewModel.IsDrawingLine || ViewModel.IsDrawingRectangle || ViewModel.IsDrawingEllipse)
                {
                    ViewModel.ProcessToolInput(x, y, ToolAction.Move, mode, false);
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            var sv = (ScrollViewer)sender;
            _isPanning = false;
            sv.ReleaseMouseCapture();
            sv.Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        // ── Status message ────────────────────────────────────────────────

        private void ShowStatus(string message)
        {
            if (StatusLabel == null) return;
            StatusLabel.Content = message;
            StatusLabel.Visibility = Visibility.Visible;

            _statusTimer?.Stop();
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                StatusLabel.Visibility = Visibility.Hidden;
                _statusTimer.Stop();
            };
            _statusTimer.Start();
        }

        // ── Canvas size input helpers ─────────────────────────────────────

        private void CanvasInput_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Highlight existing text rather than deleting it, so a mis-click doesn't
            // instantly wipe the value.
            if (sender is TextBox tb) tb.SelectAll();
        }

        private void CanvasInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                tb.Focus();
            }
        }

        private void CanvasInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            if (!int.TryParse(ViewModel.InputWidth, out int w) || w <= 0)
                ViewModel.InputWidth = (ViewModel.SpriteState?.Width ?? 16).ToString();
            if (!int.TryParse(ViewModel.InputHeight, out int h) || h <= 0)
                ViewModel.InputHeight = (ViewModel.SpriteState?.Height ?? 16).ToString();
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9]+");
        }

        private void TextBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)) &&
                Regex.IsMatch((string)e.DataObject.GetData(typeof(string))!, "[^0-9]+"))
                e.CancelCommand();
            else if (!e.DataObject.GetDataPresent(typeof(string)))
                e.CancelCommand();
        }

        // ── Utility ───────────────────────────────────────────────────────

        private (int x, int y) GetPixelCoordinates(Point pos, double actualWidth, double actualHeight)
        {
            int w = ViewModel.SpriteState.Width;
            int h = ViewModel.SpriteState.Height;
            if (actualWidth == 0 || actualHeight == 0) return (0, 0);

            int x = (int)Math.Floor(pos.X / actualWidth * w);
            int y = (int)Math.Floor(pos.Y / actualHeight * h);

            // When the mouse is inside the canvas area, clamp to valid pixel range
            // (the right/bottom edge maps to w/h which is one past the last pixel).
            // When truly outside, leave unclamped so shape tools can extend past edges.
            if (pos.X >= 0 && pos.X <= actualWidth)  x = Math.Clamp(x, 0, w - 1);
            if (pos.Y >= 0 && pos.Y <= actualHeight) y = Math.Clamp(y, 0, h - 1);

            return (x, y);
        }
    }
}
