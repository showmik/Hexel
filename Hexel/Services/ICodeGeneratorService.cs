using System.Threading.Tasks;
using Hexel.Core;

namespace Hexel.Services
{
    public interface ICodeGeneratorService
    {
        // ── Export ────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates formatted export code from the current sprite state.
        /// All options (format, name, structure flags) are taken from <paramref name="settings"/>.
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
        /// </summary>
        void ParseAdafruitGfxToState(string code, SpriteState state);

        /// <summary>
        /// Parses raw hex output (space- or comma-separated 0xNN tokens)
        /// back into the canvas. Kept from original implementation.
        /// </summary>
        void ParseHexToState(string hexText, SpriteState state);
    }
}
