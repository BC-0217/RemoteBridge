using System.Globalization;
using PeerDesk.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace PeerDesk.Server;

public sealed class ServerOptions
{
    public const int MaxPortValue = 65535;
    public const int MaxSessionsLimit = 500;
    public const int MaxDevicesLimit = 1000;
    public const int MaxSessionTtlSeconds = 86400;

    public const string BindAddressVariable = "PEERDESK_BIND_ADDRESS";
    public const string PortVariable = "PEERDESK_PORT";
    public const string AllowPublicBindVariable = "PEERDESK_ALLOW_PUBLIC_BIND";
    public const string MaxSessionsVariable = "PEERDESK_MAX_SESSIONS";
    public const string MaxDevicesVariable = "PEERDESK_MAX_DEVICES";
    public const string SessionTtlSecondsVariable = "PEERDESK_SESSION_TTL_SECONDS";

    public string InvitationCode { get; set; } = string.Empty;
    public OwnerFeatureOptions? OwnerFeature { get; set; }
    public DirectoryFeatureOptions? DirectoryFeature { get; set; }

    public X509Certificate2? EphemeralCertificate { get; set; }

    public IClock Clock { get; set; } = SystemClock.Instance;

    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    public bool AllowPublicBind { get; set; }

    public int Port { get; set; }

    public int MaxDevices { get; set; } = 100;

    public int MaxSessions { get; set; } = 500;

    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromSeconds(60);

    public const string DeviceStatePathVariable = "PEERDESK_DEVICE_STATE_PATH";

    // Absolute path of the persisted device registry. Null means an ephemeral in-memory
    // registry (explicit test options only); Program requires a non-null path.
    public string? DeviceStatePath { get; set; }

    public void RequireDeviceStatePath()
    {
        if (DeviceStatePath is null)
            throw new InvalidOperationException($"{DeviceStatePathVariable} must be set to an absolute device state file path.");
        Validate();
    }

    public static bool IsValidDeviceStatePath(string? path) =>
        path is not null && path.Length > 0 && path.Length <= 4096 && !path.Any(char.IsControl) &&
        Path.IsPathFullyQualified(path) &&
        !path.EndsWith(Path.DirectorySeparatorChar) && !path.EndsWith(Path.AltDirectorySeparatorChar);

    public static ServerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var options = new ServerOptions();

        var bind = read(BindAddressVariable);
        if (bind is not null)
        {
            options.BindAddress = ParseIpLiteral(BindAddressVariable, bind);
        }

        var port = read(PortVariable);
        if (port is not null)
        {
            options.Port = ParseBoundedInt(PortVariable, port, 0, MaxPortValue);
        }

        var allowPublic = read(AllowPublicBindVariable);
        if (allowPublic is not null)
        {
            options.AllowPublicBind = allowPublic switch
            {
                "true" => true,
                "false" => false,
                _ => throw Invalid(AllowPublicBindVariable, "must be 'true' or 'false'"),
            };
        }

        var maxSessions = read(MaxSessionsVariable);
        if (maxSessions is not null)
        {
            options.MaxSessions = ParseBoundedInt(MaxSessionsVariable, maxSessions, 1, MaxSessionsLimit);
        }

        var maxDevices = read(MaxDevicesVariable);
        if (maxDevices is not null)
        {
            options.MaxDevices = ParseBoundedInt(MaxDevicesVariable, maxDevices, 1, MaxDevicesLimit);
        }

        var ttl = read(SessionTtlSecondsVariable);
        if (ttl is not null)
        {
            options.SessionTtl = TimeSpan.FromSeconds(
                ParseBoundedInt(SessionTtlSecondsVariable, ttl, 1, MaxSessionTtlSeconds));
        }

        var deviceStatePath = read(DeviceStatePathVariable);
        if (deviceStatePath is not null)
        {
            if (!IsValidDeviceStatePath(deviceStatePath))
                throw Invalid(DeviceStatePathVariable, "must be an absolute file path");
            options.DeviceStatePath = deviceStatePath;
        }

