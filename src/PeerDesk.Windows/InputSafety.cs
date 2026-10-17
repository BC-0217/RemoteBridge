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
                    if(work.TryTake(out a,WatchdogIntervalMs)) { try { a(); } catch(Exception ex) { Trip("input guard error: "+ex.Message); } }
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
            if(now-lastHeartbeatMs>HoldTtlMs) { Trip("controller hold heartbeat expired; held input released"); return; }
            if(now-Interlocked.Read(ref lastLeaseMs)>ServerLeaseSafetyMs) { Trip("server input lease too old; held input released"); return; }
            bool ok; try { ok=scopeValid(); } catch { ok=false; }
            if(!ok)Trip("capture scope or owned foreground lost; held input released");
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
            if(disposed||failure!=null) { tcs.TrySetException(new InvalidOperationException(failure??"input guard stopped")); return tcs.Task; }
            Action a=()=>{
                if(failure!=null) { tcs.TrySetException(new InvalidOperationException(failure)); return; }
                try { tcs.TrySetResult(body()); }
                catch(Exception ex) { Trip("native input failed: "+ex.Message); tcs.TrySetException(ex); }
            };
            try { work.Add(a); } catch(InvalidOperationException) { tcs.TrySetException(new InvalidOperationException("input guard stopped")); }
            return tcs.Task;
        }

        // ---- guard-thread-only operations (call from inside RunAsync bodies) ----
        internal bool KeyDown(int vk)
        {
            if(vk<1||vk>254||IsMouseVk(vk))throw new InvalidOperationException("invalid key");
            bool mine=heldKeys.Contains(vk);
            if(!mine&&native.IsPhysicallyDown(vk))return false; // locally held key: never claim it (we must not release it later)
            if(!native.Key(vk,true))throw new InvalidOperationException("SendInput keyDown failed");
            if(!mine)OnClaim(); heldKeys.Add(vk); HeldCount=heldKeys.Count+heldButtons.Count; // key repeat keeps a single entry
            return true;
        }
        internal bool KeyUp(int vk)
        {
            if(!heldKeys.Contains(vk))return false; // never release a key this guard did not press
            bool ok=native.Key(vk,false);
            if(!ok)throw new InvalidOperationException("SendInput keyUp failed");
            heldKeys.Remove(vk); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        internal bool ButtonDown(int button,int px,int py)
        {
            if(button<0||button>2)throw new InvalidOperationException("invalid button");
            if(heldButtons.Contains(button))return false;
            if(native.IsPhysicallyDown(ButtonVk(button)))return false;
            if(!native.ButtonDown(button,px,py))throw new InvalidOperationException("SendInput mouseDown failed");
            OnClaim(); heldButtons.Add(button); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        // atPoint=false (point outside the owned scope): release without moving the pointer.
        internal bool ButtonUp(int button,int px,int py,bool atPoint)
        {
            if(!heldButtons.Contains(button))return false;
            bool ok=atPoint?native.ButtonUp(button,px,py):native.ButtonUpInPlace(button);
            if(!ok)throw new InvalidOperationException("SendInput mouseUp failed");
            heldButtons.Remove(button); HeldCount=heldKeys.Count+heldButtons.Count;
            return true;
        }
        internal bool Wheel(int delta,int px,int py)
        {
            if(delta==0||delta<-1200||delta>1200)throw new InvalidOperationException("invalid wheel delta");
            if(!native.Wheel(delta,px,py))throw new InvalidOperationException("SendInput wheel failed");
            return true;
        }
        // Legacy taps/moves on a stateful stream also run here so every native apply is serialized with the watchdog.
        internal void Legacy(InputCommand c,int px,int py) { NativeInput.Apply(c,px,py); }
        internal void Heartbeat() { if(heldKeys.Count+heldButtons.Count>0)lastHeartbeatMs=clock.ElapsedMilliseconds; } // no repress
        internal void ReleaseAllOnThread() { ReleaseHeldOnThread(); }
        internal bool HasHeldOnThread { get { return heldKeys.Count+heldButtons.Count>0; } }
        private void OnClaim() { if(heldKeys.Count+heldButtons.Count==0)lastHeartbeatMs=clock.ElapsedMilliseconds; }
        internal static bool IsMouseVk(int vk) { return vk==1||vk==2||vk==4||vk==5||vk==6; }
        private static int ButtonVk(int b) { return b==0?0x01:b==1?0x02:0x04; }

        // Releases held input and fails permanently (stale batch, fatal session error).
        internal Task FailAsync(string reason)
        {
            if(failure==null)failure=reason;
            var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { work.Add(()=>{ ReleaseHeldOnThread(); tcs.TrySetResult(true); }); } catch(InvalidOperationException) { tcs.TrySetResult(true); }
            return tcs.Task;
        }
        public void Dispose()
        {
            if(disposed)return; disposed=true;
            if(failure==null)failure="input guard stopped";
            try { work.CompleteAdding(); } catch(ObjectDisposedException) { }
            var t=thread; if(t!=null&&t!=Thread.CurrentThread)t.Join(2000);
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
    internal sealed class NativeStatefulInput : INativeStatefulInput
    {
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
        private static bool One(INPUT i) { return SendInput(1,new[]{i},Marshal.SizeOf(typeof(INPUT)))==1; }
    }
}
