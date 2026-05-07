using Hexprite.Services;
using Xunit;

namespace Hexprite.Tests;

public sealed class ImportFromCodeDetectorTests
{
    [Fact]
    public void TryParseExplicitDimensions_FromWidthHeightConstants()
    {
        const string code = """
            const uint8_t ICON_WIDTH  = 12;
            const uint8_t ICON_HEIGHT = 8;
            const uint8_t PROGMEM icon[] = { 0x00 };
            """;

        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(12, w);
        Assert.Equal(8, h);
        Assert.Equal("constants", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_FromAdafruitDrawBitmapComment()
    {
        const string code = "// display.drawBitmap(x, y, s, 9, 3)";
        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(9, w);
        Assert.Equal(3, h);
        Assert.Equal("usage comment", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_FromDrawXbmComment()
    {
        const string code = "// u8g2.drawXBM(x, y, 18, 4, bits)";
        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(18, w);
        Assert.Equal(4, h);
        Assert.Equal("usage comment", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_FromU8g2DrawBitmap_ComputesWidthFromBytesPerRow()
    {
        const string code = "// u8g2.drawBitmap(x, y, 2, 5, bmp)";
        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(16, w);
        Assert.Equal(5, h);
        Assert.Equal("usage comment", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_FromPlainCUsageComment()
    {
        const string code = "// Use bmp as a 7×11 bitmap (MSB first)";
        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(7, w);
        Assert.Equal(11, h);
        Assert.Equal("usage comment", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_FromFrameBufferComment()
    {
        const string code = "# fb = framebuf.FrameBuffer(sprite, 20, 6, framebuf.MONO_HLSB)";
        Assert.True(ImportFromCodeDetector.TryParseExplicitDimensions(code, out int w, out int h, out string src));
        Assert.Equal(20, w);
        Assert.Equal(6, h);
        Assert.Equal("usage comment", src);
    }

    [Fact]
    public void TryParseExplicitDimensions_NoMatch_ReturnsFalse()
    {
        const string code = "random text without hints";
        Assert.False(ImportFromCodeDetector.TryParseExplicitDimensions(code, out _, out _, out string src));
        Assert.Equal("", src);
    }

    [Fact]
    public void DetectVariableName_CArray_ReturnsIdentifier()
    {
        const string code = "static const uint8_t PROGMEM dinogame_icon[32] = {\n  0xFF,\n};";
        Assert.Equal("dinogame_icon", ImportFromCodeDetector.DetectVariableName(code));
    }

    [Fact]
    public void DetectVariableName_PythonBytearray_ReturnsIdentifier()
    {
        const string code = "my_sprite = bytearray([\n    0x80,\n])";
        Assert.Equal("my_sprite", ImportFromCodeDetector.DetectVariableName(code));
    }

    [Fact]
    public void DetectVariableName_NoDeclaration_ReturnsNull()
    {
        Assert.Null(ImportFromCodeDetector.DetectVariableName("0x01 0x02"));
    }

    [Fact]
    public void DetectBytesPerRow_FindsMostCommonHexCountPerDataLine()
    {
        string code =
            "const uint8_t x[] = {\n" +
            "  0x01, 0x02,\n" +
            "  0x03, 0x04,\n" +
            "};";
        Assert.Equal(2, ImportFromCodeDetector.DetectBytesPerRow(code));
    }

    [Fact]
    public void CountHexDataBytes_StripsComments()
    {
        const string code = """
            // 0xEE skipped (comment)
            0xAA /* 0xBB masked */
            0xCC
            """;
        Assert.Equal(2, ImportFromCodeDetector.CountHexDataBytes(code));
    }

    [Fact]
    public void TryInferDimensionsFromHexData_LineStructureTwoBytesPerRow_16WideCanvas()
    {
        string code =
            "const uint8_t x[] = {\n" +
            "  0xFF, 0xFF,\n" +
            "  0xFF, 0xFF,\n" +
            "};";
        Assert.True(ImportFromCodeDetector.TryInferDimensionsFromHexData(code, byteCount: 4, out int w, out int h,
            out ImportDimensionInferHint hint));
        Assert.Equal(16, w);
        Assert.Equal(2, h);
        Assert.Equal(ImportDimensionInferHint.FromLineStructure, hint);
    }

    [Fact]
    public void TryInferDimensionsFromHexData_AmbiguousLineStructure_FallsBackToByteCount()
    {
        // Two hex tokens per line, but total bytes (5) not divisible by 2 → ambiguous branch
        string code =
            "{\n" +
            "  0x01, 0x02,\n" +
            "  0x03, 0x04,\n" +
            "  0x05,\n" +
            "}";
        Assert.True(ImportFromCodeDetector.TryInferDimensionsFromHexData(code, byteCount: 5, out int w, out int h,
            out ImportDimensionInferHint hint));
        Assert.Equal(ImportDimensionInferHint.AmbiguousLineStructure, hint);
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    public void TryGuessDimensionsFromByteCount_PrefersSquareWhenPossible()
    {
        // 8×8 → 64 px → ceil(8/8)=1 byte/row → 64 rows… wrong logic

        // byteCount = H * ceil(W/8). For 8×8 mono: 8 rows * 1 byte = 8 bytes.
        Assert.True(ImportFromCodeDetector.TryGuessDimensionsFromByteCount(8, out int w, out int h));
        Assert.Equal(8, w);
        Assert.Equal(8, h);
    }

    [Theory]
    [InlineData("// drawXBM(", true)]
    [InlineData("XBM dump", true)]
    [InlineData("const uint8_t bmp[] = { 0x01 };", false)]
    public void IsLikelyXbmFormat(string fragment, bool expected) =>
        Assert.Equal(expected, ImportFromCodeDetector.IsLikelyXbmFormat(fragment));

    [Fact]
    public void ExpectedByteCount_MatchesCeilingBytesPerRow()
    {
        Assert.Equal(2 * 2, ImportFromCodeDetector.ExpectedByteCount(width: 9, height: 2)); // ceil(9/8)=2
        Assert.Equal(1 * 1, ImportFromCodeDetector.ExpectedByteCount(width: 8, height: 1));
    }
}
