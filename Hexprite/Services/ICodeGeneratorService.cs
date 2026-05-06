using System.Threading.Tasks;
using Hexprite.Core;

namespace Hexprite.Services
{
    public interface ICodeGeneratorService
    {
        // ── Export ────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates formatted export code from the current sprite state.
        /// All options (format, name, structure flags) are taken from <paramref name="settings"/>.
        /// Layer semantics are resolved by the caller into <see cref="SpriteState.Pixels"/>
        /// before this method is invoked.
        /// </summary>
        string GenerateCode(
            SpriteState state,
            ExportSettings settings,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH);

        /// <summary>
        /// Async wrapper — runs generation on a thread-pool thread so the UI
        /// thread is never blocked, even for 256×256 canvases.
        /// </summary>
        Task<string> GenerateCodeAsync(
            SpriteState state,
            ExportSettings settings,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH);

        // ── Import ────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses an Adafruit GFX / plain C array block back into the canvas.
        /// Handles both <c>0xFF</c> and <c>0xff</c> hex literals.
        /// Parsed data is written to the active layer pixel buffer.
        /// </summary>
        void ParseAdafruitGfxToState(string code, SpriteState state);

        /// <summary>
        /// Parses raw hex output (space- or comma-separated 0xNN tokens)
        /// back into the canvas. Kept from original implementation.
        /// Parsed data is written to the active layer pixel buffer.
        /// </summary>
        void ParseHexToState(string hexText, SpriteState state);

        /// <summary>
        /// Parses an XBM array back into the canvas (LSB-first, bit-reversed).
        /// Parsed data is written to the active layer pixel buffer.
        /// </summary>
        void ParseXbmToState(string code, SpriteState state);

        /// <summary>
        /// Parses a raw binary string back into the canvas.
        /// Parsed data is written to the active layer pixel buffer.
        /// </summary>
        void ParseBinaryToState(string code, SpriteState state);
    }
}
