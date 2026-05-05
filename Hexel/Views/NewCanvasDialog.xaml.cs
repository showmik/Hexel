using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Hexel.Core;
using Hexel.ViewModels;

namespace Hexel.Views
{
    public partial class NewCanvasDialog : Window
    {
        /// <summary>Result dimensions if the user clicked Create, null if cancelled.</summary>
        public (int Width, int Height)? Result { get; private set; }

        public NewCanvasDialog()
        {
            InitializeComponent();
            PresetComboBox.SelectedIndex = 0; // "Custom"
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

        private void Create_Click(object sender, RoutedEventArgs e)
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

            Result = (w, h);
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
