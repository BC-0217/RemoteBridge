using System;
using System.Collections.Generic;
using System.Threading;
using PeerDesk.Windows;

// Synthetic only: no desktop capture and no real SendInput (FakeNative), private mutex name per run.
internal static class Program
{
    private sealed class FakeNative : INativeStatefulInput
    {
        public readonly List<string> Log=new List<string>(); public HashSet<int> Physical=new HashSet<int>(); public bool FailNext;
        private bool Ok(string s) { lock(Log)Log.Add(s); if(FailNext){FailNext=false;return false;} return true; }
        public bool IsPhysicallyDown(int vk) { return Physical.Contains(vk); }
        public bool Key(int vk,bool down) { return Ok((down?"kd":"ku")+vk); }
        public bool ButtonDown(int b,int x,int y) { return Ok("bd"+b); }
        public bool ButtonUp(int b,int x,int y) { return Ok("bu"+b); }
        public bool ButtonUpInPlace(int b) { return Ok("bup"+b); }
        public bool Wheel(int d,int x,int y) { return Ok("w"+d); }
        public int Count(string s) { lock(Log)return Log.FindAll(x=>x==s).Count; }
        public bool Has(string s) { lock(Log)return Log.Contains(s); }
    }
    private static int failures;
    private static void Check(bool ok,string name) { Console.WriteLine((ok?"PASS ":"FAIL ")+name); if(!ok)failures++; }
    private static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
    private static Dictionary<string,object> Row(long seq,string kind,Dictionary<string,object> extra=null)
    {
        var d=new Dictionary<string,object>{{"generation",7},{"sequence",seq},{"kind",kind},{"x",0},{"y",0}};
        if(extra!=null)foreach(var kv in extra)d[kv.Key]=kv.Value; return d;
    }
    private static Dictionary<string,object> Batch(params object[] rows) { return new Dictionary<string,object>{{"generation",7},{"leaseMs",100},{"events",new System.Collections.ArrayList(rows)}}; }

