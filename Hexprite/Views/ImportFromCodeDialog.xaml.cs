using System;
using System.Windows;
using System.Windows.Input;
using Hexprite.Services;

namespace Hexprite.Views
{
    /// <summary>
    /// Modal dialog for importing sprite data from pasted code.
    /// Auto-detects width/height from NAME_WIDTH / NAME_HEIGHT constants
    /// and lets the user override them before importing.
    /// </summary>
    public partial class ImportFromCodeDialog : Window
    {
        /// <summary>
        /// The result of a successful import.
        /// Width and Height are the resolved canvas dimensions;
        /// Code is the raw pasted text for the service to parse;
        /// SpriteName is the auto-detected variable name (may be null).
        /// </summary>
        public (int Width, int Height, string Code, string? SpriteName, bool IsXbm)? Result { get; private set; }

        private bool _isXbm;

        private string? _detectedSpriteName;

        private bool _dimensionsAutoDetected;
        private bool _suppressAutoDetect;

        public ImportFromCodeDialog()
        {
            InitializeComponent();
            var prefs = UserPreferencesService.Get();
            _suppressAutoDetect = true;
            TxtWidth.Text = prefs.ImportFromCodeWidth.ToString();
            TxtHeight.Text = prefs.ImportFromCodeHeight.ToString();
            _suppressAutoDetect = false;
            _dimensionsAutoDetected = false;
            if (TxtDetectionHint != null)
                TxtDetectionHint.Text = "(last used)";
        }

        // ── Auto-detect dimensions from pasted code ──────────────────────

        private void TxtCode_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string code = TxtCode.Text;

            if (ImportFromCodeDetector.TryParseExplicitDimensions(code, out int parsedW, out int parsedH, out string detectionSource))
            {
                _suppressAutoDetect = true;
                TxtWidth.Text  = parsedW.ToString();
                TxtHeight.Text = parsedH.ToString();
                _suppressAutoDetect = false;
                _dimensionsAutoDetected = true;
                TxtDetectionHint.Text = $"(auto-detected from {detectionSource})";
            }
            else
            {
                int byteCount = ImportFromCodeDetector.CountHexDataBytes(code);

                if (byteCount > 0 && !_dimensionsAutoDetected)
                {
                    if (ImportFromCodeDetector.TryInferDimensionsFromHexData(code, byteCount, out int iw, out int ih,
                            out ImportDimensionInferHint inferHint))
                    {
                        _suppressAutoDetect = true;
                        TxtWidth.Text  = iw.ToString();
                        TxtHeight.Text = ih.ToString();
                        _suppressAutoDetect = false;
                        TxtDetectionHint.Text = inferHint switch
                        {
                            ImportDimensionInferHint.FromLineStructure =>
                                $"(detected from code structure — {byteCount} bytes)",
                            ImportDimensionInferHint.AmbiguousLineStructure =>
                                "(line structure ambiguous — guessed from byte count)",
                            _ =>
                                $"(guessed from {byteCount} bytes — adjust if needed)",
                        };
                    }
                }
                else if (byteCount == 0)
                {
                    TxtDetectionHint.Text = "";
                }
            }

            _detectedSpriteName = ImportFromCodeDetector.DetectVariableName(code);
            _isXbm = ImportFromCodeDetector.IsLikelyXbmFormat(code);

            UpdateStats();

            if (_isXbm && TxtDetectionHint != null)
            {
                if (string.IsNullOrWhiteSpace(TxtDetectionHint.Text) || TxtDetectionHint.Text.StartsWith("(auto-detected") || TxtDetectionHint.Text.StartsWith("(guessed"))
                {
                    TxtDetectionHint.Text = "XBM (LSB-first) detected — bytes will be bit-reversed";
                }
            }
        }

        private void Dimension_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_suppressAutoDetect || TxtDetectionHint == null) return;
            _dimensionsAutoDetected = false;
            TxtDetectionHint.Text = "(manually set)";
            UpdateStats();
        }

        private void UpdateStats()
        {
            if (TxtCode == null || TxtStats == null || BtnImport == null) return;

            string code = TxtCode.Text;
            int byteCount = ImportFromCodeDetector.CountHexDataBytes(code);

            bool validWidth  = int.TryParse(TxtWidth.Text, out int w) && w > 0 && w <= 256;
            bool validHeight = int.TryParse(TxtHeight.Text, out int h) && h > 0 && h <= 256;

            if (byteCount == 0)
            {
                TxtStats.Text = "Paste code above to begin.";
                BtnImport.IsEnabled = false;
                return;
            }

            int expectedBytes = validWidth && validHeight ? ImportFromCodeDetector.ExpectedByteCount(w, h) : 0;

            if (!validWidth || !validHeight)
            {
                TxtStats.Text = $"Found {byteCount} data bytes. Enter valid dimensions (1–256).";
                BtnImport.IsEnabled = false;
            }
            else if (byteCount < expectedBytes)
            {
                TxtStats.Text = $"Found {byteCount} bytes, but {w}×{h} needs {expectedBytes}. Missing data will be zeroed.";
                BtnImport.IsEnabled = true;
            }
            else if (byteCount > expectedBytes)
            {
                TxtStats.Text = $"Found {byteCount} bytes, but {w}×{h} only needs {expectedBytes}. Extra bytes will be ignored.";
                BtnImport.IsEnabled = true;
            }
            else
            {
                TxtStats.Text = $"Found {byteCount} bytes — perfect match for {w}×{h}.";
                BtnImport.IsEnabled = true;
            }

            // Append detected name info
            if (_detectedSpriteName != null)
                TxtStats.Text += $"  Name: \"{_detectedSpriteName}\"";
        }

        // ── Input validation ─────────────────────────────────────────────

        private void NumberOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        // ── Buttons ──────────────────────────────────────────────────────

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtWidth.Text, out int w) || w < 1 || w > 256) return;
            if (!int.TryParse(TxtHeight.Text, out int h) || h < 1 || h > 256) return;

            Result = (w, h, TxtCode.Text, _detectedSpriteName, _isXbm);
            UserPreferencesService.Update(p =>
            {
                p.ImportFromCodeWidth = w;
                p.ImportFromCodeHeight = h;
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
    }
}
