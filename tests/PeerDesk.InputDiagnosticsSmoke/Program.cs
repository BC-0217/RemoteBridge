using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeerDesk.Windows;

// Input failure-branch observability. Synthetic only: no desktop capture, no real SendInput (fake natives and the
// per-thread/per-instance NativeSendOverride test seam), private mutex names, isolated temporary diagnostic log directory.
internal static class Program
{
    // Stateful fake with a diagnostic source: records the requested/sent/error of each call like NativeStatefulInput.
    sealed class DiagFake : INativeStatefulInput, INativeInputDiagnosticSource
    {
        internal readonly List<string> Log=new List<string>(); internal int FailNext; internal int FailError=5;
        internal ManualResetEventSlim BlockUp, UpEntered; // finite gate on key-up (cleanup) calls; null = never blocks
        bool valid; long sent; int error;
        bool Ok(string s) { lock(Log)Log.Add(s); bool fail=FailNext>0; if(fail)FailNext--; sent=fail?0:1; error=fail?FailError:0; valid=true; return !fail; }
        public bool IsPhysicallyDown(int vk) { return false; }
        public bool Key(int vk,bool down) { if(!down&&BlockUp!=null) { UpEntered.Set(); BlockUp.Wait(5000); } return Ok((down?"kd":"ku")+vk); }
        public bool ButtonDown(int b,int x,int y) { return Ok("bd"+b); }
        public bool ButtonUp(int b,int x,int y) { return Ok("bu"+b); }
        public bool ButtonUpInPlace(int b) { return Ok("bup"+b); }
        public bool Wheel(int d,int x,int y) { return Ok("w"+d); }
        public bool TakeLastCall(out int requested,out long s,out int e) { requested=1; s=sent; e=error; bool v=valid; valid=false; return v; }
        internal int Count(string s) { lock(Log)return Log.FindAll(x=>x==s).Count; }
    }
    // No diagnostic source: native results must be reported Unavailable, never reused from an earlier call.
    sealed class PlainFake : INativeStatefulInput
    {
        internal bool FailNext; internal int Calls;
        bool Ok() { Calls++; if(FailNext){FailNext=false;return false;} return true; }
        public bool IsPhysicallyDown(int vk) { return false; }
        public bool Key(int vk,bool down) { return Ok(); }
        public bool ButtonDown(int b,int x,int y) { return Ok(); }
        public bool ButtonUp(int b,int x,int y) { return Ok(); }
        public bool ButtonUpInPlace(int b) { return Ok(); }
        public bool Wheel(int d,int x,int y) { return Ok(); }
    }
    // Scripted SendInput results: (sent, error) per call; also records requested counts. Never calls the OS.
    sealed class Script
    {
        readonly Queue<KeyValuePair<uint,int>> results=new Queue<KeyValuePair<uint,int>>(); internal readonly List<int> Requested=new List<int>();
        internal Script(params uint[] sentErrorPairs) { for(int i=0;i+1<sentErrorPairs.Length;i+=2)results.Enqueue(new KeyValuePair<uint,int>(sentErrorPairs[i],(int)sentErrorPairs[i+1])); }
        internal uint Send(int requested,out int error)
        {
            lock(Requested)Requested.Add(requested);
            if(results.Count==0) { error=0; return (uint)requested; }
            var r=results.Dequeue(); error=r.Value; return r.Key;
        }
    }

    const string Sentinel="SENTINEL_9f3c_TEXT"; const int SentinelX=31337, SentinelY=27182; const long Cor=0x1234abcd5678ef01;
    static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks, failures;
    static void Check(bool ok,string name) { checks++; Console.WriteLine((ok?"PASS ":"FAIL ")+name); if(!ok)failures++; }
    static string Name() { return @"Local\RemoteBridge.InputDiagSmoke."+Guid.NewGuid().ToString("N"); }
    static InputSafety Guard(INativeStatefulInput n,Func<bool> scope=null) { var g=new InputSafety(scope??(()=>true),n); g.DiagCorrelation=Cor; g.Start(Name()); return g; }
    static Exception Fault(Task t) { try { t.GetAwaiter().GetResult(); return null; } catch(Exception ex) { return ex; } }
    static Exception Fault(Action a) { try { a(); return null; } catch(Exception ex) { return ex; } }
    static bool Wait(Func<bool> c,int ms) { var w=Stopwatch.StartNew(); while(!c()&&w.ElapsedMilliseconds<ms)Thread.Sleep(10); return c(); }
    static InputCommand Cmd(string kind,string text=null) { return new InputCommand{Kind=kind,Generation=7,Sequence=1,X=SentinelX,Y=SentinelY,Button=0,KeyCode=0x41,Text=text}; }

    static int Main()
    {
        var total=Stopwatch.StartNew();
        string logDir=Path.Combine(Path.GetTempPath(),"remotebridge-inputdiag-"+Guid.NewGuid().ToString("N"));
        DiagnosticLog.Initialize(logDir,1024*1024);
        try {
            StatefulNative(); Watchdog(); GuardControl(); ApplyStateful(); AuthorityState(); LegacyNative(); LegacyThroughGuard();
            FirstCauseRace(); DisposeOrder(); QueueClosed(); Workers();
            DiagnosticLog.Shutdown(3000);
            LogFile(logDir);
        } catch(Exception ex) { Console.WriteLine("FAIL unexpected "+ex.GetType().Name+": "+ex.Message); failures++; }
        finally { try { Directory.Delete(logDir,true); } catch { } }
        Console.WriteLine((failures==0?"INPUT_DIAGNOSTICS_SMOKE_PASS":"INPUT_DIAGNOSTICS_SMOKE_FAIL failures="+failures)+" checks="+checks+" elapsedMs="+total.ElapsedMilliseconds+" synthetic=true nativeInput=false");
        return failures==0?0:1;
    }

