using System.Security.Cryptography;

namespace PeerDesk.Server;

/// <summary>
/// Generates server-issued device IDs: exactly 10 decimal digits, first digit 1-9.
/// Each digit is drawn with the unbiased CSPRNG <see cref="RandomNumberGenerator.GetInt32(int, int)"/>.
/// </summary>
public static class DeviceIdGenerator
{
    public const int DeviceIdLength = 10;
    // Bounded collision retries; caller must insert with TryAdd (never overwrite).
    public const int MaxCollisionRetries = 16;

    public static string NewId()
    {
        var chars = new char[DeviceIdLength];
        chars[0] = (char)('0' + RandomNumberGenerator.GetInt32(1, 10));
        for (int i = 1; i < DeviceIdLength; i++)
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        return new string(chars);
    }

    public static bool IsValidFormat(string? s)
    {
        if (s is null || s.Length != DeviceIdLength)
            return false;
        if (s[0] < '1' || s[0] > '9')
            return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
                return false;
        }
        return true;
    }
}
