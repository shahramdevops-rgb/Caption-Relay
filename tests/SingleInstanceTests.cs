using System;
using CaptionRelay.Infrastructure;
using Xunit;

namespace CaptionRelay.Tests
{
    public class SingleInstanceTests
    {
        [Fact]
        public void SecondInstance_IsNotFirst_UntilAllHandlesAreClosed()
        {
            string name = @"Local\CaptionRelay.Test." + Guid.NewGuid().ToString("N");

            var first = new SingleInstance(name);
            var second = new SingleInstance(name);
            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);

            first.Dispose();
            second.Dispose();

            using var third = new SingleInstance(name);
            Assert.True(third.IsFirst);
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            var instance = new SingleInstance(@"Local\CaptionRelay.Test." + Guid.NewGuid().ToString("N"));
            instance.Dispose();
            instance.Dispose();
        }
    }
}