    static void StatefulNative()
    {
        // down failure: original exception/message and guard failure text unchanged; native snapshot is the failing call.
        var n=new DiagFake{FailNext=1,FailError=5}; var g=Guard(n);
        var ex=Fault(g.RunAsync(()=>g.KeyDown(65)));
        var s=g.LastDiagnostic;
        Check(ex is InvalidOperationException&&ex.Message=="SendInput keyDown failed"&&g.Failure=="native input failed: SendInput keyDown failed"&&g.HeldCount==0,"keyDown native failure preserves exception, message and guard failure");
        Check(s!=null&&s.Cause==DiagGuardCause.NativeFailure&&s.BodyCause==DiagBodyCause.None&&s.Native!=null&&s.Native.Op==DiagNativeOp.KeyDown&&s.Native.Requested==1&&s.Native.Sent==0&&s.Native.Win32Error==5&&s.Native.Captured&&s.FirstFailure,
            "keyDown failure classified Native with requested/sent/error from the failing call");
        g.Dispose();
        // up failure with a held key: held snapshot taken before release; cleanup retry happens after (original ownership retained).
        n=new DiagFake(); g=Guard(n);
        g.RunAsync(()=>g.KeyDown(66)).Wait(); n.FailNext=1; n.FailError=1450;
        ex=Fault(g.RunAsync(()=>g.KeyUp(66))); s=g.LastDiagnostic;
        Check(ex!=null&&ex.Message=="SendInput keyUp failed"&&n.Count("ku66")==2&&g.Failed&&g.HeldCount==0,"keyUp failure: cleanup retry released after original failure");
        Check(s.Cause==DiagGuardCause.NativeFailure&&s.Native.Op==DiagNativeOp.KeyUp&&s.Native.Win32Error==1450&&s.HeldKeys==1&&s.HeldButtons==0,
            "keyUp snapshot holds the original error (not the cleanup call) and pre-release held count");
        g.Dispose();
        // mouse up in place and wheel ops are distinct
        n=new DiagFake(); g=Guard(n);
        g.RunAsync(()=>g.ButtonDown(0,1,1)).Wait(); n.FailNext=1;
        Fault(g.RunAsync(()=>g.ButtonUp(0,SentinelX,SentinelY,false)));
        Check(g.LastDiagnostic.Native.Op==DiagNativeOp.ButtonUpInPlace&&g.LastDiagnostic.HeldButtons==1,"mouseUp in place op recorded");
        g.Dispose();
        // native without a diagnostic source: Unavailable, never a previous value
        var p=new PlainFake(); g=Guard(p); p.FailNext=true;
        Fault(g.RunAsync(()=>g.Wheel(120,1,1))); s=g.LastDiagnostic;
        Check(s.Cause==DiagGuardCause.NativeFailure&&s.Native.Op==DiagNativeOp.Wheel&&!s.Native.Captured&&s.Native.Sent==-1,"fake native without source reports Unavailable");
        g.Dispose();
        // real NativeStatefulInput.One through the instance test seam: success then failure; failure values are exact, not stale.
        var script=new Script(1,0, 0,87);
        var real=new NativeStatefulInput(script.Send); g=Guard(real);
        Check(g.RunAsync(()=>g.KeyDown(0x87)).Result&&g.HeldCount==1,"seamed NativeStatefulInput success claims key");
        ex=Fault(g.RunAsync(()=>g.KeyUp(0x87))); s=g.LastDiagnostic;
        Check(ex!=null&&s.Native.Captured&&s.Native.Sent==0&&s.Native.Win32Error==87&&s.Native.Requested==1&&script.Requested.Count==3,
            "NativeStatefulInput instance snapshot: failing call's error, cleanup call separate");
        int r; long sent; int e;
        Check(!((INativeInputDiagnosticSource)real).TakeLastCall(out r,out sent,out e)||sent==1,"instance snapshot consumed/overwritten, no stale reuse");
        g.Dispose();
    }

