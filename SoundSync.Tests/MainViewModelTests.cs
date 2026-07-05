using Moq;
using SoundSync.Models;
using SoundSync.Services;
using SoundSync.ViewModels;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Xunit;

namespace SoundSync.Tests
{
    public class MainViewModelTests
    {
        private readonly Mock<IAudioEngine> _audioEngineMock = new Mock<IAudioEngine>();
        private readonly Mock<INetworkStreamer> _networkStreamerMock = new Mock<INetworkStreamer>();
        private readonly Mock<ISettingsManager> _settingsManagerMock = new Mock<ISettingsManager>();
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        private MainViewModel CreateViewModel()
        {
            return new MainViewModel(_audioEngineMock.Object, _networkStreamerMock.Object, _settingsManagerMock.Object, _dispatcher);
        }

        [Fact]
        public void DelayChange_InvokesAudioEngineUpdate()
        {
            // Arrange
            var vm = CreateViewModel();
            var device = new DeviceItem { Device = new NAudio.CoreAudioApi.MMDevice() };
            // Set callback to invoke the engine update as the real LoadDevices does
            device.DelayChangedCallback = () => _audioEngineMock.Object.UpdateRelativeDelays(vm.Devices.ToList());
            vm.Devices.Add(device);

            // Act
            device.Delay = 5; // Changing delay should trigger callback

            // Assert
            _audioEngineMock.Verify(a => a.UpdateRelativeDelays(It.IsAny<System.Collections.Generic.List<DeviceItem>>()), Times.Once);
        }

        [Fact]
        public void RefreshCommand_CanExecute_ReflectsConnectionState()
        {
            var vm = CreateViewModel();
            // initially not connected, command can execute
            Assert.True(vm.RefreshCommand.CanExecute(null));
            // simulate connection
            vm.IsConnected = true;
            Assert.False(vm.RefreshCommand.CanExecute(null));
        }
    }
}
