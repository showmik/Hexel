using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Hexel.Views
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
        /// Code is the raw pasted text for the service to parse.
        /// </summary>
        public (int Width, int Height, string Code)? Result { get; private set; }

        private bool _dimensionsAutoDetected;
        private bool _suppressAutoDetect;

        public ImportFromCodeDialog()
        {
            InitializeComponent();
        }

        // ── Auto-detect dimensions from pasted code ──────────────────────

        private void TxtCode_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string code = TxtCode.Text;

            // Strategy 1: Extract width and height from explicit constants like:
            //   const uint8_t MYSPRITE_WIDTH  = 16;
            //   #define MYSPRITE_WIDTH 16
            var widthMatch  = Regex.Match(code, @"(?:_WIDTH\s*=\s*|_WIDTH\s+)(\d+)", RegexOptions.IgnoreCase);
            var heightMatch = Regex.Match(code, @"(?:_HEIGHT\s*=\s*|_HEIGHT\s+)(\d+)", RegexOptions.IgnoreCase);

            if (widthMatch.Success && heightMatch.Success)
            {
                _suppressAutoDetect = true;
                TxtWidth.Text  = widthMatch.Groups[1].Value;
                TxtHeight.Text = heightMatch.Groups[1].Value;
                _suppressAutoDetect = false;
                _dimensionsAutoDetected = true;
                TxtDetectionHint.Text = "(auto-detected from constants)";
            }
            else
            {
                var hexMatches = Regex.Matches(code, @"0[xX][0-9a-fA-F]{1,2}");
                int byteCount = hexMatches.Count;

                if (byteCount > 0 && !_dimensionsAutoDetected)
                {
                    TryGuessDimensions(code, byteCount);
                }
                else if (byteCount == 0)
                {
                    TxtDetectionHint.Text = "";
                }
            }

            UpdateStats();
        }

        /// <summary>
        /// Multi-strategy dimension guesser. Priority:
        ///   1. Line-structure analysis: count hex values on data lines to find
        ///      bytes-per-row, which directly gives width = bpr × 8.
        ///   2. Array size from brackets: parse name[N] to confirm total bytes.
        ///   3. Fallback: prefer square dimensions, then common widths.
        /// </summary>
        private void TryGuessDimensions(string code, int byteCount)
        {
            int detectedBpr = DetectBytesPerRow(code);

            if (detectedBpr > 0)
            {
                // Line structure gives us bytes-per-row → width
                int w = detectedBpr * 8;
                int h = byteCount / detectedBpr;

                if (w > 0 && w <= 256 && h > 0 && h <= 256)
                {
                    _suppressAutoDetect = true;
                    TxtWidth.Text  = w.ToString();
                    TxtHeight.Text = h.ToString();
                    _suppressAutoDetect = false;
                    TxtDetectionHint.Text = $"(detected from code structure — {byteCount} bytes)";
                    return;
                }
            }

            // Fallback: prefer square, then common widths
            GuessFromByteCount(byteCount);
            TxtDetectionHint.Text = $"(guessed from {byteCount} bytes — adjust if needed)";
        }

        /// <summary>
        /// Analyses the data lines of the code to determine bytes-per-row.
        /// Looks at lines that contain hex values (0xNN) and finds the most
        /// common count of hex values per line — that's bytes-per-row.
        /// Skips lines that look like comments or constant declarations.
        /// </summary>
        private static int DetectBytesPerRow(string code)
        {
            var lines = code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bprCounts = new System.Collections.Generic.Dictionary<int, int>();

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                // Skip comments, constant declarations, brackets-only lines
                if (trimmed.StartsWith("//") || trimmed.StartsWith("#") ||
                    trimmed.StartsWith("const ") || trimmed.StartsWith("static ") ||
                    trimmed == "{" || trimmed == "};" || trimmed == "};")
                    continue;

                // Count hex values on this line
                int count = Regex.Matches(line, @"0[xX][0-9a-fA-F]{1,2}").Count;
                if (count > 0)
                {
                    bprCounts.TryGetValue(count, out int existing);
                    bprCounts[count] = existing + 1;
                }
            }

            if (bprCounts.Count == 0) return 0;

            // Return the most frequently occurring hex-values-per-line
            int bestBpr = 0, bestFreq = 0;
            foreach (var kvp in bprCounts)
            {
                if (kvp.Value > bestFreq)
                {
                    bestFreq = kvp.Value;
                    bestBpr = kvp.Key;
                }
            }

            return bestBpr;
        }

        /// <summary>
        /// Fallback: try square first, then common widths.
        /// </summary>
        private void GuessFromByteCount(int byteCount)
        {
            // Try square: width == height, byteCount = h * ceil(w/8)
            // → byteCount = w * ceil(w/8)
            int[] candidates = { 8, 16, 24, 32, 48, 64, 96, 128, 256 };
            foreach (int w in candidates)
            {
                int bpr = (int)Math.Ceiling(w / 8.0);
                if (byteCount % bpr == 0)
                {
                    int h = byteCount / bpr;
                    if (h == w && h > 0 && h <= 256)
                    {
                        // Perfect square match — prefer this
                        _suppressAutoDetect = true;
                        TxtWidth.Text  = w.ToString();
                        TxtHeight.Text = h.ToString();
                        _suppressAutoDetect = false;
                        return;
                    }
                }
            }

            // No square match — try any valid combo, preferring wider over taller
            foreach (int w in candidates)
            {
                int bpr = (int)Math.Ceiling(w / 8.0);
                if (byteCount % bpr == 0)
                {
                    int h = byteCount / bpr;
                    if (h > 0 && h <= 256)
                    {
                        _suppressAutoDetect = true;
                        TxtWidth.Text  = w.ToString();
                        TxtHeight.Text = h.ToString();
                        _suppressAutoDetect = false;
                        return;
                    }
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
            var hexMatches = Regex.Matches(code, @"0[xX][0-9a-fA-F]{1,2}");
            int byteCount = hexMatches.Count;

            bool validWidth  = int.TryParse(TxtWidth.Text, out int w) && w > 0 && w <= 256;
            bool validHeight = int.TryParse(TxtHeight.Text, out int h) && h > 0 && h <= 256;

            if (byteCount == 0)
            {
                TxtStats.Text = "Paste code above to begin.";
                BtnImport.IsEnabled = false;
                return;
            }

            int expectedBytes = validWidth && validHeight ? h * (int)Math.Ceiling(w / 8.0) : 0;

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

            Result = (w, h, TxtCode.Text);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
