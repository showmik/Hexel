using Hexel.Core;

namespace Hexel.Services
{
    public interface ICodeGeneratorService
    {
        (string Binary, string Hex) GenerateExportStrings(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH);
        System.Threading.Tasks.Task<(string Binary, string Hex)> GenerateExportStringsAsync(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH);
        void ParseBinaryToState(string binaryText, SpriteState state);
        void ParseHexToState(string hexText, SpriteState state);
    }
}
