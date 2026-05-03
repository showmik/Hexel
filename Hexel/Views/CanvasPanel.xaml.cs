using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hexel.Views
{
    public partial class CanvasPanel : UserControl
    {
        public CanvasPanel()
        {
            InitializeComponent();
        }

        private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

        // ── Zoom Events for MainWindow ────────────────────────────────────

        public event RoutedEventHandler ZoomInClicked
        {
            add { AddHandler(ZoomInClickedEvent, value); }
            remove { RemoveHandler(ZoomInClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomInClickedEvent = EventManager.RegisterRoutedEvent("ZoomInClicked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CanvasPanel));

        public event RoutedEventHandler ZoomOutClicked
        {
            add { AddHandler(ZoomOutClickedEvent, value); }
            remove { RemoveHandler(ZoomOutClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomOutClickedEvent = EventManager.RegisterRoutedEvent("ZoomOutClicked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CanvasPanel));

        public event RoutedEventHandler ZoomResetClicked
        {
            add { AddHandler(ZoomResetClickedEvent, value); }
            remove { RemoveHandler(ZoomResetClickedEvent, value); }
        }
        public static readonly RoutedEvent ZoomResetClickedEvent = EventManager.RegisterRoutedEvent("ZoomResetClicked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CanvasPanel));

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(ZoomInClickedEvent, sender));
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(ZoomOutClickedEvent, sender));
        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(ZoomResetClickedEvent, sender));

        // ── Brush Options ─────────────────────────────────────────────────

        private void BrushShape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is null || ViewModel is null) return;
            ViewModel.BrushShape = rb.Tag.ToString() switch
            {
                "Square" => Core.BrushShape.Square,
                "Line" => Core.BrushShape.Line,
                _ => Core.BrushShape.Circle
            };
        }

        private void BtnBrushDown_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.BrushSize--;
        }

        private void BtnBrushUp_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.BrushSize++;
        }

        // ── Text Validation ───────────────────────────────────────────────

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^[0-9]+$");
        }

        private void BrushSizeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && ViewModel != null)
            {
                if (int.TryParse(tb.Text, out int val))
                    ViewModel.BrushSize = Math.Clamp(val, 1, 64);
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