    static void Watchdog()
    {
        var n=new DiagFake(); var g=Guard(n);
        g.RunAsync(()=>g.KeyDown(65)).Wait();
        var w=Stopwatch.StartNew(); while(!g.Failed&&w.ElapsedMilliseconds<1500) { g.ObserveServerLease(); Thread.Sleep(25); }
        var s=g.FirstDiagnostic;
        Check(g.Failed&&n.Count("ku65")==1&&s.Cause==DiagGuardCause.WatchdogHeartbeat&&s.HeartbeatAgeMs>InputSafety.HoldTtlMs&&s.ServerLeaseAgeMs==-1&&s.ScopeResult==DiagTri.NotEvaluated&&s.HeldKeys==1,
            "watchdog heartbeat: age>750, lease/scope NotEvaluated, held snapshot before release");
        g.Dispose();

        n=new DiagFake(); g=Guard(n);
        g.RunAsync(()=>g.ButtonDown(0,1,1)).Wait();
        for(int i=0;i<12&&!g.Failed;i++) { Fault(g.RunAsync(()=>{g.Heartbeat();return true;})); Thread.Sleep(100); }
        s=g.FirstDiagnostic;
        Check(g.Failed&&n.Count("bup0")==1&&s.Cause==DiagGuardCause.WatchdogServerLease&&s.ServerLeaseAgeMs>InputSafety.ServerLeaseSafetyMs&&s.HeartbeatAgeMs<=InputSafety.HoldTtlMs&&s.ScopeResult==DiagTri.NotEvaluated&&s.HeldButtons==1,
            "watchdog server lease: age>750 with fresh heartbeat, scope NotEvaluated");
        g.Dispose();

        int falseCalls=0; bool ok=true;
        n=new DiagFake(); g=Guard(n,()=>{ if(Volatile.Read(ref ok))return true; Interlocked.Increment(ref falseCalls); return false; });
        g.RunAsync(()=>g.KeyDown(65)).Wait(); g.ObserveServerLease(); Volatile.Write(ref ok,false);
        Wait(()=>g.Failed,500); Thread.Sleep(100); s=g.FirstDiagnostic;
        Check(g.Failed&&s.Cause==DiagGuardCause.WatchdogScopeFalse&&s.ScopeResult==DiagTri.False&&Volatile.Read(ref falseCalls)==1&&n.Count("ku65")==1,
            "watchdog scope false: callback evaluated once, recorded False");
        g.Dispose();

        int throwCalls=0; bool fine=true;
        n=new DiagFake(); g=Guard(n,()=>{ if(Volatile.Read(ref fine))return true; Interlocked.Increment(ref throwCalls); throw new InvalidOperationException(Sentinel); });
        g.RunAsync(()=>g.KeyDown(65)).Wait(); g.ObserveServerLease(); Volatile.Write(ref fine,false);
        Wait(()=>g.Failed,500); Thread.Sleep(100); s=g.FirstDiagnostic;
        Check(g.Failed&&g.Failure=="capture scope or owned foreground lost; held input released"&&s.Cause==DiagGuardCause.WatchdogScopeThrew&&s.ScopeResult==DiagTri.Threw&&Volatile.Read(ref throwCalls)==1,
            "watchdog scope throw: distinct from false, original failure text unchanged");
        g.Dispose();

        bool never=false; n=new DiagFake(); g=Guard(n,()=>never);
        Thread.Sleep(150);
        Check(!g.Failed&&g.FirstDiagnostic==null,"watchdog with nothing held never evaluates scope or trips");
        g.Dispose();
    }

    static void GuardControl()
    {
        // body (non-native) exception is not misclassified as native even though the guard failure text says so
        var n=new DiagFake(); var g=Guard(n);
        var ex=Fault(g.RunAsync(()=>{ throw new InvalidOperationException(Sentinel); }));
        var s=g.FirstDiagnostic;
        Check(ex!=null&&ex.Message==Sentinel&&g.Failure=="native input failed: "+Sentinel,"body exception and guard failure text preserved");
        Check(s.Cause==DiagGuardCause.BodyFailure&&s.BodyCause==DiagBodyCause.Other&&s.Native==null,"body exception classified Body/Other, not Native");
        // guard pre-failed rejection: same exception type/message; first snapshot kept, rejection logged once
        var ex2=Fault(g.RunAsync(()=>g.KeyDown(66)));
        var ex3=Fault(g.RunAsync(()=>g.KeyDown(66)));
        Check(ex2 is InvalidOperationException&&ex2.Message==g.Failure&&ex3!=null&&g.FirstDiagnostic==s&&g.LastDiagnostic.Cause==DiagGuardCause.RejectedPreFailed&&g.LastDiagnostic.HeldKeys==-1&&n.Count("kd66")==0,
            "pre-failed RunAsync rejected without native call; off-thread snapshot never reads held set");
        g.Dispose();
        Check(g.LastDiagnostic.Cause==DiagGuardCause.Dispose&&!g.LastDiagnostic.FirstFailure,"dispose after failure: FirstFailure=false");
        var ex4=Fault(g.RunAsync(()=>true));
        Check(ex4 is InvalidOperationException&&ex4.Message==g.Failure,"disposed guard rejects with original message");

        // FailAsync: snapshot before the original release, release first, then completion
        n=new DiagFake(); g=Guard(n);
        g.RunAsync(()=>{g.KeyDown(65);g.ButtonDown(1,1,1);return true;}).Wait();
        g.FailAsync("stale stateful input batch").Wait();
        s=g.LastDiagnostic;
        Check(g.Failure=="stale stateful input batch"&&n.Count("ku65")==1&&n.Count("bup1")==1&&g.HeldCount==0&&s.Cause==DiagGuardCause.FailAsync&&s.FirstFailure&&s.HeldKeys==1&&s.HeldButtons==1,
            "FailAsync: reason kept, held released, snapshot has pre-release counts");
        g.Dispose();
        // clean dispose without failure
        n=new DiagFake(); g=Guard(n); g.Dispose();
        Check(g.Failure=="input guard stopped"&&g.FirstDiagnostic.Cause==DiagGuardCause.Dispose&&g.FirstDiagnostic.FirstFailure,"clean dispose: original stop text, Dispose code");
    }

