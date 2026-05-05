using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hexprite.Core;

namespace Hexprite.Services
{
    public class CodeGeneratorService : ICodeGeneratorService
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════════════

        public string GenerateCode(
            SpriteState state,
            ExportSettings settings,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH)
        {
            // Resolve the effective pixel grid (including any floating selection)
            byte[] data = BuildByteArray(state, settings.Format == ExportFormat.U8g2DrawXBM,
                isFloating, floatingPixels, floatX, floatY, floatW, floatH);

            string name = SanitiseName(settings.SpriteName);
            string hexFmt = settings.UppercaseHex ? "X2" : "x2";

            return settings.Format switch
            {
                ExportFormat.AdafruitGfx      => BuildAdafruitGfx(data, name, state, settings, hexFmt),
                ExportFormat.U8g2DrawBitmap   => BuildU8g2DrawBitmap(data, name, state, settings, hexFmt),
                ExportFormat.U8g2DrawXBM      => BuildU8g2DrawXBM(data, name, state, settings, hexFmt),
                ExportFormat.PlainCArray       => BuildPlainCArray(data, name, state, settings, hexFmt),
                ExportFormat.MicroPython      => BuildMicroPython(data, name, state, settings, hexFmt),
                ExportFormat.RawHex           => BuildRawHex(state, settings, isFloating, floatingPixels,
                                                     floatX, floatY, floatW, floatH, hexFmt),
                ExportFormat.RawBinary        => BuildRawBinary(state, settings, isFloating, floatingPixels,
                                                     floatX, floatY, floatW, floatH),
                _                             => string.Empty
            };
        }

