using PeerDesk.Server;

const string InitializeDeviceStateFlag = "--initialize-device-state";

if (args.Length > 0 && args[0] == InitializeDeviceStateFlag)
{
    // Explicit one-shot initializer: creates a missing empty state file and exits.
    // Never overwrites; needs no TLS certificate or invitation.
    if (args.Length != 2 || !ServerOptions.IsValidDeviceStatePath(args[1]))
    {
        Console.Error.WriteLine($"Usage: {InitializeDeviceStateFlag} <absolute-path>");
        return 2;
    }
    try
    {
        DeviceRegistryStateStore.Initialize(args[1], ServerOptions.MaxDevicesLimit);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine("Device state initialization failed: " + ex.Message);
        return 1;
    }
    Console.WriteLine("Device state initialized.");
    return 0;
}

if (Array.IndexOf(args, InitializeDeviceStateFlag) >= 0)
{
    Console.Error.WriteLine($"{InitializeDeviceStateFlag} must be the first argument.");
    return 2;
}

// Production always persists the device registry; there is no ephemeral fallback.
var options = ServerOptions.FromEnvironment();
options.RequireDeviceStatePath();
var app = PeerDeskServerApp.BuildApp(args, options);
app.Run();
return 0;
