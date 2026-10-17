using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PeerDesk.Windows
{
    // New server format: device ID = 10 decimal digits (first 1-9); temporary password = 9 ASCII letters
    // with at least one upper and one lower case letter (case-sensitive, never normalized).
    // Legacy DPAPI load also accepts 32-hex device IDs and 16-hex passwords; server rotation replaces them.
    internal static class CredentialFormat
    {
        public static bool IsNewDeviceId(string value) { return value!=null&&value.Length==10&&Regex.IsMatch(value,"^[1-9][0-9]{9}$",RegexOptions.CultureInvariant); }
        public static bool IsLegacyDeviceId(string value) { return value!=null&&value.Length==32&&Regex.IsMatch(value,"^[0-9a-fA-F]{32}$",RegexOptions.CultureInvariant); }
        public static bool IsNewPassword(string value)
        {
            return value!=null&&value.Length==9&&Regex.IsMatch(value,"^[A-Za-z]{9}$",RegexOptions.CultureInvariant)&&
                Regex.IsMatch(value,"[A-Z]",RegexOptions.CultureInvariant)&&Regex.IsMatch(value,"[a-z]",RegexOptions.CultureInvariant);
        }
        public static bool IsLegacyPassword(string value) { return value!=null&&value.Length==16&&Regex.IsMatch(value,"^[0-9a-fA-F]{16}$",RegexOptions.CultureInvariant); }
        public static bool IsStoredDeviceId(string value) { return IsNewDeviceId(value)||IsLegacyDeviceId(value); }
        public static bool IsStoredPassword(string value) { return IsNewPassword(value)||IsLegacyPassword(value); }
        public static bool IsUnexpired(string expiresAt)
        {
            DateTimeOffset expires;
            return DateTimeOffset.TryParse(expiresAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expires)&&expires>DateTimeOffset.UtcNow;
        }
    }
}
