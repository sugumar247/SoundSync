using SoundSync.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SoundSync.Services
{
    public class SettingsManager : ISettingsManager
    {
        private readonly string _profilePath;

        public SettingsManager(string? customProfilePath = null)
        {
            _profilePath = customProfilePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundSync",
                "settings_profile.json"
            );

            try
            {
                string? directory = Path.GetDirectoryName(_profilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch { }
        }

        public List<SavedDeviceSettings> LoadProfile()
        {
            try
            {
                if (!File.Exists(_profilePath))
                {
                    return new List<SavedDeviceSettings>();
                }

                string json = File.ReadAllText(_profilePath);
                var savedData = JsonSerializer.Deserialize<List<SavedDeviceSettings>>(json);
                return savedData ?? new List<SavedDeviceSettings>();
            }
            catch
            {
                return new List<SavedDeviceSettings>();
            }
        }

        public void SaveProfile(List<SavedDeviceSettings> settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_profilePath, json);
            }
            catch
            {
                // Silently ignore saving errors as in original code
            }
        }
    }
}
