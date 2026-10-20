using System;

namespace PeerDesk.Windows
{
    // Diagnostics only: numeric facts of one failed native SendInput operation. Never holds key/button values, text,
    // coordinates, window handles or addresses. Win32Error is Marshal.GetLastWin32Error read by the very next statement
    // after SendInput on the same thread; 0 (or any value) is NOT proof of success or of UIPI, and may be stale.
    internal sealed class NativeInputFailure
    {
        internal readonly DiagNativeOp Op; internal readonly int Requested; internal readonly long Sent; internal readonly int Win32Error; internal readonly bool Captured;
        internal readonly bool CleanupAttempted; internal readonly int CleanupRequested; internal readonly long CleanupSent; internal readonly int CleanupWin32Error;
        internal NativeInputFailure(DiagNativeOp op,int requested,long sent,int win32Error,bool captured,bool cleanupAttempted=false,int cleanupRequested=0,long cleanupSent=0,int cleanupWin32Error=0)
        {
            Op=op; Requested=requested; Sent=sent; Win32Error=win32Error; Captured=captured;
            CleanupAttempted=cleanupAttempted; CleanupRequested=cleanupRequested; CleanupSent=cleanupSent; CleanupWin32Error=cleanupWin32Error;
        }
        // A native implementation without a diagnostic source: the result is unavailable (never a previous call's value).
        internal static NativeInputFailure Unavailable(DiagNativeOp op) { return new NativeInputFailure(op,1,-1,0,false); }
        internal DiagValue[] Fields()
        {
            var f=new System.Collections.Generic.List<DiagValue>(10){DiagValue.Of(DiagKey.Operation,Op),DiagValue.Of(DiagKey.Requested,(long)Requested),
                DiagValue.Of(DiagKey.Sent,Sent),DiagValue.Of(DiagKey.ErrorCaptured,Captured),DiagValue.Of(DiagKey.ErrorMayBeStale,true),DiagValue.Of(DiagKey.AuthoritativeCause,false)};
            if(Captured)f.Add(DiagValue.Of(DiagKey.Win32Error,(long)Win32Error));
            f.Add(DiagValue.Of(DiagKey.CleanupAttempted,CleanupAttempted));
            if(CleanupAttempted) { f.Add(DiagValue.Of(DiagKey.CleanupRequested,(long)CleanupRequested)); f.Add(DiagValue.Of(DiagKey.CleanupSent,CleanupSent)); f.Add(DiagValue.Of(DiagKey.CleanupWin32Error,(long)CleanupWin32Error)); }
            return f.ToArray();
        }
    }

    // Optional side interface of a stateful native implementation (INativeStatefulInput itself is unchanged).
    // TakeLastCall returns the requested/sent/error of the most recent call on this instance and clears it.
    internal interface INativeInputDiagnosticSource
    {
        bool TakeLastCall(out int requested,out long sent,out int win32Error);
    }

    // Test seam: null in production (real SendInput). Fakes return the sent count and the error they want recorded.
    internal delegate uint NativeSendOverride(int requested,out int win32Error);
}
