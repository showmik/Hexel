using System.Threading.Tasks;
using Hexel.Core;

namespace Hexel.Services
{
    public interface ICodeGeneratorService
    {
        /// <summary>
        /// Synchronously generates binary and hex export strings from the current sprite state.
        /// Prefer the async overload for any call made from the UI thread.
        /// </summary>
        (string Binary, string Hex) GenerateExportStrings(
            SpriteState state,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            bool binaryUseComma, bool hexUseComma);

        /// <summary>
        /// Async wrapper — runs the generation on a thread-pool thread so the UI
        /// thread is never blocked during export.
        /// </summary>
        Task<(string Binary, string Hex)> GenerateExportStringsAsync(
            SpriteState state,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            bool binaryUseComma, bool hexUseComma);

        void ParseBinaryToState(string binaryText, SpriteState state);
        void ParseHexToState(string hexText, SpriteState state);
    }
}
