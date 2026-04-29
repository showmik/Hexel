using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Hexel.Core;

namespace Hexel.Services
{
    public class CodeGeneratorService : ICodeGeneratorService
    {
        public (string Binary, string Hex) GenerateExportStrings(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH)
        {
            var binBuilder = new StringBuilder();
            var hexList = new List<string>();
            int bytesPerRow = (int)Math.Ceiling(state.Size / 8.0);

            for (int row = 0; row < state.Size; row++)
            {
                var fullRowBinary = new StringBuilder();
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    var byteString = new StringBuilder(8);
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        if (col < state.Size)
                        {
                            int index = (row * state.Size) + col;
                            bool isPixelOn = state.Pixels[index];

                            if (isFloating)
                            {
                                int localX = col - floatX;
                                int localY = row - floatY;
                                if (localX >= 0 && localX < floatW && localY >= 0 && localY < floatH)
                                {
                                    if (floatingPixels[localX, localY]) isPixelOn = true;
                                }
                            }
                            byteString.Append(isPixelOn ? "1" : "0");
                        }
                        else
                        {
                            byteString.Append("0");
                        }
                    }
                    fullRowBinary.Append(byteString).Append(" ");
                    hexList.Add($"0x{Convert.ToInt32(byteString.ToString(), 2):X2}");
                }
                binBuilder.AppendLine($"// Row {row,2}: {fullRowBinary}");
            }

            var hexFormatted = new StringBuilder();
            for (int i = 0; i < hexList.Count; i++)
            {
                hexFormatted.Append(hexList[i]).Append(", ");
                if ((i + 1) % bytesPerRow == 0) hexFormatted.AppendLine().Append("  ");
            }

            string hexFinal = $"const unsigned char sprite_{state.Size}x{state.Size}[] PROGMEM = {{\n  " +
                              hexFormatted.ToString().TrimEnd(' ', ',', '\n', '\r') +
                              "\n};";

            return (binBuilder.ToString().TrimEnd('\r', '\n'), hexFinal);
        }

        public async System.Threading.Tasks.Task<(string Binary, string Hex)> GenerateExportStringsAsync(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH)
        {
            // Offload CPU-bound work to thread pool to avoid blocking UI thread
            return await System.Threading.Tasks.Task.Run(() => GenerateExportStrings(state, isFloating, floatingPixels, floatX, floatY, floatW, floatH));
        }

        public void ParseBinaryToState(string binaryText, SpriteState state)
        {
            string[] lines = binaryText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;

            foreach (string line in lines)
            {
                if (row >= state.Size) break;
                string dataPart = line.Contains(":") ? line.Substring(line.IndexOf(':') + 1) : line;
                dataPart = dataPart.Replace(" ", "");

                for (int col = 0; col < state.Size; col++)
                {
                    state.Pixels[(row * state.Size) + col] = (col < dataPart.Length) && (dataPart[col] == '1');
                }
                row++;
            }
        }

        public void ParseHexToState(string hexText, SpriteState state)
        {
            MatchCollection matches = Regex.Matches(hexText, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = (int)Math.Ceiling(state.Size / 8.0);
            int matchIndex = 0;

            for (int row = 0; row < state.Size; row++)
            {
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    if (matchIndex < matches.Count)
                    {
                        byte b = Convert.ToByte(matches[matchIndex].Groups[1].Value, 16);
                        matchIndex++;
                        for (int bit = 7; bit >= 0; bit--)
                        {
                            int col = (chunk * 8) + (7 - bit);
                            if (col < state.Size)
                            {
                                state.Pixels[(row * state.Size) + col] = ((b >> bit) & 1) == 1;
                            }
                        }
                    }
                }
            }
        }
    }
}