    static void ApplyStateful()
    {
        var d=new SessionTransportDescriptor("input-diag","initiator","target","epoch",7,3,1,1,1,250,100,1);
        // context changes between bind and native application: body cause ContextBeforeNative, no native call
        {
            var a=Adapter(d); Receive(a,51,"mouseDown"); bool current=true;
            var authority=Authority(d,()=>current); List<InputCommand> batch;
            Check(a.TryBindInputs(authority,CancellationToken.None,out batch)&&batch.Count==1,"fixture binds fresh edge");
            var session=Session(a); var sink=new PlainFake(); var g=Guard(sink);
            try {
                current=false;
                var ex=Fault(Apply(session,g,batch[0],authority,CancellationToken.None));
                var s=g.FirstDiagnostic;
                Check(ex is InvalidOperationException&&ex.Message=="secure input identity changed before native application"&&sink.Calls==0&&g.Failed,"context mismatch: original exception, no native call");
                Check(s.Cause==DiagGuardCause.BodyFailure&&s.BodyCause==DiagBodyCause.ContextBeforeNative&&s.Native==null,"context mismatch classified Body/ContextBeforeNative");
            } finally { g.Dispose(); }
        }
        // canceled before native: OperationCanceledException preserved, body cause CanceledBeforeNative
        {
            var a=Adapter(d); Receive(a,52,"mouseDown");
            var authority=Authority(d,()=>true); List<InputCommand> batch;
            a.TryBindInputs(authority,CancellationToken.None,out batch);
            var session=Session(a); var sink=new PlainFake(); var g=Guard(sink);
            try {
                var cts=new CancellationTokenSource(); cts.Cancel();
                var ex=Fault(Apply(session,g,batch[0],authority,cts.Token));
                Check(ex is OperationCanceledException&&sink.Calls==0&&g.FirstDiagnostic.BodyCause==DiagBodyCause.CanceledBeforeNative&&g.FirstDiagnostic.Native==null,"cancel before native classified Body/CanceledBeforeNative");
            } finally { g.Dispose(); }
        }
        // expired authority (mid-batch lease): discarded, no injection, guard not failed, nothing logged by the guard
        {
            var a=Adapter(d); Receive(a,53,"mouseDown");
            var authority=Authority(d,()=>true); List<InputCommand> batch;
            a.TryBindInputs(authority,CancellationToken.None,out batch);
            var session=Session(a); var sink=new PlainFake(); var g=Guard(sink);
            try {
                Thread.Sleep(120);
                bool applied=Apply(session,g,batch[0],authority,CancellationToken.None).GetAwaiter().GetResult();
                Check(!applied&&sink.Calls==0&&!g.Failed&&g.FirstDiagnostic==null,"expired lease: discard, no injection, no guard failure");
            } finally { g.Dispose(); }
        }
    }

    static void AuthorityState()
    {
        var session=(MediaSharingSession)FormatterServices.GetUninitializedObject(typeof(MediaSharingSession));
        var m=typeof(MediaSharingSession).GetMethod("InputAuthorityState",F); var cur=typeof(MediaSharingSession).GetMethod("InputAuthorityCurrent",F);
        Func<CancellationToken,DiagInputContext> st=ct=>(DiagInputContext)m.Invoke(session,new object[]{null,1L,2L,8L,null,ct});
        var canceled=new CancellationTokenSource(); canceled.Cancel();
        Set(session,"running",false); Set(session,"captureLeased",false); Set(session,"generation",7L);
        Check(st(canceled.Token)==DiagInputContext.Canceled,"authority state: cancel evaluated first");
        Check(st(CancellationToken.None)==DiagInputContext.NotRunning&&!(bool)cur.Invoke(session,new object[]{null,1L,2L,8L,null,CancellationToken.None}),"authority state: not running (bool wrapper false)");
        Set(session,"running",true);
        Check(st(CancellationToken.None)==DiagInputContext.NotLeased,"authority state: not leased");
        Set(session,"captureLeased",true);
        Check(st(CancellationToken.None)==DiagInputContext.Generation,"authority state: generation mismatch (short-circuit before host)");
    }

    static void LegacyNative()
    {
        // click partial: original (sent=1, error=87) fixed before cleanup; cleanup recorded separately
        var sc=new Script(1,87, 1,0); NativeInput.TestSendOverride=sc.Send;
        try {
            var ex=Fault(()=>NativeInput.Apply(Cmd("click"),SentinelX,SentinelY));
            var f=NativeInput.TakeFailure();
            Check(ex is InvalidOperationException&&ex.Message=="SendInput click failed"&&sc.Requested.Count==2&&sc.Requested[0]==2&&sc.Requested[1]==1,"click partial: original exception, one cleanup call");
            Check(f!=null&&f.Op==DiagNativeOp.Click&&f.Requested==2&&f.Sent==1&&f.Win32Error==87&&f.CleanupAttempted&&f.CleanupRequested==1&&f.CleanupSent==1&&f.CleanupWin32Error==0,"click partial: original vs cleanup snapshots distinct");
            Check(NativeInput.TakeFailure()==null,"legacy snapshot consumed once");
            sc=new Script(0,5); NativeInput.TestSendOverride=sc.Send;
            Fault(()=>NativeInput.Apply(Cmd("key"),0,0)); f=NativeInput.TakeFailure();
            Check(f.Op==DiagNativeOp.KeyTap&&f.Sent==0&&f.Win32Error==5&&!f.CleanupAttempted&&sc.Requested.Count==1,"key tap sent=0: no cleanup");
            sc=new Script(1,0, 0,6); NativeInput.TestSendOverride=sc.Send;
            ex=Fault(()=>NativeInput.Apply(Cmd("text",Sentinel),0,0)); f=NativeInput.TakeFailure();
            Check(ex.Message=="SendInput key tap failed"&&f.Op==DiagNativeOp.TextTap&&f.Sent==1&&f.CleanupSent==0&&f.CleanupWin32Error==6,"text tap partial: cleanup failure kept separate");
            sc=new Script(0,1450); NativeInput.TestSendOverride=sc.Send;
            ex=Fault(()=>NativeInput.Apply(Cmd("move"),0,0)); f=NativeInput.TakeFailure();
            Check(ex.Message=="SendInput failed"&&f.Op==DiagNativeOp.Move&&f.Requested==1&&f.Win32Error==1450,"move failure snapshot");
            // consecutive: failure (not consumed) then success resets at operation start
            sc=new Script(0,5, 2,0); NativeInput.TestSendOverride=sc.Send;
            Fault(()=>NativeInput.Apply(Cmd("key"),0,0)); NativeInput.Apply(Cmd("key"),0,0);
            Check(NativeInput.TakeFailure()==null,"success after unconsumed failure: no stale reuse");
            // two threads: thread B never sees thread A's snapshot
            sc=new Script(0,5); NativeInput.TestSendOverride=sc.Send;
            Fault(()=>NativeInput.Apply(Cmd("key"),0,0));
            NativeInputFailure other=null; bool otherBefore=true;
            var t=new Thread(()=>{ otherBefore=NativeInput.TakeFailure()==null; NativeInput.TestSendOverride=new Script().Send; NativeInput.Apply(Cmd("key"),0,0); other=NativeInput.TakeFailure(); NativeInput.TestSendOverride=null; });
            t.Start(); t.Join(2000);
            f=NativeInput.TakeFailure();
            Check(otherBefore&&other==null&&f!=null&&f.Win32Error==5,"two-thread isolation of legacy snapshots");
        } finally { NativeInput.TestSendOverride=null; }
    }

