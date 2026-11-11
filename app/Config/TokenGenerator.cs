using System;
using System.Security.Cryptography;
using System.Text;

namespace CaptionRelay.Config
{
    public static class TokenGenerator
    {
        /// <summary>32 cryptographically random bytes as base64url (43 chars, no padding).</summary>
        public static string NewToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Short non-reversible id of a token (first 8 hex chars of SHA-256), safe to log so that
        /// app and extension tokens can be compared without revealing them.
        /// </summary>
        public static string Fingerprint(string token)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
    }
}
