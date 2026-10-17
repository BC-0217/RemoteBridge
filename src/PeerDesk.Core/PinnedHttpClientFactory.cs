using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerDesk.Core;

public static class PinnedHttpClientFactory
{
    // expectedSha256Hex must be exactly 64 hex characters (case-insensitive)
    public static HttpClient CreateClient(string expectedSha256Hex)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256Hex) ||
            expectedSha256Hex.Trim().Length != 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                expectedSha256Hex.Trim(), "^[0-9a-fA-F]{64}$"))
        {
            throw new ArgumentException(
                "expectedSha256Hex must be exactly 64 hexadecimal characters.",
                nameof(expectedSha256Hex));
        }

        // Normalise once; comparison is constant-time over equal-length ASCII strings.
        string normalised = expectedSha256Hex.Trim().ToUpperInvariant();
        byte[] expectedBytes = System.Text.Encoding.ASCII.GetBytes(normalised);

        var handler = new HttpClientHandler
        {
            // No redirects – follow-redirect could bypass pin.
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslErrors) =>
            {
                // 1. Certificate must be present.
                if (cert == null) return false;

                // 2. There must be no hostname error.
                //    sslErrors is a flags enum; anything other than None is a failure
                //    UNLESS the only flag is RemoteCertificateChainErrors (self-signed)
                //    AND we still accept self-signed only when the DER hash matches.
                //    We reject any NameMismatch unconditionally.
                var withoutChain = sslErrors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
                if (withoutChain != System.Net.Security.SslPolicyErrors.None)
                    return false;

                // 3. Certificate UTC validity.
                var now = DateTimeOffset.UtcNow;
                if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
                    return false;

                // 4. Exact DER SHA-256 pin.
                byte[] hash = cert.GetCertHash(HashAlgorithmName.SHA256);
                string actualHex = Convert.ToHexString(hash).ToUpperInvariant();
                byte[] actualBytes = System.Text.Encoding.ASCII.GetBytes(actualHex);

                return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }
}
