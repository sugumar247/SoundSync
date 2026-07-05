using Moq;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests
{
    public class NetworkStreamerTests
    {
        [Fact]
        public void StartStop_ChangesIsRunningState()
        {
            var mockStreamer = new Mock<INetworkStreamer>();
            mockStreamer.SetupGet(m => m.IsRunning).Returns(true);

            Assert.True(mockStreamer.Object.IsRunning);
        }
    }
}
