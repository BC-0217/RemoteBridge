using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    // Session-owned guard for stateful (down/up) remote input. One dedicated thread owns the named mutex, the held set and
    // every native SendInput of the stateful stream, so the watchdog can never race an apply. Any watchdog trip, Stop, Dispose
    // or native failure releases what this guard injected (best effort) and fails the guard permanently: there is no automatic
    // re-press and no revival of a held input after a timeout. A forced process kill/OS stop cannot be covered here.
    internal sealed class InputSafety : IDisposable
    {
        internal const string MutexName=@"Local\RemoteBridge.InputOwner";
        internal const int HoldTtlMs=750, ServerLeaseSafetyMs=750, WatchdogIntervalMs=25;
        private readonly BlockingCollection<Action> work=new BlockingCollection<Action>(new ConcurrentQueue<Action>());
        private readonly Func<bool> scopeValid;
        private readonly INativeStatefulInput native;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly HashSet<int> heldKeys=new HashSet<int>(), heldButtons=new HashSet<int>();
        private Thread thread; private long lastHeartbeatMs, lastLeaseMs; // read/written on the guard thread, except lease (Interlocked)
        private volatile string failure; private volatile bool disposed;
        internal bool Failed { get { return failure!=null; } }
        internal string Failure { get { return failure; } }
        internal int HeldCount { get; private set; } // updated on the guard thread; informational for other threads
        // Diagnostics only (never control): guard-thread scratch for the current RunAsync body, the random session correlation,
        // and immutable first/last failure snapshots. The original cause is published in memory (first only) before its failure
        // becomes visible or anything is released; last/log are emitted after the original release. No IO before release.
        private NativeInputFailure nativeFault; private DiagBodyCause bodyCause;
        private long diagCorrelation; private int rejectLogged, queuedRejectLogged;
        private InputGuardDiagnostic firstDiagnostic, lastDiagnostic;
        internal long DiagCorrelation { get { return Interlocked.Read(ref diagCorrelation); } set { Interlocked.Exchange(ref diagCorrelation,value); } }
        internal InputGuardDiagnostic FirstDiagnostic { get { return Volatile.Read(ref firstDiagnostic); } }
        internal InputGuardDiagnostic LastDiagnostic { get { return Volatile.Read(ref lastDiagnostic); } }

        internal InputSafety(Func<bool> scopeStillValid) : this(scopeStillValid,new NativeStatefulInput()) { }
        internal InputSafety(Func<bool> scopeStillValid,INativeStatefulInput nativeInput) { scopeValid=scopeStillValid; native=nativeInput; }

        // Acquires the per-interactive-session owner mutex on the guard thread. A second owner fails explicitly.
        internal void Start(string mutexName=MutexName)
        {
            if(thread!=null)throw new InvalidOperationException("input guard already started");
            var ready=new ManualResetEventSlim(false); Exception startError=null;
            thread=new Thread(()=>Run(mutexName,ready,e=>startError=e)) { IsBackground=true,Name="RemoteBridge.InputSafety" };
            thread.Start(); ready.Wait();
            if(startError!=null) { thread.Join(1000); throw startError; }
            Interlocked.Exchange(ref lastLeaseMs,clock.ElapsedMilliseconds);
        }
        private void Run(string mutexName,ManualResetEventSlim ready,Action<Exception> fail)
        {
            Mutex mutex=null; bool owned=false;
            try {
                try {
                    mutex=new Mutex(false,mutexName);
                    try { owned=mutex.WaitOne(0); } catch(AbandonedMutexException) { owned=true; } // previous owner died; we own it now
                    if(!owned)throw new InvalidOperationException("another RemoteBridge instance owns stateful remote input in this Windows session");
                } catch(Exception ex) { failure=ex.Message; fail(ex); ready.Set(); return; }
                ready.Set();
                while(true) {
                    Action a;
                    if(work.TryTake(out a,WatchdogIntervalMs)) { try { a(); } catch(Exception ex) { var s=SnapOnThread(DiagGuardCause.RunActionFailure); Publish(s); Trip("input guard error: "+ex.Message); Emit(s,ex); } }
                    else if(work.IsCompleted)break;
                    Watchdog();
                }
            } finally {
                ReleaseHeldOnThread();
                if(mutex!=null) { if(owned)try { mutex.ReleaseMutex(); } catch(ApplicationException) { } mutex.Dispose(); }
            }
        }
        private void Watchdog()
        {
            if(heldKeys.Count+heldButtons.Count==0)return;
            long now=clock.ElapsedMilliseconds;
            long heartbeatAge=now-lastHeartbeatMs;
            if(heartbeatAge>HoldTtlMs) { var s=SnapOnThread(DiagGuardCause.WatchdogHeartbeat,heartbeatAge,-1); Publish(s); Trip("controller hold heartbeat expired; held input released"); Emit(s); return; }
            long leaseAge=now-Interlocked.Read(ref lastLeaseMs);
            if(leaseAge>ServerLeaseSafetyMs) { var s=SnapOnThread(DiagGuardCause.WatchdogServerLease,heartbeatAge,leaseAge); Publish(s); Trip("server input lease too old; held input released"); Emit(s); return; }
            // The scope callback is evaluated exactly once; its result (false vs threw) is recorded, never re-evaluated.
            bool ok; DiagTri scopeResult; try { ok=scopeValid(); scopeResult=ok?DiagTri.True:DiagTri.False; } catch { ok=false; scopeResult=DiagTri.Threw; }
            if(!ok) { var s=SnapOnThread(scopeResult==DiagTri.Threw?DiagGuardCause.WatchdogScopeThrew:DiagGuardCause.WatchdogScopeFalse,heartbeatAge,leaseAge,scopeResult); Publish(s); Trip("capture scope or owned foreground lost; held input released"); Emit(s); }
        }
        private void Trip(string reason) { if(failure==null)failure=reason; ReleaseHeldOnThread(); }
        private void ReleaseHeldOnThread()
        {
            foreach(int vk in new List<int>(heldKeys)) { try { if(native.Key(vk,false))heldKeys.Remove(vk); } catch { } }
            foreach(int b in new List<int>(heldButtons)) { try { if(native.ButtonUpInPlace(b))heldButtons.Remove(b); } catch { } }
            // Best effort: anything that still failed is forgotten (never retried later as a surprise up/down).
            heldKeys.Clear(); heldButtons.Clear(); HeldCount=0;
        }

        // Fresh server lease observed (even with an empty batch). Never renews the controller hold heartbeat.
        internal void ObserveServerLease() { Interlocked.Exchange(ref lastLeaseMs,clock.ElapsedMilliseconds); }

        // Runs body on the guard thread. body returns false when a guard declined (nothing injected).
        internal Task<bool> RunAsync(Func<bool> body)
        {
            var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(disposed||failure!=null) {
                bool wasDisposed=disposed;
                tcs.TrySetException(new InvalidOperationException(failure??"input guard stopped"));
                if(Interlocked.Exchange(ref rejectLogged,1)==0)Emit(SnapOffThread(wasDisposed?DiagGuardCause.RejectedDisposed:DiagGuardCause.RejectedPreFailed,false));
                return tcs.Task;
            }
            Action a=()=>{
                if(failure!=null) { tcs.TrySetException(new InvalidOperationException(failure)); if(Interlocked.Exchange(ref queuedRejectLogged,1)==0)Emit(SnapOnThread(DiagGuardCause.QueuedPreFailed)); return; }
                nativeFault=null; bodyCause=DiagBodyCause.None;
                try { tcs.TrySetResult(body()); }
                catch(Exception ex) {
                    // Native = a SendInput result recorded on this thread by this body; anything else (context/cancel/validation) is body.
                    NativeInputFailure nf=nativeFault; DiagBodyCause bc=bodyCause; nativeFault=null; bodyCause=DiagBodyCause.None;
                    var s=SnapOnThread(nf!=null?DiagGuardCause.NativeFailure:DiagGuardCause.BodyFailure,-2,-2,DiagTri.NotEvaluated,nf,nf!=null?DiagBodyCause.None:bc==DiagBodyCause.None?DiagBodyCause.Other:bc);
                    Publish(s); Trip("native input failed: "+ex.Message); Emit(s,ex); tcs.TrySetException(ex);
                }
            };
            try { work.Add(a); } catch(InvalidOperationException) { tcs.TrySetException(new InvalidOperationException("input guard stopped")); Emit(SnapOffThread(DiagGuardCause.QueueClosed,false)); }
            return tcs.Task;
        }

        // ---- guard-thread-only operations (call from inside RunAsync bodies) ----
        internal bool KeyDown(int vk)
        {
            if(vk<1||vk>254||IsMouseVk(vk))throw new InvalidOperationException("invalid key");
            bool mine=heldKeys.Contains(vk);
            if(!mine&&native.IsPhysicallyDown(vk))return false; // locally held key: never claim it (we must not release it later)
            if(!native.Key(vk,true))throw NativeFail(DiagNativeOp.KeyDown,"SendInput keyDown failed");
            if(!mine)OnClaim(); heldKeys.Add(vk); HeldCount=heldKeys.Count+heldButtons.Count; // key repeat keeps a single entry
            return true;
        }
        internal bool KeyUp(int vk)
        {
            if(!heldKeys.Contains(vk))return false; // never release a key this guard did not press
            bool ok=native.Key(vk,false);
            if(!ok)throw NativeFail(DiagNativeOp.KeyUp,"SendInput keyUp failed");
            heldKeys.Remove(vk); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        internal bool ButtonDown(int button,int px,int py)
        {
            if(button<0||button>2)throw new InvalidOperationException("invalid button");
            if(heldButtons.Contains(button))return false;
            if(native.IsPhysicallyDown(ButtonVk(button)))return false;
            if(!native.ButtonDown(button,px,py))throw NativeFail(DiagNativeOp.ButtonDown,"SendInput mouseDown failed");
            OnClaim(); heldButtons.Add(button); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        // atPoint=false (point outside the owned scope): release without moving the pointer.
        internal bool ButtonUp(int button,int px,int py,bool atPoint)
        {
            if(!heldButtons.Contains(button))return false;
            bool ok=atPoint?native.ButtonUp(button,px,py):native.ButtonUpInPlace(button);
            if(!ok)throw NativeFail(atPoint?DiagNativeOp.ButtonUp:DiagNativeOp.ButtonUpInPlace,"SendInput mouseUp failed");
            heldButtons.Remove(button); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        internal bool Wheel(int delta,int px,int py)
        {
            if(delta==0||delta<-1200||delta>1200)throw new InvalidOperationException("invalid wheel delta");
            if(!native.Wheel(delta,px,py))throw NativeFail(DiagNativeOp.Wheel,"SendInput wheel failed");
            return true;
        }
        // Legacy taps/moves on a stateful stream also run here so every native apply is serialized with the watchdog.
        // The legacy SendInput result is consumed here, on the same thread, before the exception leaves the call.
        internal void Legacy(InputCommand c,int px,int py) { try { NativeInput.Apply(c,px,py); } catch { nativeFault=NativeInput.TakeFailure(); throw; } }
        // Called by a RunAsync body right before it throws for a non-native reason (diagnostics only).
        internal void NoteBodyCause(DiagBodyCause cause) { bodyCause=cause; }
        internal void Heartbeat() { if(heldKeys.Count+heldButtons.Count>0)lastHeartbeatMs=clock.ElapsedMilliseconds; } // no repress
        internal void ReleaseAllOnThread() { ReleaseHeldOnThread(); }
        internal bool HasHeldOnThread { get { return heldKeys.Count+heldButtons.Count>0; } }
        private void OnClaim() { if(heldKeys.Count+heldButtons.Count==0)lastHeartbeatMs=clock.ElapsedMilliseconds; }
        internal static bool IsMouseVk(int vk) { return vk==1||vk==2||vk==4||vk==5||vk==6; }
        private static int ButtonVk(int b) { return b==0?0x01:b==1?0x02:0x04; }

        // Releases held input and fails permanently (stale batch, fatal session error).
        internal Task FailAsync(string reason)
        {
            bool first=failure==null;
            // First cause published before the failure is visible (off-thread: held counts not observed); the guard-thread
            // snapshot with held counts follows as the explicit detail event.
            if(first)Publish(SnapOffThread(DiagGuardCause.FailAsync,true));
            if(failure==null)failure=reason;
            var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { work.Add(()=>{ var s=SnapOnThread(DiagGuardCause.FailAsync,-2,-2,DiagTri.NotEvaluated,null,DiagBodyCause.None,first); ReleaseHeldOnThread(); Emit(s); tcs.TrySetResult(true); }); }
            catch(InvalidOperationException) { tcs.TrySetResult(true); Emit(SnapOffThread(DiagGuardCause.FailAsyncQueueClosed,first)); }
            return tcs.Task;
        }
        public void Dispose()
        {
            if(disposed)return;
            bool first=failure==null;
            var s=SnapOffThread(DiagGuardCause.Dispose,first,true); Publish(s); // published before disposed=true so a racing RejectedDisposed cannot win
            disposed=true;
            if(failure==null)failure="input guard stopped";
            try { work.CompleteAdding(); } catch(ObjectDisposedException) { }
            var t=thread; if(t!=null&&t!=Thread.CurrentThread)t.Join(2000);
            Emit(s); // after the guard thread's original cleanup
        }

        // ---- diagnostics (never throw, never wait, never change failure/control state) ----
        private InvalidOperationException NativeFail(DiagNativeOp op,string message)
        {
            try {
                int requested,error; long sent; var source=native as INativeInputDiagnosticSource;
                nativeFault=source!=null&&source.TakeLastCall(out requested,out sent,out error)?new NativeInputFailure(op,requested,sent,error,true):NativeInputFailure.Unavailable(op);
            } catch { nativeFault=NativeInputFailure.Unavailable(op); }
            return new InvalidOperationException(message);
        }
        // Guard thread only: held collections are read here and nowhere else. -2 = compute ages now.
        private InputGuardDiagnostic SnapOnThread(DiagGuardCause cause,long heartbeatAge=-2,long leaseAge=-2,DiagTri scope=DiagTri.NotEvaluated,NativeInputFailure nf=null,DiagBodyCause body=DiagBodyCause.None,bool? first=null)
        {
            try {
                long now=clock.ElapsedMilliseconds;
                if(heartbeatAge==-2)heartbeatAge=now-lastHeartbeatMs;
                if(leaseAge==-2)leaseAge=now-Interlocked.Read(ref lastLeaseMs);
                return new InputGuardDiagnostic(cause,heldKeys.Count,heldButtons.Count,heartbeatAge,leaseAge,scope,first??failure==null,disposed,body,nf);
            } catch { return null; }
        }
        // Any other thread: held collections are not touched (-1 = not observed); only the lease age is atomic.
        private InputGuardDiagnostic SnapOffThread(DiagGuardCause cause,bool first,bool? disposedOverride=null)
        {
            try { return new InputGuardDiagnostic(cause,-1,-1,-1,clock.ElapsedMilliseconds-Interlocked.Read(ref lastLeaseMs),DiagTri.NotEvaluated,first,disposedOverride??disposed,DiagBodyCause.None,null); }
            catch { return null; }
        }
        // In memory only (no IO, no wait): the first published cause can never be replaced by a later rejection.
        private void Publish(InputGuardDiagnostic s) { if(s!=null)Interlocked.CompareExchange(ref firstDiagnostic,s,null); }
        private void Emit(InputGuardDiagnostic s,Exception ex=null)
        {
            try {
                if(s==null)return;
                Interlocked.CompareExchange(ref firstDiagnostic,s,null); Volatile.Write(ref lastDiagnostic,s);
                long cor=DiagCorrelation; DiagValue[] fields=s.Fields();
                if(ex!=null)DiagnosticLog.Failure(DiagEvent.InputGuardFailure,cor,ex,fields); else DiagnosticLog.Event(DiagEvent.InputGuardFailure,cor,fields);
                if(s.Native!=null) {
                    var nf=s.Native.Fields(); var all=new DiagValue[nf.Length+1]; all[0]=DiagValue.Of(DiagKey.Cause,s.Cause); Array.Copy(nf,0,all,1,nf.Length);
                    DiagnosticLog.Event(DiagEvent.InputNativeFailure,cor,all);
                }
            } catch { }
        }
    }

    // Immutable guard failure snapshot (numbers/enums only). HeldKeys/HeldButtons -1 = not observed off the guard thread.
    internal sealed class InputGuardDiagnostic
    {
        internal readonly DiagGuardCause Cause; internal readonly int HeldKeys, HeldButtons; internal readonly long HeartbeatAgeMs, ServerLeaseAgeMs;
        internal readonly DiagTri ScopeResult; internal readonly bool FirstFailure, Disposed; internal readonly DiagBodyCause BodyCause; internal readonly NativeInputFailure Native;
        internal InputGuardDiagnostic(DiagGuardCause cause,int heldKeys,int heldButtons,long heartbeatAgeMs,long serverLeaseAgeMs,DiagTri scope,bool first,bool disposed,DiagBodyCause body,NativeInputFailure native)
        {
            Cause=cause; HeldKeys=heldKeys; HeldButtons=heldButtons; HeartbeatAgeMs=heartbeatAgeMs; ServerLeaseAgeMs=serverLeaseAgeMs;
            ScopeResult=scope; FirstFailure=first; Disposed=disposed; BodyCause=body; Native=native;
        }
        internal DiagValue[] Fields()
        {
            return new[]{DiagValue.Of(DiagKey.Cause,Cause),DiagValue.Of(DiagKey.HeldKeys,(long)HeldKeys),DiagValue.Of(DiagKey.HeldButtons,(long)HeldButtons),
                DiagValue.Of(DiagKey.HeartbeatAgeMs,HeartbeatAgeMs),DiagValue.Of(DiagKey.ServerLeaseAgeMs,ServerLeaseAgeMs),DiagValue.Of(DiagKey.ScopeResult,ScopeResult),
                DiagValue.Of(DiagKey.FirstFailure,FirstFailure),DiagValue.Of(DiagKey.Disposed,Disposed),DiagValue.Of(DiagKey.BodyCause,BodyCause),DiagValue.Of(DiagKey.NativeFailure,Native!=null)};
        }
    }

    internal interface INativeStatefulInput
    {
        bool IsPhysicallyDown(int vk);
        bool Key(int vk,bool down);
        bool ButtonDown(int button,int px,int py);
        bool ButtonUp(int button,int px,int py);
        bool ButtonUpInPlace(int button);
        bool Wheel(int delta,int px,int py);
    }

    // Each call injects exactly one INPUT, so "sent==1" is the actual success (no partial batch to reconcile).
    internal sealed class NativeStatefulInput : INativeStatefulInput, INativeInputDiagnosticSource
    {
        // Last call on this instance (guard thread only). sendOverride is a test seam; null in production.
        private readonly NativeSendOverride sendOverride; private bool lastValid; private int lastRequested, lastError; private long lastSent;
        public NativeStatefulInput() { }
        internal NativeStatefulInput(NativeSendOverride testSend) { sendOverride=testSend; }
        bool INativeInputDiagnosticSource.TakeLastCall(out int requested,out long sent,out int win32Error)
        {
            requested=lastRequested; sent=lastSent; win32Error=lastError; bool valid=lastValid; lastValid=false; return valid;
        }
        private const uint INPUT_MOUSE=0,INPUT_KEYBOARD=1, MOUSEMOVE=0x0001, ABSOLUTE=0x8000, VIRTUALDESK=0x4000, WHEEL=0x0800, KEYUP=0x0002, EXTENDED=0x0001;
        private static readonly uint[] Down={0x0002,0x0008,0x0020}, Up={0x0004,0x0010,0x0040};
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION U; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
        [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint n,INPUT[] inputs,int cb);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code,uint mapType);
        public bool IsPhysicallyDown(int vk) { return (GetAsyncKeyState(vk)&0x8000)!=0; }
        public bool Key(int vk,bool down)
        {
            uint flags=(down?0:KEYUP)|(IsExtended(vk)?EXTENDED:0);
            return One(new INPUT{type=INPUT_KEYBOARD,U=new UNION{ki=new KEYBDINPUT{vk=(ushort)vk,scan=(ushort)MapVirtualKey((uint)vk,0),flags=flags}}});
        }
        public bool ButtonDown(int b,int px,int py) { return One(Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK|Down[b],0)); }
        public bool ButtonUp(int b,int px,int py) { return One(Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK|Up[b],0)); }
        public bool ButtonUpInPlace(int b) { return One(new INPUT{type=INPUT_MOUSE,U=new UNION{mi=new MOUSEINPUT{dwFlags=Up[b]}}}); }
        public bool Wheel(int delta,int px,int py) { return One(Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK|WHEEL,unchecked((uint)delta))); }
        private static bool IsExtended(int vk)
        {
            switch(vk) { case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x28: case 0x2D: case 0x2E:
                case 0x5B: case 0x5C: case 0x5D: case 0x6F: case 0x90: case 0xA3: case 0xA5: case 0x2C: return true; }
            return false;
        }
        private static INPUT Mouse(int x,int y,uint flags,uint data)
        {
            Rectangle v=SystemInformation.VirtualScreen; int dx=(int)((long)(x-v.Left)*65535/Math.Max(1,v.Width-1)),dy=(int)((long)(y-v.Top)*65535/Math.Max(1,v.Height-1));
            return new INPUT{type=INPUT_MOUSE,U=new UNION{mi=new MOUSEINPUT{dx=dx,dy=dy,mouseData=data,dwFlags=flags}}};
        }
        private bool One(INPUT i)
        {
            uint sent; int error; var fake=sendOverride;
            if(fake!=null)sent=fake(1,out error);
            else { sent=SendInput(1,new[]{i},Marshal.SizeOf(typeof(INPUT))); error=Marshal.GetLastWin32Error(); } // same thread, next statement; may be stale
            lastRequested=1; lastSent=sent; lastError=error; lastValid=true;
            return sent==1;
        }
    }
}
