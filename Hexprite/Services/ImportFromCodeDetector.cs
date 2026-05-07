using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hexprite.Services
{
    /// <summary>How <see cref="ImportFromCodeDetector.TryInferDimensionsFromHexData"/> chose width/height.</summary>
    public enum ImportDimensionInferHint
    {
        None,
        FromLineStructure,
        FromByteCount,
        AmbiguousLineStructure,
    }

    /// <summary>
    /// Pure helpers for the Import from Code dialog: dimension hints, variable names,
    /// line-structure analysis, and XBM detection. Keeps parsing rules unit-testable without WPF.
    /// </summary>
    public static class ImportFromCodeDetector
    {
        /// <summary>Returns true if pasted code likely uses XBM / LSB-first byte layout.</summary>
        public static bool IsLikelyXbmFormat(string code) =>
            code.Contains("drawXBM", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("XBM", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Strategy 1–2 from the dialog: NAME_WIDTH / HEIGHT constants and Hexprite usage comments.
        /// </summary>
        public static bool TryParseExplicitDimensions(string code, out int width, out int height, out string detectionSource)
        {
            width = height = 0;
            detectionSource = "";

            var widthMatch = Regex.Match(code, @"(?:_WIDTH\s*=\s*|_WIDTH\s+)(\d+)", RegexOptions.IgnoreCase);
            var heightMatch = Regex.Match(code, @"(?:_HEIGHT\s*=\s*|_HEIGHT\s+)(\d+)", RegexOptions.IgnoreCase);
            var adafruitMatch = Regex.Match(code, @"drawBitmap\([^,]+,\s*[^,]+,\s*[^,]+,\s*(\d+)\s*,\s*(\d+)");
            var u8g2BitmapMatch = Regex.Match(code, @"u8g2\.drawBitmap\([^,]+,\s*[^,]+,\s*(\d+)\s*,\s*(\d+)");
            var u8g2XbmMatch = Regex.Match(code, @"drawXBM\([^,]+,\s*[^,]+,\s*(\d+)\s*,\s*(\d+)");
            var plainCMatch = Regex.Match(code, @"as a (\d+)[x×](\d+) bitmap");
            var pythonMatch = Regex.Match(code, @"FrameBuffer\([^,]+,\s*(\d+)\s*,\s*(\d+)");

            if (widthMatch.Success && heightMatch.Success)
            {
                width = int.Parse(widthMatch.Groups[1].Value);
                height = int.Parse(heightMatch.Groups[1].Value);
                detectionSource = "constants";
                return true;
            }

            if (adafruitMatch.Success)
            {
                width = int.Parse(adafruitMatch.Groups[1].Value);
                height = int.Parse(adafruitMatch.Groups[2].Value);
                detectionSource = "usage comment";
                return true;
            }

            if (u8g2XbmMatch.Success)
            {
                width = int.Parse(u8g2XbmMatch.Groups[1].Value);
                height = int.Parse(u8g2XbmMatch.Groups[2].Value);
                detectionSource = "usage comment";
                return true;
            }

            if (u8g2BitmapMatch.Success)
            {
                int bpr = int.Parse(u8g2BitmapMatch.Groups[1].Value);
                width = bpr * 8;
                height = int.Parse(u8g2BitmapMatch.Groups[2].Value);
                detectionSource = "usage comment";
                return true;
            }

            if (plainCMatch.Success)
            {
                width = int.Parse(plainCMatch.Groups[1].Value);
                height = int.Parse(plainCMatch.Groups[2].Value);
                detectionSource = "usage comment";
                return true;
            }

            if (pythonMatch.Success)
            {
                width = int.Parse(pythonMatch.Groups[1].Value);
                height = int.Parse(pythonMatch.Groups[2].Value);
                detectionSource = "usage comment";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Counts <c>0xNN</c> literals after stripping // and /* */ comments (same as the dialog).
        /// </summary>
        public static int CountHexDataBytes(string code)
        {
            string cleanCode = Regex.Replace(code, @"//.*", "");
            cleanCode = Regex.Replace(cleanCode, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Matches(cleanCode, @"0[xX][0-9a-fA-F]{1,2}").Count;
        }

        /// <summary>
        /// Analyses data lines to find the most frequent hex-values-per-line (bytes-per-row).
        /// </summary>
        public static int DetectBytesPerRow(string code)
        {
            string[] lines = code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bprCounts = new Dictionary<int, int>();

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//") || trimmed.StartsWith("#") ||
                    trimmed.StartsWith("const ") || trimmed.StartsWith("static ") ||
                    trimmed == "{" || trimmed == "};" || trimmed == "};")
                    continue;

                int count = Regex.Matches(line, @"0[xX][0-9a-fA-F]{1,2}").Count;
                if (count > 0)
                {
                    bprCounts.TryGetValue(count, out int existing);
                    bprCounts[count] = existing + 1;
                }
            }

            if (bprCounts.Count == 0) return 0;

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
        /// Fallback dimension guess from total byte count (square preference, then common widths).
        /// </summary>
        public static bool TryGuessDimensionsFromByteCount(int byteCount, out int width, out int height)
        {
            width = height = 0;
            if (byteCount <= 0) return false;

            int[] candidates = { 8, 16, 24, 32, 48, 64, 96, 128, 256 };

            foreach (int w in candidates)
            {
                int bpr = (int)Math.Ceiling(w / 8.0);
                if (byteCount % bpr != 0) continue;
                int h = byteCount / bpr;
                if (h == w && h > 0 && h <= 256)
                {
                    width = w;
                    height = h;
                    return true;
                }
            }

            foreach (int w in candidates)
            {
                int bpr = (int)Math.Ceiling(w / 8.0);
                if (byteCount % bpr != 0) continue;
                int h = byteCount / bpr;
                if (h > 0 && h <= 256)
                {
                    width = w;
                    height = h;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Line-structure analysis plus byte-count fallback (mirrors the Import from Code dialog heuristics).
        /// </summary>
        public static bool TryInferDimensionsFromHexData(
            string code,
            int byteCount,
            out int width,
            out int height,
            out ImportDimensionInferHint hint)
        {
            width = height = 0;
            hint = ImportDimensionInferHint.None;
            if (byteCount <= 0) return false;

            int detectedBpr = DetectBytesPerRow(code);

            if (detectedBpr > 0 && byteCount % detectedBpr == 0)
            {
                int w = detectedBpr * 8;
                int h = byteCount / detectedBpr;
                if (w > 0 && w <= 256 && h > 0 && h <= 256)
                {
                    width = w;
                    height = h;
                    hint = ImportDimensionInferHint.FromLineStructure;
                    return true;
                }
            }

            if (detectedBpr > 0 && byteCount % detectedBpr != 0)
                hint = ImportDimensionInferHint.AmbiguousLineStructure;
            else
                hint = ImportDimensionInferHint.FromByteCount;

            return TryGuessDimensionsFromByteCount(byteCount, out width, out height);
        }

        /// <summary>
        /// Extracts the bitmap variable name from common C / MicroPython patterns.
        /// </summary>
        public static string? DetectVariableName(string code)
        {
            var match = Regex.Match(code,
                @"(?:PROGMEM|U8X8_PROGMEM|uint8_t|const|static|unsigned|char)\s+" +
                @"(?:(?:PROGMEM|U8X8_PROGMEM|uint8_t|const|static|unsigned|char)\s+)*" +
                @"([a-zA-Z_][a-zA-Z0-9_]*)\s*\[",
                RegexOptions.Multiline);

            if (match.Success)
                return match.Groups[1].Value;

            var pyMatch = Regex.Match(code,
                @"([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*bytearray\s*\(",
                RegexOptions.Multiline);

            if (pyMatch.Success)
                return pyMatch.Groups[1].Value;

            return null;
        }

        /// <summary>Expected byte count for a monochrome bitmap of <paramref name="width"/>×<paramref name="height"/>.</summary>
        public static int ExpectedByteCount(int width, int height) =>
            height * (int)Math.Ceiling(width / 8.0);
    }
}
