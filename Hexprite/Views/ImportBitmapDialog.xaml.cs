using Hexprite.Core;
using Hexprite.Services;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hexprite.Views
{
    public partial class ImportBitmapDialog : Window
    {
        public BitmapImportSettings? Result { get; private set; }

        public ImportBitmapDialog(string fileName, BitmapImportSettings initialSettings)
        {
            InitializeComponent();

            TxtSourceFile.Text = fileName;
            DitherCombo.ItemsSource = Enum.GetValues(typeof(BitmapDitheringAlgorithm));
            DitherCombo.SelectedItem = initialSettings.DitheringAlgorithm;
            DitherCombo.SelectionChanged += AnySettingChanged;

            TxtThreshold.Text = Math.Clamp(initialSettings.Threshold, 0, 255).ToString();
            TxtAlphaThreshold.Text = Math.Clamp(initialSettings.AlphaThreshold, 0, 255).ToString();
            TxtMaxDimension.Text = Math.Clamp(initialSettings.MaxDimension, 1, SpriteState.MaxDimension).ToString();
            ChkInvert.IsChecked = initialSettings.Invert;
            RefreshImportEnabled();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSettings(out BitmapImportSettings? settings))
                return;

            Result = settings;
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

        private void AnyTextSettingChanged(object sender, TextChangedEventArgs e)
        {
            RefreshImportEnabled();
        }

        private void AnySettingChanged(object sender, RoutedEventArgs e)
        {
            RefreshImportEnabled();
        }

        private void RefreshImportEnabled()
        {
            BtnImport.IsEnabled = TryReadSettings(out _);
        }

        private bool TryReadSettings(out BitmapImportSettings? settings)
        {
            settings = null;

            if (!int.TryParse(TxtThreshold.Text, out int threshold) || threshold < 0 || threshold > 255)
                return false;

            if (!int.TryParse(TxtAlphaThreshold.Text, out int alphaThreshold) || alphaThreshold < 0 || alphaThreshold > 255)
                return false;

            if (!int.TryParse(TxtMaxDimension.Text, out int maxDimension) || maxDimension < 1 || maxDimension > SpriteState.MaxDimension)
                return false;

            if (DitherCombo.SelectedItem is not BitmapDitheringAlgorithm algorithm)
                return false;

            settings = new BitmapImportSettings
            {
                DitheringAlgorithm = algorithm,
                Threshold = threshold,
                AlphaThreshold = alphaThreshold,
                MaxDimension = maxDimension,
                Invert = ChkInvert.IsChecked == true
            };

            return true;
        }
    }
}
