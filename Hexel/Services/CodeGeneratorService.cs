using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hexel.Core;

namespace Hexel.Services
{
    public class CodeGeneratorService : ICodeGeneratorService
    {
        // ── Export ────────────────────────────────────────────────────────

        public (string Binary, string Hex) GenerateExportStrings(
            SpriteState state,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            bool binaryUseComma, bool hexUseComma)
        {
            var binBuilder = new StringBuilder();
            var hexBuilder = new StringBuilder();
            int bytesPerRow = (int)Math.Ceiling(state.Width / 8.0);

            for (int row = 0; row < state.Height; row++)
            {
                var rowBin = new StringBuilder();
                var rowHex = new StringBuilder();

                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    var byteStr = new StringBuilder(8);

                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        bool isPixelOn = col < state.Width && state.Pixels[(row * state.Width) + col];

                        // Overlay floating selection pixels if active
                        if (isFloating && floatingPixels != null && col < state.Width)
                        {
                            int lx = col - floatX;
                            int ly = row - floatY;
                            if (lx >= 0 && lx < floatW && ly >= 0 && ly < floatH && floatingPixels[lx, ly])
                                isPixelOn = true;
                        }

                        byteStr.Append(isPixelOn ? '1' : '0');
                    }

                    string bin = byteStr.ToString();
                    string hex = $"0x{Convert.ToInt32(bin, 2):X2}";
                    string binSep = binaryUseComma ? ", " : " ";
                    string hexSep = hexUseComma ? ", " : " ";

                    rowBin.Append(bin).Append(binSep);
                    rowHex.Append(hex).Append(hexSep);
                }

                binBuilder.AppendLine(rowBin.ToString().TrimEnd());
                hexBuilder.AppendLine(rowHex.ToString().TrimEnd());
            }

            return (
                binBuilder.ToString().TrimEnd('\r', '\n'),
                hexBuilder.ToString().TrimEnd('\r', '\n')
            );
        }

        public Task<(string Binary, string Hex)> GenerateExportStringsAsync(
            SpriteState state,
            bool isFloating, bool[,]? floatingPixels,
            int floatX, int floatY, int floatW, int floatH,
            bool binaryUseComma, bool hexUseComma)
        {
            return Task.Run(() => GenerateExportStrings(
                state, isFloating, floatingPixels,
                floatX, floatY, floatW, floatH,
                binaryUseComma, hexUseComma));
        }

        // ── Import ────────────────────────────────────────────────────────

        public void ParseBinaryToState(string binaryText, SpriteState state)
        {
            Array.Clear(state.Pixels, 0, state.Pixels.Length);
            string[] lines = binaryText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;

            foreach (string line in lines)
            {
                if (row >= state.Height) break;

                // Strip optional "row N:" prefix
                string data = line.Contains(':') ? line[(line.IndexOf(':') + 1)..] : line;
                data = data.Replace(" ", "").Replace(",", "");

                for (int col = 0; col < state.Width; col++)
                    state.Pixels[(row * state.Width) + col] = col < data.Length && data[col] == '1';

                row++;
            }
        }

        public void ParseHexToState(string hexText, SpriteState state)
        {
            var matches = Regex.Matches(hexText, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = (int)Math.Ceiling(state.Width / 8.0);
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
    }
}
