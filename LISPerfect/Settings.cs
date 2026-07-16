using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LISPerfect
{
    /// <summary>
    /// User-configurable settings, persisted to %APPDATA%\LISPerfect\settings.json.
    /// New settings: add a property with a default value, and they're automatically
    /// picked up. Missing fields in the file fall back to the property's default.
    /// </summary>
    public class Settings
    {
        // Font sizes (auto-persist on zoom changes)
        public double EditorFontSize { get; set; } = 14.0;
        public double ReplFontSize { get; set; } = 13.0;

        // Window geometry (auto-persist on close)
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double WindowWidth { get; set; } = 1100;
        public double WindowHeight { get; set; } = 700;
        public bool WindowMaximized { get; set; } = false;

        // Splitter position: width of the editor column as a fraction of total (0..1).
        // Stored as fraction so it survives window resizes.
        public double EditorColumnFraction { get; set; } = 2.0 / 3.0;

        // Editing behavior
        public bool ParedItEnabled { get; set; } = false;

        // Reserved for future use
        public string Theme { get; set; } = "default";

        // Auto-save
        public bool AutoSaveOnFocusLoss { get; set; } = false;
        public bool AutoSaveOnInterval { get; set; } = false;
        public int AutoSaveIntervalSeconds { get; set; } = 30;

        // Project tree
        public string? ProjectFolder { get; set; }
        public bool ShowOnlyLispFiles { get; set; } = false;
        public double ProjectTreeWidth { get; set; } = 220;
    }

    public static class SettingsManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LISPerfect");

        public static string SettingsFile =>
            Path.Combine(SettingsDir, "settings.json");

        /// <summary>Load settings from disk, or return defaults if none exist / file is corrupt.</summary>
        public static Settings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new Settings();
                string json = File.ReadAllText(SettingsFile);
                if (string.IsNullOrWhiteSpace(json)) return new Settings();
                var loaded = JsonSerializer.Deserialize<Settings>(json);
                return loaded ?? new Settings();
            }
            catch
            {
                return new Settings();
            }
        }

        public static Settings Load(out bool loadedCleanly)
        {
            loadedCleanly = true;
            try
            {
                if (!File.Exists(SettingsFile)) return new Settings();
                string json = File.ReadAllText(SettingsFile);
                if (string.IsNullOrWhiteSpace(json)) return new Settings();
                var loaded = JsonSerializer.Deserialize<Settings>(json);
                if (loaded == null)
                {
                    loadedCleanly = false;
                    System.Windows.MessageBox.Show(
                        "Settings file was empty or unrecognized. Defaults will be used, " +
                        "but your settings.json will not be overwritten this session.",
                        "LISPerfect settings",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return new Settings();
                }
                return loaded;
            }
            catch (Exception ex)
            {
                loadedCleanly = false;
                System.Windows.MessageBox.Show(
                    $"Settings file could not be read. Defaults will be used, " +
                    $"but your settings.json will not be overwritten this session.\n\n" +
                    $"Error: {ex.GetType().Name}: {ex.Message}",
                    "LISPerfect settings",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return new Settings();
            }
        }

        /// <summary>Save settings to disk. Silently succeeds or fails.</summary>
        public static void Save(Settings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* non-critical */ }
        }
    }
}