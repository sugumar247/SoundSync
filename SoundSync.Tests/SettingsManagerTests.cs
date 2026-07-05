using System.Collections.Generic;
using System.IO;
using SoundSync.Models;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests
{
    public class SettingsManagerTests
    {
        [Fact]
        public void SaveAndLoadProfile_WorksCorrectly()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                var manager = new SettingsManager(tempFile);
                var testSettings = new List<SavedDeviceSettings>
                {
                    new SavedDeviceSettings 
                    { 
                        DeviceId = "Device1", 
                        IsSelected = true, 
                        Volume = 0.8f, 
                        Delay = 10,
                        Bass = 1.0f,
                        Mid = 2.0f,
                        Treble = 3.0f
                    }
                };

                manager.SaveProfile(testSettings);
                var loaded = manager.LoadProfile();

                Assert.Single(loaded);
                Assert.Equal("Device1", loaded[0].DeviceId);
                Assert.True(loaded[0].IsSelected);
                Assert.Equal(0.8f, loaded[0].Volume);
                Assert.Equal(10, loaded[0].Delay);
                Assert.Equal(1.0f, loaded[0].Bass);
                Assert.Equal(2.0f, loaded[0].Mid);
                Assert.Equal(3.0f, loaded[0].Treble);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void LoadProfile_WhenFileDoesNotExist_ReturnsEmptyList()
        {
            string nonExistentPath = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString() + ".json");
            var manager = new SettingsManager(nonExistentPath);

            var result = manager.LoadProfile();

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void LoadProfile_WhenFileIsCorrupted_ReturnsEmptyList()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "{ invalid json }");
                var manager = new SettingsManager(tempFile);

                var result = manager.LoadProfile();

                Assert.NotNull(result);
                Assert.Empty(result);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}
