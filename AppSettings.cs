using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScanEzi
{
    public class AppSettings
    {
        public string? DefaultScannerId { get; set; }
        public string DefaultPaperSize { get; set; } = "A4";
        public bool DefaultDuplex { get; set; } = false;

        /// <summary>
        /// Remembers the sides choice per scanner, so a duplex feeder stays 2-sided while a
        /// simplex device stays 1-sided without the user re-picking every time.
        /// </summary>
        public Dictionary<string, bool> DuplexByScanner { get; set; } = new();
        public string DefaultSource { get; set; } = "Auto Detect";
        public int DefaultResolution { get; set; } = 300;
        public string DefaultFormat { get; set; } = "pdf";
        public int DefaultQuality { get; set; } = 90;

        /// <summary>Drops all-black (sensor miss) and all-white (blank sheet) pages from the export.</summary>
        public bool DiscardBlankPages { get; set; } = true;

        /// <summary>
        /// Uses the Windows OCR engine to spot a page fed upside down or sideways and turn it upright.
        /// Ignored when no OCR language is installed.
        /// </summary>
        public bool AutoOrientPages { get; set; } = true;

        public int ThumbnailWidth { get; set; } = 200;
        public int ThumbnailHeight { get; set; } = 150;
        public int MaxThumbnails { get; set; } = 10;

        /// <summary>Bumped when a default changes so existing installs pick up the new behaviour.</summary>
        public int Version { get; set; } = 1;

        private const int CurrentVersion = 2;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScanEzi",
            "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath).TrimStart('\uFEFF', '\u200B');
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    return Migrate(settings);
                }
            }
            catch { }
            return Migrate(new AppSettings());
        }

        private static AppSettings Migrate(AppSettings settings)
        {
            if (settings.Version < CurrentVersion)
            {
                // v2: PDF became the default output format.
                settings.DefaultFormat = "pdf";
                settings.Version = CurrentVersion;
                settings.Save();
            }

            return settings;
        }

        /// <summary>
        /// The sides choice to show for a device. An explicit per-device choice wins;
        /// otherwise a duplex-capable feeder starts out scanning both sides.
        /// </summary>
        public bool GetDuplex(string? scannerId, bool canDuplex)
        {
            if (!string.IsNullOrEmpty(scannerId) &&
                DuplexByScanner != null &&
                DuplexByScanner.TryGetValue(scannerId, out var saved))
            {
                return saved;
            }

            return canDuplex || DefaultDuplex;
        }

        public void SetDuplex(string? scannerId, bool duplex)
        {
            DefaultDuplex = duplex;

            if (string.IsNullOrEmpty(scannerId)) return;

            DuplexByScanner ??= new Dictionary<string, bool>();
            DuplexByScanner[scannerId] = duplex;
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}