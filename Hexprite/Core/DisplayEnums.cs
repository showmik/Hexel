namespace Hexprite.Core
{
    /// <summary>
    /// OLED/embedded display types for preview rendering.
    /// </summary>
    public enum DisplayType
    {
        Generic_White,
        SSD1306_Blue,
        SSD1306_Green,
        ePaper,
        FlipperZero
    }

    /// <summary>
    /// Display simulation presets for realistic preview rendering.
    /// </summary>
    public enum DisplaySimulationPreset
    {
        Flat = 0,
        GenericLcd = 1,
        Ssd1306OledBlue = 2,
        Ssd1306OledGreen = 3,
        EPaper = 4,
    }

    /// <summary>
    /// Quality levels for preview simulation rendering.
    /// </summary>
    public enum PreviewQuality
    {
        Fast = 0,
        Balanced = 1,
        High = 2
    }
}
