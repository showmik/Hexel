namespace Hexel.Services
{
    /// <summary>
    /// Manages application theme switching and persistence.
    /// </summary>
    public interface IThemeService
    {
        /// <summary>Gets the name of the currently applied theme ("Dark" or "Light").</summary>
        string CurrentTheme { get; }

        /// <summary>
        /// Switches the active theme by swapping the color ResourceDictionary.
        /// </summary>
        /// <param name="themeName">"Dark" or "Light"</param>
        void ApplyTheme(string themeName);

        /// <summary>Raised after the theme has been switched.</summary>
        event System.EventHandler? ThemeChanged;
    }
}
