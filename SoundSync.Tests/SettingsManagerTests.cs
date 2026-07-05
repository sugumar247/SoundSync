using Xunit;
using SoundSync.Models;

namespace SoundSync.Tests
{
    public class DeviceItemTests
    {
        [Fact]
        public void DelayChange_TriggersCallback()
        {
            bool callbackCalled = false;
            var item = new DeviceItem
            {
                DelayChangedCallback = () => callbackCalled = true
            };

            item.Delay = 100;

            Assert.True(callbackCalled);
            Assert.Equal(100, item.Delay);
        }
    }
}
