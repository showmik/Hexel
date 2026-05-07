using System.Text.RegularExpressions;
using Hexprite.Core;
using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public sealed partial class CodeGeneratorServiceTests
{
    private static byte[] OrderedHexLiterals(string code)
    {
        MatchCollection matches = HexLiteralRegex().Matches(code);
        var bytes = new byte[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            bytes[i] = Convert.ToByte(matches[i].Groups["h"].Value, 16);
        return bytes;
    }

    [GeneratedRegex(@"0[xX](?<h>[0-9a-fA-F]{2})")]
    private static partial Regex HexLiteralRegex();
    private static ExportSettings MinimalExport(Action<ExportSettings>? configure = null)
    {
        var s = new ExportSettings
        {
            Format = ExportFormat.AdafruitGfx,
            SpriteName = "testSprite",
            IncludeUsageComment = false,
            IncludeDimensionConstants = false,
            IncludeArraySize = false,
            UseCommaSeparator = true,
            BytesPerLine = 0,
            UppercaseHex = true,
            IncludeRowComments = false,
        };
        configure?.Invoke(s);
        return s;
    }

    private static void SetPixelsRowMajor(SpriteState state, ReadOnlySpan<bool> values)
    {
        Assert.Equal(state.Width * state.Height, values.Length);
        for (int i = 0; i < values.Length; i++)
            state.Pixels[i] = values[i];
    }

    private static bool[] ParseRawHexLines(string rawHexOutput, int width, int height)
    {
        var result = new bool[width * height];
        string[] lines = rawHexOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(height, lines.Length);
        int bytesPerRow = (int)Math.Ceiling(width / 8.0);

        for (int row = 0; row < height; row++)
        {
            string[] tokens = lines[row].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(bytesPerRow, tokens.Length);
            for (int chunk = 0; chunk < bytesPerRow; chunk++)
            {
                string t = tokens[chunk].Trim();
                Assert.StartsWith("0x", t, StringComparison.OrdinalIgnoreCase);
                byte b = Convert.ToByte(t[2..], 16);
                for (int bit = 7; bit >= 0; bit--)
                {
                    int col = (chunk * 8) + (7 - bit);
                    if (col < width)
                        result[(row * width) + col] = ((b >> bit) & 1) == 1;
                }
            }
        }

        return result;
    }

    [Theory]
    [InlineData("", "sprite")]
    [InlineData("   ", "sprite")]
    [InlineData("my sprite", "my_sprite")]
    [InlineData("123", "_123")]
    [InlineData("9abc", "_9abc")]
    [InlineData("___", "___")]
    [InlineData("!@#", "___")]
    public void SanitiseName_NormalisesIdentifiers(string? input, string expected)
    {
        Assert.Equal(expected, CodeGeneratorService.SanitiseName(input));
    }

    [Fact]
    public void SanitiseName_Null_ReturnsSprite()
    {
        Assert.Equal("sprite", CodeGeneratorService.SanitiseName(null));
    }

    [Fact]
    public void GenerateCode_UnknownFormat_ReturnsEmptyString()
    {
        var state = new SpriteState(8, 1);
        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = (ExportFormat)999 };
        Assert.Equal(string.Empty, svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(ExportFormat.AdafruitGfx, "PROGMEM", "0x80")]
    [InlineData(ExportFormat.U8g2DrawBitmap, "U8X8_PROGMEM", "0x80")]
    [InlineData(ExportFormat.U8g2DrawXBM, "PROGMEM", "0x01")]
    [InlineData(ExportFormat.PlainCArray, "const uint8_t testSprite[]", "0x80")]
    [InlineData(ExportFormat.MicroPython, "bytearray", "0x80")]
    public void GenerateCode_EachFormat_IncludesDistinctMarker(
        ExportFormat format, string expectedSubstring, string expectedFirstByte)
    {
        var state = new SpriteState(8, 1);
        state.Pixels[0] = true;
        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = format;
            s.SpriteName = "testSprite";
        });

        string code = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains(expectedSubstring, code);
        Assert.Contains(expectedFirstByte, code);
    }

    [Fact]
    public void GenerateCode_RawHex_RespectsSeparatorAndHexCase()
    {
        // Two byte columns so comma-separated output actually appears between chunks.
        var state = new SpriteState(9, 1);
        state.Pixels[0] = true;
        var svc = new CodeGeneratorService();

        var commaUpper = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true, UppercaseHex = true };
        Assert.Contains(", ", svc.GenerateCode(state, commaUpper, false, null, 0, 0, 0, 0));
        Assert.Contains("0x80", svc.GenerateCode(state, commaUpper, false, null, 0, 0, 0, 0));

        var spaceLower = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = false, UppercaseHex = false };
        string lower = svc.GenerateCode(state, spaceLower, false, null, 0, 0, 0, 0);
        Assert.Contains("0x80", lower);
        Assert.DoesNotContain("0X", lower);
    }

    [Fact]
    public void GenerateCode_RawBinary_MatchesPixelPattern()
    {
        var state = new SpriteState(4, 1);
        // MSB-first within the single byte: columns 0..3 map to bits 7..4
        state.Pixels[0] = true;
        state.Pixels[1] = false;
        state.Pixels[2] = true;
        state.Pixels[3] = false;
        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawBinary, UseCommaSeparator = false };
        string line = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Equal("10100000", line);
    }

    [Fact]
    public void GenerateCode_IncludeOptions_EmitExpectedStructure()
    {
        var state = new SpriteState(8, 1);
        var svc = new CodeGeneratorService();
        var settings = new ExportSettings
        {
            Format = ExportFormat.AdafruitGfx,
            SpriteName = "foo",
            IncludeUsageComment = true,
            IncludeDimensionConstants = true,
            IncludeArraySize = true,
            IncludeRowComments = true,
            BytesPerLine = 0,
            UseCommaSeparator = true,
            UppercaseHex = true,
        };

        string code = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains("display.drawBitmap", code);
        Assert.Contains("FOO_WIDTH", code);
        Assert.Contains("FOO_HEIGHT", code);
        Assert.Contains("foo[1]", code);
        Assert.Contains("// row 0", code);
    }

    [Fact]
    public void RoundTrip_AdafruitGfx_MsbFirst_PreservesPixels()
    {
        var original = new SpriteState(8, 2);
        SetPixelsRowMajor(original, new[]
        {
            true, false, true, false, false, false, false, false,
            false, false, false, false, false, false, false, true,
        });

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.AdafruitGfx;
            s.SpriteName = "rt";
        });
        string code = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        var roundTrip = new SpriteState(8, 2);
        svc.ParseAdafruitGfxToState(code, roundTrip);

        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public void RoundTrip_PlainCArray_ParseAdafruitGfx_PreservesPixels()
    {
        var original = new SpriteState(5, 1);
        SetPixelsRowMajor(original, new[] { true, true, false, true, false });

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s => s.Format = ExportFormat.PlainCArray);
        string code = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        var roundTrip = new SpriteState(5, 1);
        svc.ParseAdafruitGfxToState(code, roundTrip);

        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public void RoundTrip_U8g2DrawXbm_PreservesPixels()
    {
        var original = new SpriteState(8, 2);
        SetPixelsRowMajor(original, new[]
        {
            false, true, true, false, false, true, true, false,
            true, true, true, true, false, false, false, false,
        });

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.U8g2DrawXBM;
            s.SpriteName = "xbm_rt";
        });
        string code = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        var roundTrip = new SpriteState(8, 2);
        svc.ParseXbmToState(code, roundTrip);

        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public void RoundTrip_RawHex_MatchesDirectEncode()
    {
        var original = new SpriteState(8, 2);
        SetPixelsRowMajor(original, new[]
        {
            true, false, false, false, false, false, false, false,
            false, true, false, false, false, false, false, false,
        });

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true, UppercaseHex = true };
        string raw = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        bool[] decoded = ParseRawHexLines(raw, original.Width, original.Height);
        Assert.Equal(original.Pixels, decoded);
    }

    [Fact]
    public void RoundTrip_RawBinary_WithColumnPrefix_PreservesPixels()
    {
        var original = new SpriteState(3, 2);
        SetPixelsRowMajor(original, new[]
        {
            true, false, true,
            false, true, false,
        });

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawBinary, UseCommaSeparator = true };
        string raw = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);
        string[] rows = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        string withPrefix = $"0: {rows[0]}{Environment.NewLine}1: {rows[1]}";

        var restored = new SpriteState(3, 2);
        svc.ParseBinaryToState(withPrefix, restored);

        Assert.Equal(original.Pixels, restored.Pixels);
    }

    [Fact]
    public void ParseBinaryToState_NoRowPrefix_SpacesCommasSkippedBetweenDigits()
    {
        var state = new SpriteState(4, 2);
        var svc = new CodeGeneratorService();
        svc.ParseBinaryToState("1010 , 0\n 010  1", state);

        Assert.True(state.Pixels[0]); Assert.False(state.Pixels[1]); Assert.True(state.Pixels[2]); Assert.False(state.Pixels[3]);
        Assert.False(state.Pixels[4]); Assert.True(state.Pixels[5]); Assert.False(state.Pixels[6]); Assert.True(state.Pixels[7]);
    }

    [Fact]
    public void ParseBinaryToState_TruncatesWhenFewerBitsThanWidthPerRow()
    {
        var state = new SpriteState(8, 1);
        var svc = new CodeGeneratorService();
        svc.ParseBinaryToState("111", state);
        Assert.True(state.Pixels[0]); Assert.True(state.Pixels[1]); Assert.True(state.Pixels[2]);
        Assert.All(state.Pixels.AsSpan(3).ToArray(), Assert.False);
    }

    [Fact]
    public void RoundTrip_U8g2DrawBitmap_ParseAdafruitGfx_PreservesPixels()
    {
        var original = new SpriteState(8, 1);
        original.Pixels[0] = true;
        original.Pixels[7] = true;

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.U8g2DrawBitmap;
            s.SpriteName = "bmp_u8";
        });
        string code = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        var roundTrip = new SpriteState(8, 1);
        svc.ParseAdafruitGfxToState(code, roundTrip);
        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public void RoundTrip_MicroPython_ParseAdafruitGfx_PreservesPixels()
    {
        var original = new SpriteState(8, 1);
        SetPixelsRowMajor(original, new[] { false, true, false, true, false, true, false, true });

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.MicroPython;
            s.SpriteName = "pybits";
        });
        string code = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        var roundTrip = new SpriteState(8, 1);
        svc.ParseAdafruitGfxToState(code, roundTrip);
        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public void ParseAdafruitGfxToState_WithDimensionsDefined_ReadsOnlyBraceLiterals()
    {
        var state = new SpriteState(8, 1);
        var svc = new CodeGeneratorService();
        const string code =
            """
            #define ICON_WIDTH 8
            #define ICON_HEIGHT 1
            const uint8_t PROGMEM icon[] = { /* mask */ 0x81 };
            """;

        svc.ParseAdafruitGfxToState(code, state);

        Assert.True(state.Pixels[0]); Assert.False(state.Pixels[6]); Assert.True(state.Pixels[7]);
        Assert.All(state.Pixels.AsSpan(1, 6).ToArray(), Assert.False);
    }

    [Fact]
    public void ParseHexToState_StripsComments_AndHandlesCase()
    {
        var state = new SpriteState(8, 1);
        var svc = new CodeGeneratorService();
        string code = """
            // leading
            0xaA /* mid */ ,
            """; // MSB stripe: alternating pattern in first byte → 10101010
        svc.ParseHexToState(code, state);

        for (int col = 0; col < state.Width; col++)
            Assert.Equal(col % 2 == 0, state.Pixels[col]);
    }

    [Fact]
    public void ParseHexToState_PartialInput_StopsGracefullyWithoutThrowing()
    {
        var state = new SpriteState(16, 1);
        var svc = new CodeGeneratorService();
        svc.ParseHexToState("0x80", state);
        Assert.True(state.Pixels[0]);
        Assert.False(state.Pixels[15]);
    }

    [Fact]
    public void GenerateCode_Floating_OverridesBaseTransparentPixel()
    {
        var state = new SpriteState(8, 1);
        Array.Clear(state.Pixels, 0, state.Pixels.Length);
        bool[,] floatSel = new bool[2, 1];
        floatSel[0, 0] = false;
        floatSel[1, 0] = true;

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true };

        string code = svc.GenerateCode(state, settings, true, floatSel, 6, 0, 2, 1);
        Assert.Contains("0x01", code);
    }

    [Fact]
    public async Task GenerateCodeAsync_DoesNotMutateOriginalStatePixels()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        state.Layers.Add(new LayerState
        {
            Name = "L1",
            IsVisible = true,
            Pixels = new[] { false, false, false, false, false, false, false, false },
        });
        state.ActiveLayerIndex = 0;
        state.NormalizeLayerState();

        bool[] exported = new[] { true, false, false, false, false, false, false, false };
        state.Pixels = exported;
        var snapshotBefore = (bool[])exported.Clone();

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true };

        string code = await svc.GenerateCodeAsync(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains("0x80", code);

        Assert.True(ReferenceEquals(exported, state.Pixels));
        Assert.Equal(snapshotBefore, state.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void GenerateCode_BytesPerLine_DoesNotChangeEncodedByteSequence(int bytesPerLine)
    {
        var state = new SpriteState(24, 1);
        state.Pixels[0] = true;
        state.Pixels[16] = true;

        var reference = new byte[] { 0x80, 0x00, 0x80 };
        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.PlainCArray;
            s.BytesPerLine = bytesPerLine;
        });

        byte[] parsed = OrderedHexLiterals(svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0));
        Assert.Equal(reference, parsed);
    }

    [Fact]
    public void GenerateCode_IsDisplayInverted_DoesNotChangeOutput()
    {
        var state = new SpriteState(8, 1);
        state.Pixels[0] = true;
        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s => s.Format = ExportFormat.RawHex);

        string normal = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        state.IsDisplayInverted = true;
        string flagged = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Equal(normal, flagged);
    }

    [Fact]
    public void GenerateCode_StructuredFormats_IgnoresUseCommaToggle_StillCommaBetweenIntermediateBytes()
    {
        var state = new SpriteState(16, 1);
        state.Pixels[0] = true;
        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.PlainCArray;
            s.UseCommaSeparator = false;
        });

        string code = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains(", ", code);
        Assert.Contains("0x80,", code);
    }

    [Fact]
    public void GenerateCode_RawHex_UseCommaSeparatorFalse_UsesSpacesBetweenChunks()
    {
        var state = new SpriteState(9, 1);
        state.Pixels[0] = true;
        var svc = new CodeGeneratorService();
        var settings = new ExportSettings
        {
            Format = ExportFormat.RawHex,
            UseCommaSeparator = false,
            UppercaseHex = true,
        };

        string line = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Contains("0x80 0x00", line);
        Assert.DoesNotContain(", ", line);
    }

    [Fact]
    public void GenerateCode_FloatingPixelsIgnored_WhenNotFloating()
    {
        var state = new SpriteState(8, 1);
        Array.Clear(state.Pixels, 0, state.Pixels.Length);
        bool[,] floatSel = new bool[1, 1];
        floatSel[0, 0] = true;

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true };
        string code = svc.GenerateCode(state, settings, false, floatSel, 0, 0, 1, 1);
        Assert.Contains("0x00", code);
        Assert.DoesNotContain("0x80", code);
    }

    [Fact]
    public void GenerateCode_RowComments_AttachOnlyToEndOfCanvasRow_WhenBytesPerLineSplitsRow()
    {
        var state = new SpriteState(16, 2);
        state.Pixels[0] = true;
        state.Pixels[state.Width + 1] = true;

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s =>
        {
            s.Format = ExportFormat.PlainCArray;
            s.IncludeRowComments = true;
            s.BytesPerLine = 1;
        });

        string code = svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0);
        Assert.Single(Regex.Matches(code, @"// row 0\b"));
        Assert.Single(Regex.Matches(code, @"// row 1\b"));
    }

    [Fact]
    public void ParseHexToState_EmptyInput_LeavesCanvasClear()
    {
        var state = new SpriteState(8, 1);
        Array.Fill(state.Pixels, true);
        var svc = new CodeGeneratorService();
        svc.ParseHexToState("", state);
        Assert.All(state.Pixels, Assert.False);
    }

    [Fact]
    public void ParseHexToState_BlockCommentCanSpanLines()
    {
        var state = new SpriteState(8, 2);
        var svc = new CodeGeneratorService();
        string code =
            "0xAA/* line1\n" +
            "line2 */0x55";
        svc.ParseHexToState(code, state);

        // One byte consumed per raster row — second literal must not share the row striping test with the first byte.
        for (int c = 0; c < 8; c++)
            Assert.Equal(c % 2 == 0, state.Pixels[c]);
        for (int c = 0; c < 8; c++)
            Assert.Equal(c % 2 == 1, state.Pixels[8 + c]);
    }

    [Fact]
    public void ParseXbmToState_IgnoresPreambleOutsideBracedBody()
    {
        var original = new SpriteState(8, 1);
        original.Pixels[0] = true;
        original.Pixels[3] = true;

        var svc = new CodeGeneratorService();
        ExportSettings settings = MinimalExport(s => s.Format = ExportFormat.U8g2DrawXBM);
        string export = svc.GenerateCode(original, settings, false, null, 0, 0, 0, 0);

        string wrapped = "// noise 0xFF 0xFE (must not confuse import)\n#pragma once\n" + export;

        var roundTrip = new SpriteState(8, 1);
        svc.ParseXbmToState(wrapped, roundTrip);
        Assert.Equal(original.Pixels, roundTrip.Pixels);
    }

    [Fact]
    public async Task GenerateCodeAsync_LeavesActiveLayerBufferUnchanged_WhenProjectionDiffersFromLayer()
    {
        var state = new SpriteState(8, 1);
        state.Layers.Clear();
        bool[] layerBuf = new bool[8]; // stays all false while export uses overridden projection
        state.Layers.Add(new LayerState { Name = "L1", IsVisible = true, Pixels = layerBuf });
        state.ActiveLayerIndex = 0;
        state.NormalizeLayerState();

        bool[] exported = new[] { true, false, false, false, false, false, false, false };
        state.Pixels = exported;

        var svc = new CodeGeneratorService();
        var settings = new ExportSettings { Format = ExportFormat.RawHex, UseCommaSeparator = true };

        await svc.GenerateCodeAsync(state, settings, false, null, 0, 0, 0, 0);
        Assert.All(layerBuf, Assert.False);

        Assert.Contains("0x80", svc.GenerateCode(state, settings, false, null, 0, 0, 0, 0));
    }

    [Fact]
    public void SanitiseName_ReplacesUnicodeAndSymbolsWithUnderscores()
    {
        Assert.Equal("__a_b_c", CodeGeneratorService.SanitiseName("🙂a⚡b⋯c"));
        // 🙂 is UTF-16 surrogate pair → Regex.Replace substitutes two underscores
        Assert.Equal("sprite__", CodeGeneratorService.SanitiseName("sprite🙂"));
    }
}
