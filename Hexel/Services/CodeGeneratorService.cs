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
            int bytesPerRow = (int)Math.Ceiling(state.Width / 8.0);

            for (int row = 0; row < state.Height; row++)
            {
                var fullRowBinary = new StringBuilder();
                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    var byteString = new StringBuilder(8);
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        if (col < state.Width)
                        {
                            int index = (row * state.Width) + col;
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
                binBuilder.AppendLine(fullRowBinary.ToString().TrimEnd());
            }

            var hexFormatted = new StringBuilder();
            for (int i = 0; i < hexList.Count; i++)
            {
                hexFormatted.Append(hexList[i]);
                if (i < hexList.Count - 1) hexFormatted.Append(", ");
                if ((i + 1) % bytesPerRow == 0 && i < hexList.Count - 1) hexFormatted.AppendLine();
            }

            return (binBuilder.ToString().TrimEnd('\r', '\n'), hexFormatted.ToString());
        }

        public (string Binary, string Hex) GenerateExportStrings(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH, bool binaryUseComma, bool hexUseComma)
        {
            var binBuilder = new StringBuilder();
            var hexBuilder = new StringBuilder();
            int bytesPerRow = (int)Math.Ceiling(state.Width / 8.0);

            for (int row = 0; row < state.Height; row++)
            {
                var fullRowBinary = new StringBuilder();
                var fullRowHex = new StringBuilder();

                for (int chunk = 0; chunk < bytesPerRow; chunk++)
                {
                    var byteString = new StringBuilder(8);
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = (chunk * 8) + bit;
                        if (col < state.Width)
                        {
                            int index = (row * state.Width) + col;
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

                    string binChunk = byteString.ToString();
                    string hexChunk = $"0x{Convert.ToInt32(binChunk, 2):X2}";

                    fullRowBinary.Append(binChunk);
                    fullRowHex.Append(hexChunk);

                    if (binaryUseComma) fullRowBinary.Append(", "); else fullRowBinary.Append(" ");
                    if (hexUseComma) fullRowHex.Append(", "); else fullRowHex.Append(" ");
                }

                binBuilder.AppendLine(fullRowBinary.ToString().TrimEnd());
                hexBuilder.AppendLine(fullRowHex.ToString().TrimEnd());
            }

            return (binBuilder.ToString().TrimEnd('\r', '\n'), hexBuilder.ToString().TrimEnd('\r', '\n'));
        }

        public async System.Threading.Tasks.Task<(string Binary, string Hex)> GenerateExportStringsAsync(SpriteState state, bool isFloating, bool[,] floatingPixels, int floatX, int floatY, int floatW, int floatH, bool binaryUseComma, bool hexUseComma)
        {
            return await System.Threading.Tasks.Task.Run(() => GenerateExportStrings(state, isFloating, floatingPixels, floatX, floatY, floatW, floatH, binaryUseComma, hexUseComma));
        }

        public void ParseBinaryToState(string binaryText, SpriteState state)
        {
            Array.Clear(state.Pixels, 0, state.Pixels.Length);
            string[] lines = binaryText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int row = 0;

            foreach (string line in lines)
            {
                if (row >= state.Height) break;
                string dataPart = line.Contains(":") ? line.Substring(line.IndexOf(':') + 1) : line;
                dataPart = dataPart.Replace(" ", "").Replace(",", "");

                for (int col = 0; col < state.Width; col++)
                {
                    state.Pixels[(row * state.Width) + col] = (col < dataPart.Length) && (dataPart[col] == '1');
                }
                row++;
            }
        }

        public void ParseHexToState(string hexText, SpriteState state)
        {
            MatchCollection matches = Regex.Matches(hexText, @"0[xX]([0-9a-fA-F]{1,2})");
            int bytesPerRow = (int)Math.Ceiling(state.Width / 8.0);
            int matchIndex = 0;

            for (int row = 0; row < state.Height; row++)
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
                            if (col < state.Width)
                            {
                                state.Pixels[(row * state.Width) + col] = ((b >> bit) & 1) == 1;
                            }
                        }
                    }
                }
            }
        }
    }
}