        public Task<string> GenerateCodeAsync(
            SpriteState state,
            ExportSettings settings,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH)
        {
            // Clone state to avoid cross-thread data races
            var clone = state.Clone();
            bool[,]? fpClone = floatingPixels != null
                ? (bool[,])floatingPixels.Clone()
                : null;

            return Task.Run(() => GenerateCode(
                clone, settings, isFloating, fpClone,
                floatX, floatY, floatW, floatH));
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Format generators
        // ═══════════════════════════════════════════════════════════════════════

        // ── Adafruit GFX ─────────────────────────────────────────────────────

        private static string BuildAdafruitGfx(
            byte[] data, string name, SpriteState s, ExportSettings cfg, string hexFmt)
        {
            var sb = new StringBuilder();

            if (cfg.IncludeUsageComment)
            {
                sb.AppendLine($"// display.drawBitmap(x, y, {name}, {s.Width}, {s.Height}, color);");
            }

            if (cfg.IncludeDimensionConstants)
            {
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_WIDTH  = {s.Width};");
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_HEIGHT = {s.Height};");
            }

            string arraySize = cfg.IncludeArraySize ? data.Length.ToString() : "";
            sb.AppendLine($"const uint8_t PROGMEM {name}[{arraySize}] = {{");
            AppendByteBody(sb, data, s, cfg, hexFmt, rowPrefix: "  ");
            sb.Append("};");

            return sb.ToString();
        }

        // ── u8g2 drawBitmap ──────────────────────────────────────────────────

        private static string BuildU8g2DrawBitmap(
            byte[] data, string name, SpriteState s, ExportSettings cfg, string hexFmt)
        {
            var sb = new StringBuilder();

            if (cfg.IncludeUsageComment)
            {
                sb.AppendLine($"// u8g2.drawBitmap(x, y, {BytesPerRow(s.Width)}, {s.Height}, {name});");
            }

            if (cfg.IncludeDimensionConstants)
            {
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_WIDTH  = {s.Width};");
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_HEIGHT = {s.Height};");
            }

            string arraySize = cfg.IncludeArraySize ? data.Length.ToString() : "";
            sb.AppendLine($"const uint8_t U8X8_PROGMEM {name}[{arraySize}] = {{");
            AppendByteBody(sb, data, s, cfg, hexFmt, rowPrefix: "  ");
            sb.Append("};");

            return sb.ToString();
        }

        // ── u8g2 drawXBM ────────────────────────────────────────────────────

        private static string BuildU8g2DrawXBM(
            byte[] data, string name, SpriteState s, ExportSettings cfg, string hexFmt)
        {
            var sb = new StringBuilder();

            if (cfg.IncludeUsageComment)
            {
                // XBM width must be rounded up to the next multiple of 8
                sb.AppendLine($"// u8g2.drawXBM(x, y, {s.Width}, {s.Height}, {name});");
            }

            if (cfg.IncludeDimensionConstants)
            {
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_WIDTH  = {s.Width};");
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_HEIGHT = {s.Height};");
            }

            string arraySize = cfg.IncludeArraySize ? data.Length.ToString() : "";
            sb.AppendLine($"const uint8_t PROGMEM {name}[{arraySize}] = {{");
            AppendByteBody(sb, data, s, cfg, hexFmt, rowPrefix: "  ");
            sb.Append("};");

            return sb.ToString();
        }

        // ── Plain C array ────────────────────────────────────────────────────

        private static string BuildPlainCArray(
            byte[] data, string name, SpriteState s, ExportSettings cfg, string hexFmt)
        {
            var sb = new StringBuilder();

            if (cfg.IncludeUsageComment)
            {
                sb.AppendLine($"// Use {name} as a {s.Width}×{s.Height} bitmap (MSB first)");
            }

            if (cfg.IncludeDimensionConstants)
            {
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_WIDTH  = {s.Width};");
                sb.AppendLine($"const uint8_t {name.ToUpperInvariant()}_HEIGHT = {s.Height};");
            }

            string arraySize = cfg.IncludeArraySize ? data.Length.ToString() : "";
            sb.AppendLine($"const uint8_t {name}[{arraySize}] = {{");
            AppendByteBody(sb, data, s, cfg, hexFmt, rowPrefix: "  ");
            sb.Append("};");

            return sb.ToString();
        }

        // ── MicroPython bytearray ────────────────────────────────────────────

        private static string BuildMicroPython(
            byte[] data, string name, SpriteState s, ExportSettings cfg, string hexFmt)
        {
            var sb = new StringBuilder();

            if (cfg.IncludeUsageComment)
            {
                sb.AppendLine($"# fb = framebuf.FrameBuffer({name}, {s.Width}, {s.Height}, framebuf.MONO_HLSB)");
            }

            if (cfg.IncludeDimensionConstants)
            {
                sb.AppendLine($"{name.ToUpperInvariant()}_WIDTH  = {s.Width}");
                sb.AppendLine($"{name.ToUpperInvariant()}_HEIGHT = {s.Height}");
            }

            sb.AppendLine($"{name} = bytearray([");
            AppendByteBody(sb, data, s, cfg, hexFmt, rowPrefix: "    ");
            sb.Append("])");

            return sb.ToString();
        }

        // ── Raw Hex ──────────────────────────────────────────────────────────

        private static string BuildRawHex(
            SpriteState state, ExportSettings cfg,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            string hexFmt)
        {
            var sb = new StringBuilder();
            int bytesPerRow = BytesPerRow(state.Width);
            string sep = cfg.UseCommaSeparator ? ", " : " ";

            for (int row = 0; row < state.Height; row++)
            {
                var line = new StringBuilder();
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    byte b = SampleByte(state, row, chunk, isFloating, floatingPixels,
                                        floatX, floatY, floatW, floatH, lsbFirst: false);
                    line.Append($"0x{b.ToString(hexFmt)}");
                    if (chunk < bytesPerRow - 1) line.Append(sep);
                }
                sb.AppendLine(line.ToString());
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ── Raw Binary ───────────────────────────────────────────────────────

        private static string BuildRawBinary(
            SpriteState state, ExportSettings cfg,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH)
        {
            var sb = new StringBuilder();
            int bytesPerRow = BytesPerRow(state.Width);
            string sep = cfg.UseCommaSeparator ? ", " : " ";

            for (int row = 0; row < state.Height; row++)
            {
                var line = new StringBuilder();
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    byte b = SampleByte(state, row, chunk, isFloating, floatingPixels,
                                        floatX, floatY, floatW, floatH, lsbFirst: false);
                    line.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
                    if (chunk < bytesPerRow - 1) line.Append(sep);
                }
                sb.AppendLine(line.ToString());
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Import / Parse
        // ═══════════════════════════════════════════════════════════════════════

        public void ParseAdafruitGfxToState(string code, SpriteState state)
        {
            // Extract the array body between { and } to avoid misreading dimension constants
            int start = code.IndexOf('{');
            int end = code.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                code = code.Substring(start, end - start);
            }
            ParseHexToState(code, state);
        }

        public void ParseHexToState(string hexText, SpriteState state)
        {
            string cleanText = Regex.Replace(hexText, @"//.*", "");
            cleanText = Regex.Replace(cleanText, @"/\*.*?\*/", "", RegexOptions.Singleline);

            var matches = Regex.Matches(cleanText, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = BytesPerRow(state.Width);
            int matchIndex = 0;

            Array.Clear(state.Pixels, 0, state.Pixels.Length);

            for (int row = 0; row < state.Height; row++)
            {
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    if (matchIndex >= matches.Count) return;

                    byte b = Convert.ToByte(matches[matchIndex].Groups[1].Value, 16);
                    matchIndex++;

                    for (int bit = 7; bit >= 0; bit--)
                    {
                        int col = (chunk * 8) + (7 - bit);
                        if (col < state.Width)
                            state.Pixels[(row * state.Width) + col] = ((b >> bit) & 1) == 1;
                    }
                }
            }
        }

        public void ParseXbmToState(string code, SpriteState state)
        {
            // Extract the array body between { and }
            int start = code.IndexOf('{');
            int end = code.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                code = code.Substring(start, end - start);
            }

            string cleanText = Regex.Replace(code, @"//.*", "");
            cleanText = Regex.Replace(cleanText, @"/\*.*?\*/", "", RegexOptions.Singleline);

            var matches = Regex.Matches(cleanText, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = BytesPerRow(state.Width);
            int matchIndex = 0;

            Array.Clear(state.Pixels, 0, state.Pixels.Length);

            for (int row = 0; row < state.Height; row++)
            {
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    if (matchIndex >= matches.Count) return;

                    byte b = Convert.ToByte(matches[matchIndex].Groups[1].Value, 16);
                    matchIndex++;

                    // XBM is LSB-first: bit 0 maps to the leftmost pixel
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        if (col < state.Width)
                            state.Pixels[(row * state.Width) + col] = ((b >> bit) & 1) == 1;
                    }
                }
            }
        }

