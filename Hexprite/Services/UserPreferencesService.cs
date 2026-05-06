using Hexprite.Core;
using System;
using System.IO;
using System.Text.Json;

namespace Hexprite.Services
{
    public sealed class UserPreferences
    {
        public int NewCanvasWidth { get; set; } = 16;
        public int NewCanvasHeight { get; set; } = 16;
        public int NewCanvasPresetIndex { get; set; } = 0;

        public int ResizePresetIndex { get; set; } = 0;
        public ResizeAnchor ResizeAnchor { get; set; } = ResizeAnchor.TopLeft;

        public int ImportFromCodeWidth { get; set; } = 16;
        public int ImportFromCodeHeight { get; set; } = 16;

        public ToolMode LastTool { get; set; } = ToolMode.Pencil;
        public bool ShowGridLines { get; set; } = true;
        public int BrushSize { get; set; } = 1;
        public BrushShape BrushShape { get; set; } = BrushShape.Circle;
        public int BrushAngle { get; set; } = 0;
        public int PreviewScale { get; set; } = 2;
        public int PreviewDisplayTypeIndex { get; set; } = 0;
        public bool UseRealisticPreview { get; set; } = false;
        public int PreviewRealismStrength { get; set; } = 65;
        public int PreviewQuality { get; set; } = 1;
    }

    public static class UserPreferencesService
    {
        private static readonly object Sync = new();
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexprite");
        private static readonly string SettingsFile =
            Path.Combine(SettingsDir, "user-preferences.json");

        private static UserPreferences? _cached;

        public static UserPreferences Get()
        {
            lock (Sync)
            {
                if (_cached != null)
                    return CloneAndNormalize(_cached);

                _cached = LoadFromDisk();
                return CloneAndNormalize(_cached);
            }
        }

        public static void Update(Action<UserPreferences> apply)
        {
            if (apply == null) return;

            lock (Sync)
            {
                _cached ??= LoadFromDisk();
                apply(_cached);
                _cached = Normalize(_cached);
                SaveToDisk(_cached);
            }
        }

        private static UserPreferences LoadFromDisk()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new UserPreferences();

                string json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<UserPreferences>(json);
                return Normalize(loaded ?? new UserPreferences());
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "UserPreferencesService.LoadFromDisk", new { SettingsFile });
                return new UserPreferences();
            }
        }

        private static void SaveToDisk(UserPreferences prefs)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "UserPreferencesService.SaveToDisk", new { SettingsFile });
            }
        }

        private static UserPreferences CloneAndNormalize(UserPreferences prefs)
        {
            return Normalize(new UserPreferences
            {
                NewCanvasWidth = prefs.NewCanvasWidth,
                NewCanvasHeight = prefs.NewCanvasHeight,
                NewCanvasPresetIndex = prefs.NewCanvasPresetIndex,
                ResizePresetIndex = prefs.ResizePresetIndex,
                ResizeAnchor = prefs.ResizeAnchor,
                ImportFromCodeWidth = prefs.ImportFromCodeWidth,
                ImportFromCodeHeight = prefs.ImportFromCodeHeight,
                LastTool = prefs.LastTool,
                ShowGridLines = prefs.ShowGridLines,
                BrushSize = prefs.BrushSize,
                BrushShape = prefs.BrushShape,
                BrushAngle = prefs.BrushAngle,
                PreviewScale = prefs.PreviewScale,
                PreviewDisplayTypeIndex = prefs.PreviewDisplayTypeIndex,
                UseRealisticPreview = prefs.UseRealisticPreview,
                PreviewRealismStrength = prefs.PreviewRealismStrength,
                PreviewQuality = prefs.PreviewQuality
            });
        }

        private static UserPreferences Normalize(UserPreferences prefs)
        {
            prefs.NewCanvasWidth = Math.Clamp(prefs.NewCanvasWidth, 1, SpriteState.MaxDimension);
            prefs.NewCanvasHeight = Math.Clamp(prefs.NewCanvasHeight, 1, SpriteState.MaxDimension);
            prefs.NewCanvasPresetIndex = Math.Max(0, prefs.NewCanvasPresetIndex);

            prefs.ResizePresetIndex = Math.Max(0, prefs.ResizePresetIndex);
            if (!Enum.IsDefined(typeof(ResizeAnchor), prefs.ResizeAnchor))
                prefs.ResizeAnchor = ResizeAnchor.TopLeft;

            prefs.ImportFromCodeWidth = Math.Clamp(prefs.ImportFromCodeWidth, 1, 256);
            prefs.ImportFromCodeHeight = Math.Clamp(prefs.ImportFromCodeHeight, 1, 256);

            prefs.BrushSize = Math.Clamp(prefs.BrushSize, 1, 64);
            if (!Enum.IsDefined(typeof(BrushShape), prefs.BrushShape))
                prefs.BrushShape = BrushShape.Circle;
            prefs.BrushAngle = ((prefs.BrushAngle % 360) + 360) % 360;
            prefs.PreviewScale = Math.Max(1, prefs.PreviewScale);
            prefs.PreviewDisplayTypeIndex = Math.Clamp(prefs.PreviewDisplayTypeIndex, 0, 4);
            prefs.PreviewRealismStrength = Math.Clamp(prefs.PreviewRealismStrength, 0, 100);
            prefs.PreviewQuality = Math.Clamp(prefs.PreviewQuality, 0, 2);
            if (!Enum.IsDefined(typeof(ToolMode), prefs.LastTool))
                prefs.LastTool = ToolMode.Pencil;

            return prefs;
        }
    }
}