    static void LegacyThroughGuard()
    {
        // guard-thread legacy click: snapshot consumed on the guard thread, classified Native with cleanup
        var n=new DiagFake(); var g=Guard(n);
        var ex=Fault(g.RunAsync(()=>{ NativeInput.TestSendOverride=new Script(1,87, 1,0).Send; try { g.Legacy(Cmd("click"),SentinelX,SentinelY); } finally { NativeInput.TestSendOverride=null; } return true; }));
        var s=g.FirstDiagnostic;
        Check(ex!=null&&ex.Message=="SendInput click failed"&&g.Failure=="native input failed: SendInput click failed","guard legacy click: original exception/failure text");
        Check(s.Cause==DiagGuardCause.NativeFailure&&s.Native.Op==DiagNativeOp.Click&&s.Native.Sent==1&&s.Native.CleanupAttempted,"guard legacy click classified Native with cleanup fields");
        g.Dispose();
        // non-native exception inside Legacy (empty text) is Body, not a stale native snapshot
        n=new DiagFake(); g=Guard(n);
        ex=Fault(g.RunAsync(()=>{ NativeInput.TestSendOverride=new Script().Send; try { g.Legacy(Cmd("text",""),0,0); } finally { NativeInput.TestSendOverride=null; } return true; }));
        Check(ex is IndexOutOfRangeException&&g.FirstDiagnostic.Cause==DiagGuardCause.BodyFailure&&g.FirstDiagnostic.Native==null,"legacy non-native exception classified Body");
        g.Dispose();
    }

    static void LogFile(string dir)
    {
        var sb=new StringBuilder();
        foreach(var f in Directory.GetFiles(dir,"remotebridge-diag-*.jsonl"))sb.Append(File.ReadAllText(f));
        string log=sb.ToString(); string cor="\"cor\":\""+Cor.ToString("x16")+"\"";
        Func<string,bool> line=needle=>{ foreach(var l in log.Split('\n'))if(l.Contains(cor)&&AllIn(l,needle.Split('|')))return true; return false; };
        Check(line("\"ev\":\"InputGuardFailure\"|\"Cause\":\"NativeFailure\"|\"NativeFailure\":1|\"ExType\":\"InvalidOperation\""),"log: guard native failure with correlation and exception type");
        Check(line("\"ev\":\"InputNativeFailure\"|\"Operation\":\"KeyDown\"|\"Sent\":0|\"Win32Error\":5|\"ErrorMayBeStale\":1|\"AuthoritativeCause\":0"),"log: separate native event with numeric facts only");
        Check(line("\"ev\":\"InputNativeFailure\"|\"Operation\":\"Click\"|\"CleanupAttempted\":1|\"CleanupSent\":1"),"log: legacy click cleanup fields");
        Check(line("\"Cause\":\"WatchdogScopeThrew\"|\"ScopeResult\":\"Threw\""),"log: watchdog scope throw");
        Check(line("\"Cause\":\"WatchdogHeartbeat\"|\"ServerLeaseAgeMs\":-1"),"log: heartbeat timing with NotEvaluated lease");
        Check(line("\"Cause\":\"BodyFailure\"|\"BodyCause\":\"ContextBeforeNative\""),"log: context body cause");
        Check(line("\"Cause\":\"BodyFailure\"|\"BodyCause\":\"Other\"|\"NativeFailure\":0"),"log: body other not native");
        Check(line("\"Cause\":\"FailAsync\"|\"HeldKeys\":1|\"HeldButtons\":1"),"log: FailAsync snapshot");
        Check(line("\"ev\":\"InputWorkerSite\"|\"Cause\":\"GuardPreFailed\"|\"Secure\":0"),"log: actual worker GuardPreFailed site");
        Check(line("\"ev\":\"InputWorkerSite\"|\"Cause\":\"ContextBeforePoll\"|\"Secure\":1|\"Context\":\"NotLeased\""),"log: actual secure worker ContextBeforePoll NotLeased site");
        Check(line("\"ev\":\"InputWorkerSite\"|\"Cause\":\"LeaseMidBatch\"|\"Secure\":0|\"LeaseMs\":100|\"Index\":0|\"Count\":1|\"HeldCount\":1"),"log: actual polling worker LeaseMidBatch site");
        Check(line("\"Cause\":\"QueueClosed\"|\"NativeFailure\":0")&&line("\"Cause\":\"FailAsyncQueueClosed\"|\"NativeFailure\":0"),"log: queue-closed codes, non-native");
        Check(log.IndexOf("SENTINEL",StringComparison.OrdinalIgnoreCase)<0&&log.IndexOf(SentinelX.ToString())<0&&log.IndexOf(SentinelY.ToString())<0&&log.IndexOf("SendInput")<0&&log.IndexOf("native input failed")<0,
            "log privacy: no exception text, sentinel text or coordinates");
        int maxFields=0; foreach(var l in log.Split('\n')) { int c=0; foreach(char ch in l)if(ch==':')c++; maxFields=Math.Max(maxFields,c); }
        Check(maxFields<=32+10,"log: field count within cap (+envelope)");
    }
    // A: a concurrent rejection while the original cleanup is blocked never replaces the first (native) cause.
    static void FirstCauseRace()
    {
        var n=new DiagFake(); var g=Guard(n); var block=new ManualResetEventSlim(false); var entered=new ManualResetEventSlim(false);
        try {
            g.RunAsync(()=>g.KeyDown(65)).Wait(); g.ObserveServerLease();
            n.UpEntered=entered; n.BlockUp=block; n.FailNext=1; n.FailError=5;
            var original=g.RunAsync(()=>g.KeyDown(66));
            bool blocked=entered.Wait(2000); var s=g.FirstDiagnostic;
            var rejected=Fault(g.RunAsync(()=>true));
            Check(blocked&&g.Failed&&!original.IsCompleted&&s!=null&&s.Cause==DiagGuardCause.NativeFailure&&s.Native!=null&&s.Native.Win32Error==5&&s.HeldKeys==1&&s.FirstFailure
                &&rejected is InvalidOperationException&&rejected.Message==g.Failure&&g.FirstDiagnostic==s&&g.LastDiagnostic.Cause==DiagGuardCause.RejectedPreFailed,
                "race: native cause published before failure visibility; concurrent rejection during blocked cleanup does not replace it");
            block.Set(); var ex=Fault(original);
            Check(ex is InvalidOperationException&&ex.Message=="SendInput keyDown failed"&&g.Failure=="native input failed: SendInput keyDown failed"&&g.FirstDiagnostic==s&&g.LastDiagnostic==s
                &&n.Count("ku65")==1&&n.Count("kd66")==1&&g.HeldCount==0,"race: original exception/failure kept, release done, first cause/error/pre-held unchanged");
        } finally { block.Set(); g.Dispose(); }
    }