        var ownerConfig = read("PEERDESK_OWNER_CONFIG");
        if (ownerConfig is not null) options.OwnerFeature = OwnerFeatureOptions.Load(ownerConfig);
        var directoryConfig = read("PEERDESK_DIRECTORY_CONFIG");
        if (directoryConfig is not null) options.DirectoryFeature = DirectoryFeatureOptions.Load(directoryConfig);
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Clock is null)
        {
            throw new InvalidOperationException("ServerOptions.Clock must not be null.");
        }

        if (BindAddress is null)
        {
            throw new InvalidOperationException("ServerOptions.BindAddress must not be null.");
        }

        if (Port < 0 || Port > MaxPortValue)
        {
            throw new InvalidOperationException($"ServerOptions.Port must be between 0 and {MaxPortValue}.");
        }

        if (MaxSessions < 1 || MaxSessions > MaxSessionsLimit)
        {
            throw new InvalidOperationException($"ServerOptions.MaxSessions must be between 1 and {MaxSessionsLimit}.");
        }

        if (MaxDevices < 1 || MaxDevices > MaxDevicesLimit)
        {
            throw new InvalidOperationException($"ServerOptions.MaxDevices must be between 1 and {MaxDevicesLimit}.");
        }

        if (SessionTtl < TimeSpan.FromSeconds(1) || SessionTtl > TimeSpan.FromSeconds(MaxSessionTtlSeconds))
        {
            throw new InvalidOperationException($"ServerOptions.SessionTtl must be at least 1 second and at most {MaxSessionTtlSeconds} seconds.");
        }

        if (DeviceStatePath is not null && !IsValidDeviceStatePath(DeviceStatePath))
        {
            throw new InvalidOperationException("ServerOptions.DeviceStatePath must be an absolute file path.");
        }

        var address = BindAddress.IsIPv4MappedToIPv6 ? BindAddress.MapToIPv4() : BindAddress;
        if (IsMulticastOrBroadcast(address))
        {
            throw new InvalidOperationException("ServerOptions.BindAddress must not be a multicast or broadcast address.");
        }

        if (!IPAddress.IsLoopback(address))
        {
            if (!AllowPublicBind)
            {
                throw new InvalidOperationException("A non-loopback BindAddress requires AllowPublicBind to be true.");
            }

            if (Port == 0)
            {
                throw new InvalidOperationException("A non-loopback BindAddress requires an explicit non-zero Port.");
            }
        }
    }

    private static bool IsMulticastOrBroadcast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] >= 224 && bytes[0] <= 239)
            {
                return true;
            }

            return address.Equals(IPAddress.Broadcast);
        }

        return true;
    }

    private static IPAddress ParseIpLiteral(string name, string value)
    {
        if (value.Length == 0 || value.Length > 45)
        {
            throw Invalid(name, "must be a literal IPv4 or IPv6 address");
        }

        if (value.Contains(':'))
        {
            foreach (var c in value)
            {
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == ':' || c == '.';
                if (!ok)
                {
                    throw Invalid(name, "must be a literal IPv4 or IPv6 address");
                }
            }

            if (!IPAddress.TryParse(value, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw Invalid(name, "must be a literal IPv4 or IPv6 address");
            }

            return v6;
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            throw Invalid(name, "must be a dotted-quad IPv4 address or an IPv6 address");
        }

        var octets = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
            {
                throw Invalid(name, "must be a dotted-quad IPv4 address or an IPv6 address");
            }

            var octet = 0;
            foreach (var c in part)
            {
                if (c < '0' || c > '9')
                {
                    throw Invalid(name, "must be a dotted-quad IPv4 address or an IPv6 address");
                }

                octet = (octet * 10) + (c - '0');
            }

            if (octet > 255)
            {
                throw Invalid(name, "must be a dotted-quad IPv4 address or an IPv6 address");
            }

            octets[i] = (byte)octet;
        }

        return new IPAddress(octets);
    }

    private static int ParseBoundedInt(string name, string value, int min, int max)
    {
        if (value.Length == 0 || value.Length > 10)
        {
            throw Invalid(name, $"must be an unsigned decimal integer between {min} and {max}");
        }

        foreach (var c in value)
        {
            if (c < '0' || c > '9')
            {
                throw Invalid(name, $"must be an unsigned decimal integer between {min} and {max}");
            }
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            throw Invalid(name, $"must be an unsigned decimal integer between {min} and {max}");
        }

        return (int)parsed;
    }

    private static InvalidOperationException Invalid(string name, string requirement) =>
        new($"Environment variable {name} is invalid: it {requirement}.");
}
