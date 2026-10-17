using System;

namespace PeerDesk.Windows
{
    internal enum PasswordRefreshAction { Keep, HideNewerElsewhere, Rotate }

    // Pure decision logic for the background password check. Never decides a forced rotation:
    // only server-signalled rotationRequired or server-time expiry may trigger a conditional rotate.
    internal static class PasswordRefreshCoordinator
    {
        internal static readonly TimeSpan PollInterval=TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan MaxBackoff=TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan TriggerDebounce=TimeSpan.FromSeconds(5);

        internal static PasswordRefreshAction Decide(bool hasLocalGeneration,long localGeneration,DeviceIdentity state)
        {
            if(state==null)throw new ArgumentNullException("state");
            if(state.RotationRequired||IsExpiredAtServer(state))return PasswordRefreshAction.Rotate;
            if(!hasLocalGeneration||state.PasswordGeneration!=localGeneration)return PasswordRefreshAction.HideNewerElsewhere;
            return PasswordRefreshAction.Keep;
        }

        internal static bool IsExpiredAtServer(DeviceIdentity state)
        {
            return state.PasswordExpiresAtUtc<=state.ServerTimeUtc;
        }

        internal static TimeSpan Backoff(int consecutiveFailures)
        {
            int shift=Math.Max(0,Math.Min(consecutiveFailures,5));
            long ticks=PollInterval.Ticks<<shift;
            return ticks>MaxBackoff.Ticks?MaxBackoff:TimeSpan.FromTicks(ticks);
        }
    }
}
