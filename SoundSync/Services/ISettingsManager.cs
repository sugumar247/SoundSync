using SoundSync.Models;
using System.Collections.Generic;

namespace SoundSync.Services
{
    public interface ISettingsManager
    {
        List<SavedDeviceSettings> LoadProfile();
        void SaveProfile(List<SavedDeviceSettings> settings);
    }
}
