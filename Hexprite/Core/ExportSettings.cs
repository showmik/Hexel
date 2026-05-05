using System.Text.Json.Serialization;

namespace Hexprite.Core
{
    /// <summary>
    /// All user-configurable options that control how the export code is generated.
    /// Persisted inside the .Hexprite file so the last-used settings are remembered
    /// per document. All properties have sensible defaults.
    /// </summary>
    public class ExportSettings
    {
        // ── Format ────────────────────────────────────────────────────────────

        /// <summary>Target platform / library.</summary>
        public ExportFormat Format { get; set; } = ExportFormat.AdafruitGfx;

        // ── Naming ────────────────────────────────────────────────────────────

        /// <summary>
        /// User-defined variable / sprite name.
        /// Automatically sanitised to a valid C / Python identifier before use.
        /// Falls back to "sprite" when blank after sanitisation.
        /// </summary>
        public string SpriteName { get; set; } = "mySprite";

        // ── Code structure ────────────────────────────────────────────────────

        /// <summary>
        /// Emit a usage-comment above the array showing the appropriate
        /// display.drawBitmap / u8g2.drawBitmap call.
        /// </summary>
        public bool IncludeUsageComment { get; set; } = true;

        /// <summary>
        /// Emit <c>const uint8_t NAME_WIDTH = W;</c> and
        /// <c>const uint8_t NAME_HEIGHT = H;</c> before the array.
        /// </summary>
        public bool IncludeDimensionConstants { get; set; } = true;

        /// <summary>Bytes separated by ", " (true) or " " (false).</summary>
        public bool UseCommaSeparator { get; set; } = true;

        /// <summary>
        /// How many bytes appear on each line of the array body.
        /// 0 = match the canvas width in bytes (one row per line).
        /// </summary>
        public int BytesPerLine { get; set; } = 0;  // 0 = canvas width

        /// <summary>Emit hex digits in upper-case (0xFF) vs lower-case (0xff).</summary>
        public bool UppercaseHex { get; set; } = true;

        /// <summary>Append a <c>// row N</c> comment at the end of each data line.</summary>
        public bool IncludeRowComments { get; set; } = false;

        /// <summary>Include the byte count inside the array brackets (e.g. name[32]).</summary>
        public bool IncludeArraySize { get; set; } = false;
    }
}