    // B: Dispose publishes in memory first; LastDiagnostic/event only after the guard thread's original cleanup.
    static void DisposeOrder()
    {
        var n=new DiagFake(); var g=Guard(n); var block=new ManualResetEventSlim(false); var entered=new ManualResetEventSlim(false); Thread t=null;
        try {
            g.RunAsync(()=>g.KeyDown(65)).Wait(); g.ObserveServerLease(); n.UpEntered=entered; n.BlockUp=block;
            t=new Thread(g.Dispose) { IsBackground=true }; t.Start();
            bool blocked=entered.Wait(2000); var first=g.FirstDiagnostic;
            Check(blocked&&t.IsAlive&&g.Failure=="input guard stopped"&&g.LastDiagnostic==null&&first!=null&&first.Cause==DiagGuardCause.Dispose&&first.FirstFailure&&first.Disposed&&first.HeldKeys==-1,
                "dispose: first published in memory; last/event not visible while original cleanup is blocked");
            block.Set(); bool joined=t.Join(3000);
            Check(joined&&g.LastDiagnostic==first&&n.Count("ku65")==1&&g.HeldCount==0,"dispose: event emitted only after original guard cleanup completed");
        } finally { block.Set(); if(t!=null)t.Join(3000); }
    }

    // C: actual RunAsync/FailAsync queue-closed branches (work completed while disposed=false and failure=null).
    static void QueueClosed()
    {
        var n=new DiagFake(); var g=Guard(n);
        try {
            ((System.Collections.Concurrent.BlockingCollection<Action>)typeof(InputSafety).GetField("work",F).GetValue(g)).CompleteAdding();
            Check(!g.Failed&&g.FirstDiagnostic==null,"queue closed fixture: disposed=false, failure=null");
            bool ran=false;
            var ex=Fault(g.RunAsync(()=>{ ran=true; return g.KeyDown(65); })); var s=g.LastDiagnostic;
            Check(ex is InvalidOperationException&&ex.Message=="input guard stopped"&&!ran&&!g.Failed&&s!=null&&s.Cause==DiagGuardCause.QueueClosed&&s.Native==null&&!s.Disposed&&!s.FirstFailure&&s.HeldKeys==-1&&n.Log.Count==0,
                "RunAsync QueueClosed: original exception/message, fixed code, non-native, no input");
            var fail=g.FailAsync("stale stateful input batch"); bool done=fail.Wait(2000); s=g.LastDiagnostic;
            Check(done&&fail.Status==TaskStatus.RanToCompletion&&g.Failure=="stale stateful input batch"&&s.Cause==DiagGuardCause.FailAsyncQueueClosed&&s.Native==null&&!s.Disposed&&s.FirstFailure&&s.HeldKeys==-1&&n.Log.Count==0,
                "FailAsync FailAsyncQueueClosed: completes, original reason, fixed code, non-native, no input");
        } finally { g.Dispose(); }
    }

