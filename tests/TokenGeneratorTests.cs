using System.Text.RegularExpressions;
using CaptionRelay.Config;
using Xunit;

namespace CaptionRelay.Tests
{
    public class TokenGeneratorTests
    {
        [Fact]
        public void NewToken_Is43Base64UrlChars()
        {
            string token = TokenGenerator.NewToken();
            Assert.Equal(43, token.Length);
            Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), token);
        }

        [Fact]
        public void NewToken_IsDifferentEachTime()
        {
            Assert.NotEqual(TokenGenerator.NewToken(), TokenGenerator.NewToken());
        }

        [Fact]
        public void Fingerprint_IsStable8HexCharsAndDiffersPerToken()
        {
            string fp = TokenGenerator.Fingerprint("abc");
            Assert.Matches(new Regex("^[0-9a-f]{8}$"), fp);
            Assert.Equal(fp, TokenGenerator.Fingerprint("abc"));
            Assert.NotEqual(fp, TokenGenerator.Fingerprint("abd"));
        }
    }
}