        public void ParseBinaryToState(string text, SpriteState state)
        {
            Array.Clear(state.Pixels, 0, state.Pixels.Length);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;
            foreach (string line in lines)
            {
                if (row >= state.Height) break;
                string data = line.Contains(':') ? line[(line.IndexOf(':') + 1)..] : line;
                data = data.Replace(" ", "").Replace(",", "");
                for (int col = 0; col < state.Width; col++)
                    state.Pixels[(row * state.Width) + col] = col < data.Length && data[col] == '1';
                row++;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════════════

        // ── Byte extraction ───────────────────────────────────────────────────

        /// <summary>
        /// Builds the full byte array for the sprite.
        /// If <paramref name="lsbFirst"/> is true (XBM), each byte has its bits
        /// reversed so that the leftmost pixel maps to bit 0.
        /// Partial bytes (canvas width not a multiple of 8) are zero-padded.
        /// </summary>
        private static byte[] BuildByteArray(
            SpriteState state, bool lsbFirst,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH)
        {
            int bytesPerRow = BytesPerRow(state.Width);
            byte[] result = new byte[state.Height * bytesPerRow];

            for (int row = 0; row < state.Height; row++)
            {
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    byte b = SampleByte(state, row, chunk,
                                        isFloating, floatingPixels,
                                        floatX, floatY, floatW, floatH,
                                        lsbFirst);
                    result[(row * bytesPerRow) + chunk] = b;
                }
            }

            return result;
        }

        private static byte SampleByte(
            SpriteState state, int row, int chunk,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            bool lsbFirst)
        {
            byte b = 0;

            for (int bit = 0; bit < 8; bit++)
            {
                int col = (chunk * 8) + bit;
                bool isOn = col < state.Width && state.Pixels[(row * state.Width) + col];

                // Overlay floating selection pixels
                if (isFloating && floatingPixels != null && col < state.Width)
                {
                    int lx = col - floatX;
                    int ly = row - floatY;
                    if (lx >= 0 && lx < floatW && ly >= 0 && ly < floatH && floatingPixels[lx, ly])
                        isOn = true;
                }

                if (isOn)
                {
                    // MSB first: pixel at 'bit=0' is the most significant bit
                    // LSB first (XBM): pixel at 'bit=0' maps to bit 0
                    int shift = lsbFirst ? bit : (7 - bit);
                    b |= (byte)(1 << shift);
                }
            }

            return b;
        }

        // ── Body emitter ──────────────────────────────────────────────────────

        /// <summary>
        /// Writes the data bytes into <paramref name="sb"/>, respecting
        /// bytes-per-line chunking, row comments, and separator style.
        /// Each byte falls into exactly one of three cases:
        ///   – middle of a line: append byte + separator
        ///   – last byte on a line (more bytes follow): append byte + optional trailing comma + newline
        ///   – very last byte: append byte + newline (no trailing comma)
        /// This ensures the separator is emitted exactly once per gap.
        /// </summary>
        private static void AppendByteBody(
            StringBuilder sb, byte[] data, SpriteState s,
            ExportSettings cfg, string hexFmt, string rowPrefix)
        {
            int bytesPerRow = BytesPerRow(s.Width);
            bool useComma = cfg.UseCommaSeparator || 
                            !(cfg.Format == ExportFormat.RawHex || cfg.Format == ExportFormat.RawBinary);
            string sep     = useComma ? ", " : " ";
            int lineLen    = cfg.BytesPerLine <= 0 ? bytesPerRow : cfg.BytesPerLine;
            int totalBytes = data.Length;

            for (int i = 0; i < totalBytes; i++)
            {
                int posInRow   = i % bytesPerRow;     // position within the canvas row
                int rowIndex   = i / bytesPerRow;     // which canvas row we're in
                bool isFirstOnLine = posInRow % lineLen == 0;
                bool isLastByte    = i == totalBytes - 1;

                // A byte is the last on its output line when:
                //   • it is the last in a lineLen-chunk, OR
                //   • it is the last byte in its canvas row
                bool isLastOnLine = isLastByte
                    || ((posInRow + 1) % lineLen == 0)
                    || (posInRow + 1 == bytesPerRow);

                // Indent the start of each output line
                if (isFirstOnLine)
                    sb.Append(rowPrefix);

                sb.Append($"0x{data[i].ToString(hexFmt)}");

                if (isLastByte)
                {
                    // Very last byte — no trailing comma, optional comment, then newline
                    bool emitLastComment = cfg.IncludeRowComments
                        && (cfg.BytesPerLine <= 0 || posInRow + 1 == bytesPerRow);
                    if (emitLastComment)
                        sb.Append($"  // row {rowIndex}");
                    sb.AppendLine();
                }
                else if (isLastOnLine)
                {
                    // Last on this output line but more bytes follow
                    // Trailing comma keeps C/Python syntax valid for all-but-last rows
                    bool emitComment = cfg.IncludeRowComments
                        && (cfg.BytesPerLine <= 0 || posInRow + 1 == bytesPerRow);
                    string trail = useComma ? "," : "";
                    if (emitComment)
                        sb.Append($"{trail}  // row {rowIndex}");
                    else if (trail.Length > 0)
                        sb.Append(trail);
                    sb.AppendLine();
                }
                else
                {
                    // Middle of a line — just append the separator
                    sb.Append(sep);
                }
            }
        }


        // ── Name sanitiser ────────────────────────────────────────────────────

        /// <summary>
        /// Returns a valid C / Python identifier derived from <paramref name="name"/>.
        /// Strips invalid characters, prepends '_' if the result starts with a digit,
        /// and falls back to "sprite" if the result is empty.
        /// </summary>
        public static string SanitiseName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "sprite";

            // Replace spaces and invalid characters with underscores
            string safe = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_]", "_");

            // C identifiers cannot start with a digit
            if (safe.Length > 0 && char.IsDigit(safe[0]))
                safe = "_" + safe;

            return string.IsNullOrWhiteSpace(safe) ? "sprite" : safe;
        }

        // ── Misc ──────────────────────────────────────────────────────────────

        private static int BytesPerRow(int width) => (int)Math.Ceiling(width / 8.0);
    }
}
