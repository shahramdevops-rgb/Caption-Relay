using Xunit;

namespace CaptionRelay.Tests
{
    public class AppInfoTests
    {
        [Fact]
        public void Name_IsCaptionRelay()
        {
            Assert.Equal("CaptionRelay", AppInfo.Name);
        }

        [Fact]
        public void Version_ComesFromProjectFile()
        {
            Assert.Equal("0.1.0", AppInfo.Version);
        }
    }
}
