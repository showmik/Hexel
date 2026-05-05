namespace Hexprite.Core
{
    /// <summary>
    /// Identifies the target platform/library for which code will be generated.
    /// </summary>
    public enum ExportFormat
    {
        /// <summary>const uint8_t PROGMEM name[] = {...}; — Adafruit GFX, MSB first</summary>
        AdafruitGfx,

        /// <summary>const uint8_t U8X8_PROGMEM name[] = {...}; — u8g2 drawBitmap, MSB first</summary>
        U8g2DrawBitmap,

        /// <summary>XBM layout: PROGMEM, LSB first (leftmost pixel → bit 0)</summary>
        U8g2DrawXBM,

        /// <summary>Plain C array, no PROGMEM — ESP32 / STM32 / Pico</summary>
        PlainCArray,

        /// <summary>MicroPython bytearray — framebuf.MONO_HLSB</summary>
        MicroPython,

        /// <summary>Raw hex values, one row per line, no wrapper</summary>
        RawHex,

        /// <summary>Raw binary strings, one row per line, no wrapper</summary>
        RawBinary,
    }
}