    private static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--mutex-denied") {
            using(var child=new InputSafety(()=>true,new FakeNative())) {
                try { child.Start(args[1]); return 5; }
                catch(InvalidOperationException) { return 0; }
            }
        }
        // Capabilities
        var caps=MediaWire.ParseCapabilities(new Dictionary<string,object>{{"generation",1},{"minFrameIntervalMs",100},{"inputLeasePollSupported",true},{"statefulInputVersion",1},{"inputPollIntervalMs",50}});
        Check(caps.Stateful,"caps v1+50 stateful");
        Check(!MediaWire.ParseCapabilities(new Dictionary<string,object>{{"generation",1},{"minFrameIntervalMs",100},{"inputLeasePollSupported",true}}).Stateful,"old server legacy");
        Check(Throws(()=>MediaWire.ParseCapabilities(new Dictionary<string,object>{{"generation",1},{"minFrameIntervalMs",100},{"inputLeasePollSupported",true},{"statefulInputVersion",1},{"inputPollIntervalMs",100}})),"v1 with 100 rejected");
        Check(Throws(()=>MediaWire.ParseCapabilities(new Dictionary<string,object>{{"generation",1},{"statefulInputVersion",2}})),"v2 rejected");
        // Batch parsing
        Check(MediaWire.ParseInputLease(Batch(Row(1,"keyDown",new Dictionary<string,object>{{"keyCode",65}}),Row(2,"wheel",new Dictionary<string,object>{{"wheelDelta",-120}})),true).Events.Count==2,"stateful batch parses");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"keyDown",new Dictionary<string,object>{{"keyCode",65}})),false)),"stateful kind rejected when not opted");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"wheel")),true)),"zero wheel rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"wheel",new Dictionary<string,object>{{"wheelDelta",1201}})),true)),"wheel range");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"move",new Dictionary<string,object>{{"wheelDelta",5}})),true)),"wheelDelta on move rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(2,"move"),Row(2,"move")),true)),"non-monotonic sequence");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"keyDown",new Dictionary<string,object>{{"keyCode",1}})),true)),"mouse VK as key rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"keyDown",new Dictionary<string,object>{{"keyCode",65},{"x",1}})),true)),"keyDown unused point rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"holdHeartbeat",new Dictionary<string,object>{{"keyCode",65}})),true)),"heartbeat unused key rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"mouseDown",new Dictionary<string,object>{{"text","x"}})),true)),"mouseDown unused text rejected");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(Row(1,"wheel",new Dictionary<string,object>{{"wheelDelta",120},{"button",1}})),true)),"wheel unused button rejected");
        var rows=new object[33]; for(int i=0;i<33;i++)rows[i]=Row(i+1,"move");
        Check(Throws(()=>MediaWire.ParseInputLease(Batch(rows),true)),"batch >32 rejected");

        string name=@"Local\RemoteBridge.InputOwner.Smoke."+Guid.NewGuid().ToString("N");
        // Guard: hold, heartbeat TTL release, permanent failure
        var n=new FakeNative(); var g=new InputSafety(()=>true,n); g.Start(name);
        Check(g.RunAsync(()=>g.KeyDown(65)).Result&&g.HeldCount==1,"keyDown held");
        Check(g.RunAsync(()=>g.KeyDown(65)).Result&&g.HeldCount==1,"repeat keyDown tracked once");
        var second=new InputSafety(()=>true,new FakeNative());
        Check(Throws(()=>second.Start(name)),"second owner fails explicitly");
        second.Dispose();
        using(var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName=System.Reflection.Assembly.GetExecutingAssembly().Location,
            Arguments="--mutex-denied "+name,UseShellExecute=false,CreateNoWindow=true
        })) {
            bool exited=child.WaitForExit(3000);
            if(!exited)child.Kill();
            Check(exited&&child.ExitCode==0,"actual second process mutex ownership denied");
        }
        var heartbeatWait=System.Diagnostics.Stopwatch.StartNew();
        while(!g.Failed&&heartbeatWait.ElapsedMilliseconds<1200) { g.ObserveServerLease(); Thread.Sleep(25); } // lease alone must not renew the hold heartbeat
        Check(n.Has("ku65")&&g.Failed,"heartbeat TTL releases + fails");
        Check(Throws(()=>g.RunAsync(()=>g.KeyDown(66)).Wait()),"no revival after failure");
        g.Dispose();
        // Lease watchdog
        var n2=new FakeNative(); var g2=new InputSafety(()=>true,n2); g2.Start(name);
        g2.RunAsync(()=>g2.ButtonDown(0,1,1)).Wait();
        for(int i=0;i<10&&!g2.Failed;i++) { g2.RunAsync(()=>{g2.Heartbeat();return true;}).Wait(); Thread.Sleep(100); }
        Check(n2.Has("bup0")&&g2.Failed,"stale server lease releases");
        g2.Dispose();
        // Physically held key never claimed; partial failure trips; dispose releases
        var n3=new FakeNative(); n3.Physical.Add(16); var g3=new InputSafety(()=>true,n3); g3.Start(name);
        Check(!g3.RunAsync(()=>g3.KeyDown(16)).Result&&!n3.Has("kd16"),"physically held key not claimed");
        Check(!g3.RunAsync(()=>g3.KeyUp(17)).Result&&!n3.Has("ku17"),"never release unowned key");
        g3.RunAsync(()=>g3.KeyDown(17)).Wait(); g3.Dispose();
        Check(n3.Has("ku17"),"dispose releases held");
        var n4=new FakeNative(); var g4=new InputSafety(()=>true,n4); g4.Start(name); n4.FailNext=true;
        Check(Throws(()=>g4.RunAsync(()=>g4.KeyDown(65)).Wait())&&g4.Failed&&g4.HeldCount==0,"failed SendInput not tracked, guard fails");
        g4.Dispose();
        // Scope loss
        bool scopeOk=true; var n5=new FakeNative(); var g5=new InputSafety(()=>scopeOk,n5); g5.Start(name);
        g5.RunAsync(()=>g5.KeyDown(65)).Wait(); scopeOk=false; Thread.Sleep(150);
        Check(n5.Has("ku65")&&g5.Failed,"scope loss releases");
        g5.Dispose();
        var n6=new FakeNative(); var g6=new InputSafety(()=>true,n6); g6.Start(name);
        g6.RunAsync(()=>g6.KeyDown(65)).Wait(); n6.FailNext=true;
        Check(Throws(()=>g6.RunAsync(()=>g6.KeyUp(65)).Wait())&&n6.Count("ku65")==2&&g6.Failed,"failed keyUp retains ownership for cleanup retry");
        g6.Dispose();
        var n7=new FakeNative(); var g7=new InputSafety(()=>true,n7); g7.Start(name);
        g7.RunAsync(()=>g7.ButtonDown(0,1,1)).Wait(); n7.FailNext=true;
        Check(Throws(()=>g7.RunAsync(()=>g7.ButtonUp(0,1,1,true)).Wait())&&n7.Has("bup0")&&g7.Failed,"failed mouseUp retains ownership for cleanup retry");
        g7.Dispose();
        var n8=new FakeNative(); var g8=new InputSafety(()=>true,n8); g8.Start(name);
        g8.RunAsync(()=>{g8.KeyDown(17);g8.KeyDown(65);g8.ButtonDown(0,1,1);return true;}).Wait();
        Check(g8.HeldCount==3,"combination tracks independent held inputs");
        g8.RunAsync(()=>{g8.ReleaseAllOnThread();return true;}).Wait();
        Check(!g8.Failed&&g8.HeldCount==0&&n8.Has("ku17")&&n8.Has("ku65")&&n8.Has("bup0"),"normal releaseAll releases only owned inputs without trip");
        Check(g8.RunAsync(()=>g8.KeyDown(66)).Result,"new explicit down allowed after normal releaseAll");
        g8.Dispose();
        Console.WriteLine(failures==0?"ALL PASS":failures+" FAILURE(S)");
        return failures==0?0:1;
    }
}
