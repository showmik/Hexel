using System;
using System.IO;
using System.Windows;

namespace Hexprite.Services
{
    /// <summary>
    /// Switches themes at runtime by swapping the first MergedDictionary in
    /// <see cref="Application.Current.Resources"/>.
    /// Persists the user's choice to a simple settings file.
    /// </summary>
    public class ThemeService : IThemeService
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexprite");

        private static readonly string SettingsFile =
            Path.Combine(SettingsDir, "theme.txt");

        private string _currentTheme = "Dark";

        public string CurrentTheme => _currentTheme;

        public event EventHandler? ThemeChanged;

        /// <summary>
        /// Loads the persisted theme (if any) and applies it immediately.
        /// Call during <see cref="Application.OnStartup"/>.
        /// </summary>
        public void Initialize()
        {
            string saved = LoadSavedTheme();
            ApplyTheme(saved);
        }

        public void ApplyTheme(string themeName)
        {
            if (themeName != "Dark" && themeName != "Light" && themeName != "Dim")
                themeName = "Dark";

            var dict = new ResourceDictionary
            {
                Source = new Uri($"Themes/{themeName}.xaml", UriKind.Relative)
            };

            var mergedDicts = Application.Current.Resources.MergedDictionaries;

            // Convention: index 0 = color theme, index 1 = Styles.xaml
            if (mergedDicts.Count > 0)
                mergedDicts[0] = dict;
            else
                mergedDicts.Insert(0, dict);

            _currentTheme = themeName;
            PersistTheme(themeName);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Persistence ──────────────────────────────────────────────────

        private static string LoadSavedTheme()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string saved = File.ReadAllText(SettingsFile).Trim();
                    if (saved == "Dark" || saved == "Light" || saved == "Dim")
                        return saved;
                }
            }
            catch
            {
                // Swallow – fall through to default
            }
            return "Dark";
        }

        private static void PersistTheme(string themeName)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, themeName);
            }
            catch
            {
                // Non-critical — silently ignore
            }
        }
    }
}
