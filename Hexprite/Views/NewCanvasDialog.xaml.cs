using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Hexprite.Core;
using Hexprite.Services;
using Hexprite.ViewModels;

namespace Hexprite.Views
{
    public partial class NewCanvasDialog : Window
    {
        /// <summary>Result dimensions if the user clicked Create, null if cancelled.</summary>
        public (int Width, int Height)? Result { get; private set; }

        public NewCanvasDialog()
        {
            InitializeComponent();
            var prefs = UserPreferencesService.Get();
            TxtWidth.Text = prefs.NewCanvasWidth.ToString();
            TxtHeight.Text = prefs.NewCanvasHeight.ToString();

            int presetIndex = Math.Clamp(
                prefs.NewCanvasPresetIndex,
                0,
                Math.Max(0, MainViewModel.DisplayPresets.Count - 1));
            PresetComboBox.SelectedIndex = presetIndex;
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
            UserPreferencesService.Update(p =>
            {
                p.NewCanvasWidth = w;
                p.NewCanvasHeight = h;
                p.NewCanvasPresetIndex = Math.Max(0, PresetComboBox.SelectedIndex);
            });
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
