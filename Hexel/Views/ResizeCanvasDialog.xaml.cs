using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Hexel.Core;

namespace Hexel.Views
{
    public partial class ResizeCanvasDialog : Window
    {
        public (int Width, int Height, ResizeAnchor Anchor)? Result { get; private set; }

        private ResizeAnchor _selectedAnchor = ResizeAnchor.TopLeft;

        public ResizeCanvasDialog(int currentWidth, int currentHeight)
        {
            InitializeComponent();
            TxtCurrentSize.Text = $"{currentWidth} × {currentHeight}";
            TxtWidth.Text = currentWidth.ToString();
            TxtHeight.Text = currentHeight.ToString();
            PresetComboBox.SelectedIndex = 0;
        }

        private void Preset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is not string preset || preset == "Custom") return;
            var label = preset.Split(' ')[0];
            var parts = label.Split(new[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int pw) &&
                int.TryParse(parts[1], out int ph))
            {
                TxtWidth.Text = pw.ToString();
                TxtHeight.Text = ph.ToString();
            }
        }

        private void Anchor_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string tag) return;
            if (Enum.TryParse<ResizeAnchor>(tag, out var anchor))
                _selectedAnchor = anchor;
        }

        private void Resize_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtWidth.Text, out int w) || w <= 0 ||
                !int.TryParse(TxtHeight.Text, out int h) || h <= 0)
            {
                MessageBox.Show("Please enter valid positive dimensions.",
                    "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (w > SpriteState.MaxDimension || h > SpriteState.MaxDimension)
            {
                MessageBox.Show($"Maximum canvas size is {SpriteState.MaxDimension}×{SpriteState.MaxDimension}.",
                    "Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = (w, h, _selectedAnchor);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void NumberOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9]+");
        }
    }
}
