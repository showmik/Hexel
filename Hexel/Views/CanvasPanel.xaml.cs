using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hexel.Views
{
    public partial class CanvasPanel : UserControl
    {
        public CanvasPanel() { InitializeComponent(); }

        private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

        // ── Zoom RoutedEvents (kept as View plumbing — ZoomPanController needs UI refs) ──

        public event RoutedEventHandler ZoomInClicked
        {
            add { AddHandler(ZoomInClickedEvent, value); }
            remove { RemoveHandler(ZoomInClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomInClickedEvent =
            EventManager.RegisterRoutedEvent("ZoomInClicked", RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(CanvasPanel));

        public event RoutedEventHandler ZoomOutClicked
        {
            add { AddHandler(ZoomOutClickedEvent, value); }
            remove { RemoveHandler(ZoomOutClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomOutClickedEvent =
            EventManager.RegisterRoutedEvent("ZoomOutClicked", RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(CanvasPanel));

        public event RoutedEventHandler ZoomResetClicked
        {
            add { AddHandler(ZoomResetClickedEvent, value); }
            remove { RemoveHandler(ZoomResetClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomResetClickedEvent =
            EventManager.RegisterRoutedEvent("ZoomResetClicked", RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(CanvasPanel));

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)    => RaiseEvent(new RoutedEventArgs(ZoomInClickedEvent, sender));
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)   => RaiseEvent(new RoutedEventArgs(ZoomOutClickedEvent, sender));
        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(ZoomResetClickedEvent, sender));

        // ── View-level text normalization (pure display concern) ──────────

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
            => e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, @"^[0-9]+$");

        private void BrushSizeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && ViewModel != null)
            {
                if (int.TryParse(tb.Text, out int val))
                    ViewModel.BrushSize = System.Math.Clamp(val, 1, 64);
                tb.Text = ViewModel.BrushSize.ToString();
            }
        }

        private void BrushAngleTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && ViewModel != null)
            {
                if (int.TryParse(tb.Text, out int val))
                    ViewModel.BrushAngle = ((val % 360) + 360) % 360;
                tb.Text = ViewModel.BrushAngle.ToString();
            }
        }
    }
}