    // D: actual polling input workers against a pinned local TLS stub (same scaffolding as OpaqueCloseSmoke). Fake natives only.
    const string Sid="0123456789abcdef0123456789abcdef";
    static int leasePolls; static volatile Func<string,string> respond=p=>Reply(404,"Not Found","");
    static string Reply(int code,string reason,string body) { return "HTTP/1.1 "+code+" "+reason+"\r\nCache-Control: no-store\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body; }
    static void Serve(TcpClient c,X509Certificate2 cert)
    {
        try { using(c)using(var ssl=new SslStream(c.GetStream(),false)) {
            ssl.AuthenticateAsServer(cert,false,SslProtocols.Tls12,false);
            var head=new StringBuilder(); var one=new byte[1];
            while(!head.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)) { if(ssl.Read(one,0,1)!=1)return; head.Append((char)one[0]); if(head.Length>16384)return; }
            string path=head.ToString().Split(new[]{"\r\n"},StringSplitOptions.None)[0].Split(' ')[1];
            if(path.EndsWith("/input-lease",StringComparison.Ordinal))Interlocked.Increment(ref leasePolls);
            var bytes=Encoding.UTF8.GetBytes(respond(path)); ssl.Write(bytes,0,bytes.Length); ssl.Flush();
        } } catch { }
    }
    static X509Certificate2 Cert()
    {
        using(var rsa=RSA.Create(2048)) {
            var req=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
            using(var c=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1)))
                return new X509Certificate2(c.Export(X509ContentType.Pfx,""),"",X509KeyStorageFlags.Exportable);
        }
    }
    static MediaSharingSession WorkerSession(ControlPlaneClient c,InputSafety g,SecureSessionAdapter a)
    {
        var m=new MediaSharingSession(c,Sid,CaptureScope.ForDesktopShare());
        Set(m,"generation",7L); Set(m,"running",true); Set(m,"stateful",true); Set(m,"captureLeased",false); Set(m,"guard",g); Set(m,"diagCorrelation",Cor);
        if(a!=null) { var w=m.GetType().GetField("wire",F).GetValue(m); w.GetType().GetProperty("Secure",F).SetValue(w,a,null); }
        return m;
    }
    static Exception RunWorker(MethodInfo worker,MediaSharingSession m)
    {
        var t=(Task)worker.Invoke(m,new object[]{CancellationToken.None});
        try { t.Wait(5000); } catch { }
        if(!t.IsCompleted)return new TimeoutException("worker did not finish");
        return Fault(t);
    }
    static void Workers()
    {
        var cert=Cert(); string pin=BitConverter.ToString(SHA256.Create().ComputeHash(cert.RawData)).Replace("-","");
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(()=>{ while(true) { TcpClient c; try { c=listener.AcceptTcpClient(); } catch { return; } ThreadPool.QueueUserWorkItem(_=>Serve(c,cert)); } }) { IsBackground=true }.Start();
        try {
            var prof=ServerProfile.Parse("{\"Version\":1,\"Name\":\"stub\",\"RendezvousUrl\":\"https://localhost:"+port+"\",\"RelayUrl\":\"https://localhost:"+port+"\",\"CertificateSha256\":\""+pin+"\"}");
            var client=new ControlPlaneClient(prof); client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
            var input=typeof(MediaSharingSession).GetMethod("InputWorker",F); var secure=typeof(MediaSharingSession).GetMethod("SecureStatefulInputWorker",F);
            // GuardPreFailed: actual (legacy stateful) polling worker stops before any HTTP poll.
            {
                var n=new DiagFake(); var g=Guard(n); g.FailAsync("stale stateful input batch").Wait(); int before=Volatile.Read(ref leasePolls);
                var ex=RunWorker(input,WorkerSession(client,g,null));
                Check(ex is InvalidOperationException&&ex.Message=="stale stateful input batch"&&Volatile.Read(ref leasePolls)==before&&n.Log.Count==0,"worker GuardPreFailed: original exception before poll, no HTTP, no input");
                g.Dispose();
            }
            // ContextBeforePoll: actual secure worker, valid credentials/LiveCaptureBinding, captureLeased=false -> NotLeased, no HTTP.
            {
                var d=new SessionTransportDescriptor("input-diag","initiator","target","epoch",7,3,1,1,1,250,100,1);
                var n=new DiagFake(); var g=Guard(n); int before=Volatile.Read(ref leasePolls);
                var ex=RunWorker(secure,WorkerSession(client,g,Adapter(d)));
                Check(ex is InvalidOperationException&&ex.Message=="secure input identity context changed"&&Volatile.Read(ref leasePolls)==before&&!g.Failed&&n.Log.Count==0,"secure worker ContextBeforePoll: original exception before HTTP, guard not failed, no input");
                g.Dispose();
            }
            // LeaseMidBatch: actual polling worker; the guard thread is held behind a finite gate so the bound edge reaches the
            // guard after the 100ms request-start lease. Expect discard, fatal release of held input, original exception.
            const string Batch="{\"generation\":7,\"leaseMs\":100,\"events\":[{\"generation\":7,\"sequence\":1,\"kind\":\"mouseDown\",\"x\":31337,\"y\":27182,\"button\":0,\"keyCode\":0,\"text\":\"\",\"wheelDelta\":0}]}";
            respond=p=>p.EndsWith("/input-lease",StringComparison.Ordinal)?Reply(200,"OK","{\"generation\":7,\"leaseMs\":100,\"events\":[]}"):Reply(404,"Not Found","");
            var warm=new MediaSharingSession(client,Sid,CaptureScope.ForDesktopShare()); var warmWire=(MediaWire)warm.GetType().GetField("wire",F).GetValue(warm);
            for(int i=0;i<3;i++)warmWire.PollInputLeaseAsync(Sid,true,CancellationToken.None).Wait(5000); // JIT/TLS warm-up only
            bool done=false; int attempts=0;
            while(!done&&attempts<2) {
                attempts++;
                var n=new DiagFake(); var g=Guard(n); var polled=new ManualResetEventSlim(false);
                try {
                    respond=p=>{ if(!p.EndsWith("/input-lease",StringComparison.Ordinal))return Reply(404,"Not Found",""); polled.Set(); return Reply(200,"OK",Batch); };
                    g.RunAsync(()=>g.KeyDown(65)).Wait(); g.ObserveServerLease();
                    var gate=g.RunAsync(()=>{ polled.Wait(3000); Thread.Sleep(130); return true; });
                    var ex=RunWorker(input,WorkerSession(client,g,null));
                    if(ex!=null&&ex.Message=="stale stateful input batch; held input released, input stopped until sharing is restarted") { Console.WriteLine("INFO LeaseMidBatch attempt "+attempts+": poll response itself exceeded 100ms (StaleBatch branch); retrying"); continue; }
                    done=true; var first=g.FirstDiagnostic; var last=g.LastDiagnostic;
                    Check(ex is InvalidOperationException&&ex.Message=="stateful input lease expired mid-batch; held input released, input stopped until sharing is restarted"&&g.Failure=="stateful input lease expired mid-batch"
                        &&gate.IsCompleted&&n.Count("bd0")==0&&n.Count("ku65")==1&&g.HeldCount==0,"worker LeaseMidBatch: expired 100ms lease discards edge, no input, fatal release, original exception");
                    Check(first!=null&&first.Cause==DiagGuardCause.FailAsync&&first.FirstFailure&&first.HeldKeys==-1&&last!=null&&last.Cause==DiagGuardCause.FailAsync&&last.HeldKeys==1&&last.Native==null,
                        "worker LeaseMidBatch: FailAsync first published off-thread, guard-thread detail with held count");
                } finally { polled.Set(); g.Dispose(); }
            }
            Console.WriteLine("INFO LeaseMidBatch attempts="+attempts);
            Check(done,"worker LeaseMidBatch branch reached");
        } finally { listener.Stop(); }
    }

    static bool AllIn(string l,string[] needles) { foreach(var n in needles)if(l.IndexOf(n,StringComparison.Ordinal)<0)return false; return true; }

    // ---- fixtures (same synthetic adapter construction as InputBindingSmoke) ----
    static void Set(object target,string field,object value) { target.GetType().GetField(field,F).SetValue(target,value); }
    static TaskCompletionSource<T> NewTcs<T>() { return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); }
    static SecureSessionAdapter Adapter(SessionTransportDescriptor descriptor)
    {
        var crypto=(SecurePeerChannel)FormatterServices.GetUninitializedObject(typeof(SecurePeerChannel));
        Set(crypto,"gate",new object()); Set(crypto,"established",true); Set(crypto,"sentFinished",true); Set(crypto,"receivedFinished",true);
        var adapter=(SecureSessionAdapter)FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter));
        Set(adapter,"crypto",crypto); Set(adapter,"initiator",false); Set(adapter,"lifetime",new CancellationTokenSource()); Set(adapter,"sync",new object());
        Set(adapter,"descriptor",descriptor); Set(adapter,"json",new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8});
        var inputField=typeof(SecureSessionAdapter).GetField("inputs",F); Set(adapter,"inputs",Activator.CreateInstance(inputField.FieldType));
        Set(adapter,"hello",NewTcs<byte[]>()); Set(adapter,"finished",NewTcs<byte[]>()); Set(adapter,"readySignal",NewTcs<bool>()); Set(adapter,"firstFrame",NewTcs<bool>()); Set(adapter,"directOffer",NewTcs<byte[]>());
        if(!adapter.Ready)throw new Exception("synthetic adapter did not become ready");
        return adapter;
    }
    static void Receive(SecureSessionAdapter adapter,long seq,string kind)
    {
        string json="{\"generation\":7,\"sequence\":"+seq+",\"kind\":\""+kind+"\",\"x\":12,\"y\":34,\"button\":0,\"keyCode\":0,\"text\":\"\",\"wheelDelta\":0}";
        typeof(SecureSessionAdapter).GetMethod("ReceiveInput",F).Invoke(adapter,new object[]{new UTF8Encoding(false,true).GetBytes(json)});
    }
    static InputLeaseAuthority Authority(SessionTransportDescriptor d,Func<bool> current)
    {
        var binding=new CaptureProofBinding(d.SessionId,"host",d.TargetDeviceId,"fingerprint",2,"https://synthetic.invalid","pin",1,d.SharingGeneration,"scope");
        return new InputLeaseAuthority(d.SharingGeneration,100,Stopwatch.StartNew(),binding,2,1,d,CancellationToken.None,current);
    }
    static MediaSharingSession Session(SecureSessionAdapter a)
    {
        var wire=(MediaWire)FormatterServices.GetUninitializedObject(typeof(MediaWire));
        wire.GetType().GetProperty("Secure",F).SetValue(wire,a,null);
        var session=(MediaSharingSession)FormatterServices.GetUninitializedObject(typeof(MediaSharingSession));
        Set(session,"wire",wire); Set(session,"scope",CaptureScope.ForDesktopShare()); Set(session,"allowRemoteInput",true);
        return session;
    }
    static Task<bool> Apply(MediaSharingSession session,InputSafety g,InputCommand c,InputLeaseAuthority authority,CancellationToken ct)
    {
        return (Task<bool>)typeof(MediaSharingSession).GetMethod("ApplyStatefulAsync",F).Invoke(session,new object[]{g,c,authority.RequestStart,authority.LeaseMs,ct,authority});
    }
}
