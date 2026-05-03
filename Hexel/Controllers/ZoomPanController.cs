using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hexel.ViewModels;

namespace Hexel.Controllers
{
    /// <summary>
    /// Handles zoom (scroll wheel, buttons, keyboard) and middle-click panning.
    /// Extracted from MainWindow.xaml.cs to isolate navigation concerns.
    /// </summary>
    public class ZoomPanController
    {
        private readonly Slider _zoomSlider;
        private readonly Func<Image> _getCanvasImage;
        private readonly Func<ShellViewModel> _getShell;

        // ── Panning state ─────────────────────────────────────────────────
        private Point _panStartMouse;
        private Point _panStartScroll;
        public bool IsPanning { get; private set; }

        public const double ZoomFactor = 1.15;

        public ZoomPanController(Slider zoomSlider, Func<Image> getCanvasImage, Func<ShellViewModel> getShell)
        {
            _zoomSlider = zoomSlider ?? throw new ArgumentNullException(nameof(zoomSlider));
            _getCanvasImage = getCanvasImage;
            _getShell = getShell;
        }

        // ── Button handlers ───────────────────────────────────────────────

        public void ZoomIn() => ApplyZoomCentered(ZoomFactor);
        public void ZoomOut() => ApplyZoomCentered(1.0 / ZoomFactor);
        public void ZoomReset() => _zoomSlider.Value = 1.0;

        // ── Scroll wheel ──────────────────────────────────────────────────

        public void HandleMouseWheel(ScrollViewer sv, MouseWheelEventArgs e, Window window)
        {
            // Ctrl+Scroll = adjust brush size
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                var vm = _getShell()?.ActiveDocument;
                if (vm != null)
                    vm.BrushSize += e.Delta > 0 ? 1 : -1;
                e.Handled = true;
                return;
            }

            double factor = e.Delta > 0 ? ZoomFactor : 1.0 / ZoomFactor;
            double oldZoom = _zoomSlider.Value;
            double newZoom = SnapToTick(Math.Clamp(oldZoom * factor, _zoomSlider.Minimum, _zoomSlider.Maximum), factor > 1.0 ? 1 : -1);

            if (Math.Abs(newZoom - oldZoom) < 0.001) return;
            e.Handled = true;

            var mouseInSv = e.GetPosition(sv);
            double contentX = (sv.HorizontalOffset + mouseInSv.X) / oldZoom;
            double contentY = (sv.VerticalOffset + mouseInSv.Y) / oldZoom;

            _zoomSlider.Value = newZoom;
            sv.UpdateLayout();

            sv.ScrollToHorizontalOffset(contentX * newZoom - mouseInSv.X);
            sv.ScrollToVerticalOffset(contentY * newZoom - mouseInSv.Y);
        }

        // ── Pan start / move / end ────────────────────────────────────────

        /// <summary>Returns true if a pan gesture was started and the event should be handled.</summary>
        public bool TryStartPan(ScrollViewer sv, MouseButtonEventArgs e, Window window)
        {
            bool isPanGesture = e.ChangedButton == MouseButton.Middle ||
                               (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space));
            if (!isPanGesture) return false;

            IsPanning = true;
            _panStartMouse = e.GetPosition(window);
            _panStartScroll = new Point(sv.HorizontalOffset, sv.VerticalOffset);
            sv.CaptureMouse();
            sv.Cursor = Cursors.SizeAll;
            return true;
        }

        public void HandlePanMove(ScrollViewer sv, MouseEventArgs e, Window window)
        {
            if (!IsPanning) return;
            var delta = e.GetPosition(window) - _panStartMouse;
            sv.ScrollToHorizontalOffset(_panStartScroll.X - delta.X);
            sv.ScrollToVerticalOffset(_panStartScroll.Y - delta.Y);
            e.Handled = true;
        }

        public bool TryEndPan(ScrollViewer sv, MouseButtonEventArgs e)
        {
            if (!IsPanning) return false;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return false;
            IsPanning = false;
            sv.ReleaseMouseCapture();
            sv.Cursor = Cursors.Arrow;
            e.Handled = true;
            return true;
        }

        // ── Centered zoom (for buttons / keyboard) ────────────────────────

        public void ApplyZoomCentered(double factor)
        {
            var sv = FindScrollViewer();
            if (sv == null)
            {
                _zoomSlider.Value = SnapToTick(Math.Clamp(_zoomSlider.Value * factor, _zoomSlider.Minimum, _zoomSlider.Maximum), factor > 1.0 ? 1 : -1);
                return;
            }

            double oldZoom = _zoomSlider.Value;
            double newZoom = SnapToTick(Math.Clamp(oldZoom * factor, _zoomSlider.Minimum, _zoomSlider.Maximum), factor > 1.0 ? 1 : -1);
            if (Math.Abs(newZoom - oldZoom) < 0.001) return;

            double cx = sv.ViewportWidth / 2.0;
            double cy = sv.ViewportHeight / 2.0;
            double contentX = (sv.HorizontalOffset + cx) / oldZoom;
            double contentY = (sv.VerticalOffset + cy) / oldZoom;

            _zoomSlider.Value = newZoom;
            sv.UpdateLayout();

            sv.ScrollToHorizontalOffset(contentX * newZoom - cx);
            sv.ScrollToVerticalOffset(contentY * newZoom - cy);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private double SnapToTick(double value, double direction)
        {
            double tick = _zoomSlider.TickFrequency;
            long ticks = (long)Math.Round(value / tick);
            double snapped = ticks * tick;

            double current = _zoomSlider.Value;
            if (Math.Abs(snapped - current) < 0.001)
                snapped = direction > 0 ? current + tick : current - tick;

            return Math.Clamp(snapped, _zoomSlider.Minimum, _zoomSlider.Maximum);
        }

        private ScrollViewer? FindScrollViewer()
        {
            var image = _getCanvasImage();
            return image != null ? FindParent<ScrollViewer>(image) : null;
        }

        public static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
