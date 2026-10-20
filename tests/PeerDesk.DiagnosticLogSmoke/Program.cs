using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PeerDesk.Windows;

// Local-only smoke for the privacy-safe diagnostic log: no network, no app start, isolated temp folders. Child modes
// (this exe only, finite timeouts) cover once-per-process cases: locked log, unusable path, concurrent writers.
static class Program
{
    static int failures;
    static void Check(bool ok,string name){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;}
    const long FileBytes=64*1024;
    static string dir;

    static int Main(string[] args)
    {
        if(args.Length>0)return Child(args);
        dir=Path.Combine(Path.GetTempPath(),"remotebridge-diag-smoke-"+Guid.NewGuid().ToString("N"));
        string childRoot=Path.Combine(Path.GetTempPath(),"remotebridge-diag-child-"+Guid.NewGuid().ToString("N"));
        try
        {
            Check(!DiagnosticLog.Enabled,"disabled before Initialize (no-op logging)");
            DiagnosticLog.Event(DiagEvent.SharingStart,1);
            Check(!Directory.Exists(dir),"nothing written before Initialize");

            // Pure helpers.
            Check(DiagnosticLog.NormalizeEndReason(null)==DiagEndReason.None,"endReason null -> None");
            Check(DiagnosticLog.NormalizeEndReason("user-stopped")==DiagEndReason.UserStopped&&DiagnosticLog.NormalizeEndReason("session-ended")==DiagEndReason.SessionEnded
                &&DiagnosticLog.NormalizeEndReason("error-stopped")==DiagEndReason.ErrorStopped&&DiagnosticLog.NormalizeEndReason("expired")==DiagEndReason.Expired,"endReason allowlist");
            Check(DiagnosticLog.NormalizeEndReason("Session-Ended")==DiagEndReason.Unknown&&DiagnosticLog.NormalizeEndReason("secret pw 1234")==DiagEndReason.Unknown,"endReason outside allowlist -> Unknown");
            Check(DiagnosticLog.Classify(new CaptureProofExpiredException())==DiagExceptionKind.CaptureProofExpired,"classify capture proof expiry");
            Check(DiagnosticLog.Classify(new MediaHttpException(409,null,"session-ended"))==DiagExceptionKind.MediaHttp,"classify media http");
            Check(DiagnosticLog.Classify(new OperationCanceledException())==DiagExceptionKind.OperationCanceled,"classify cancel");
            Check(DiagnosticLog.Classify(new EndOfStreamException())==DiagExceptionKind.EndOfStream&&DiagnosticLog.Classify(new InvalidDataException())==DiagExceptionKind.InvalidData,"classify IO subtypes");
            Check(DiagnosticLog.Classify(new SocketException(10054))==DiagExceptionKind.Socket,"classify socket");
            var fields=DiagnosticLog.ExceptionFields(new MediaHttpException(409,new WebException("wx",WebExceptionStatus.ConnectFailure),"session-ended"));
            Check(Has(fields,DiagKey.ExHttpStatus,409)&&HasCode(fields,DiagKey.ExWebStatus,WebExceptionStatus.ConnectFailure)&&HasCode(fields,DiagKey.ExEndReason,DiagEndReason.SessionEnded)&&HasCode(fields,DiagKey.ExInner,DiagExceptionKind.Web),"exception facts: status/web/endReason/inner");
            var agg=DiagnosticLog.ExceptionFields(new AggregateException(new TimeoutException("t")));
            Check(HasCode(agg,DiagKey.ExType,DiagExceptionKind.Timeout),"single-inner aggregate unwrapped");
            Check(DiagnosticLog.ExceptionFields(null).Length==1,"null exception -> type None only");
            long c1=DiagnosticLog.NewCorrelation(),c2=DiagnosticLog.NewCorrelation();Check(c1!=0&&c2!=0&&c1!=c2,"random nonzero correlations");

            DiagnosticLog.Initialize(dir,FileBytes);
            Check(DiagnosticLog.Enabled,"enabled after Initialize");

            // Privacy: message/inner text, addresses, ids and paths must never reach the file.
            string[] secrets={"SECRET-TOKEN-7731","10.20.30.40","session-abc-123","device-999","C:\\Users","https://","password"};
            var leak=new InvalidOperationException("SECRET-TOKEN-7731 at 10.20.30.40 session-abc-123 device-999 C:\\Users https:// password",new IOException("10.20.30.40 SECRET-TOKEN-7731"));
            DiagnosticLog.Failure(DiagEvent.AdapterFail,c1,leak,DiagValue.Of(DiagKey.Origin,DiagAdapterOrigin.Packet),DiagValue.Of(DiagKey.Detail,2));
            DiagnosticLog.Failure(DiagEvent.SharingWorkerFirstError,c1,new OpaquePacketHttpException(404,new WebException("https://10.20.30.40/x")),DiagValue.Of(DiagKey.Worker,DiagWorker.Frame),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.Candidate));
            DiagnosticLog.Failure(DiagEvent.SharingFatal,c1,new MediaHttpException(409,null,"password"),DiagValue.Of(DiagKey.Reason,DiagSettle.IndependentFatal));
            DiagnosticLog.Event(DiagEvent.InputBudgetTimeout,c1,DiagValue.Of(DiagKey.Site,DiagInputTimeoutSite.PollCanceled),DiagValue.Of(DiagKey.RequestBudgetMs,120),DiagValue.Of(DiagKey.ProofRemainingMs,-5),DiagValue.Of(DiagKey.PendingBudgetMs,-1));
            // Field cap, caller-array clone and duplicate keys (first wins); exception facts never hide site values.
            long corF=DiagnosticLog.NewCorrelation();
            var keys=(DiagKey[])Enum.GetValues(typeof(DiagKey));var many=new DiagValue[40];
            many[0]=DiagValue.Of(DiagKey.Value,123001);many[1]=DiagValue.Of(DiagKey.Value,123002);
            for(int i=2,k=0;i<many.Length;k++){if(keys[k]==DiagKey.Value)continue;many[i++]=DiagValue.Of(keys[k],(long)(500+i));}
            DiagnosticLog.Event(DiagEvent.MainRefinement,corF,many);
            many[0]=DiagValue.Of(DiagKey.Value,123009);
            DiagnosticLog.Failure(DiagEvent.MainCleanup,corF,new MediaHttpException(409,null,"session-ended"),DiagValue.Of(DiagKey.ExHttpStatus,777),DiagValue.Of(DiagKey.ExHttpStatus,778));
            long corH=HookChecks();
            Marker(1);
            Check(WaitMarker(1),"events flushed by background writer");
            var lines=AllLines();
            string all=String.Join("\n",lines);
            Check(secrets.All(s=>all.IndexOf(s,StringComparison.Ordinal)<0),"no message/inner/address/id/path text in log");
            Check(lines.All(ValidLine),"every line is structured JSON with typed keys and identifier-only strings");
            var adapter=Parse(lines).FirstOrDefault(d=>(string)d["ev"]=="AdapterFail");
            Check(adapter!=null&&(string)adapter["Origin"]=="Packet"&&(string)adapter["ExType"]=="InvalidOperation"&&(string)adapter["ExInner"]=="IO"&&(string)adapter["cor"]==c1.ToString("x16"),"adapter failure keeps origin, inner category and correlation");
            var fatal=Parse(lines).FirstOrDefault(d=>(string)d["ev"]=="SharingFatal");
            Check(fatal!=null&&Convert.ToInt64(fatal["ExHttpStatus"])==409&&(string)fatal["ExEndReason"]=="None","media 409 with non-allowlisted end reason is normalized");
            var first=Parse(lines).First();
            Check(first.ContainsKey("pid")&&first.ContainsKey("mvid")&&first.ContainsKey("run")&&((string)first["t"]).EndsWith("Z"),"UTC timestamp, PID, run id and MVID present");
            string capLine=lines.FirstOrDefault(l=>l.Contains("\"ev\":\"MainRefinement\"")&&l.Contains(corF.ToString("x16")));
            Check(capLine!=null&&Count(capLine,"\"Value\":")==1&&capLine.Contains("\"Value\":123001")&&!capLine.Contains("123002")&&!capLine.Contains("123009"),"duplicate key first-wins; later caller mutation not retained");
            var capDict=capLine==null?null:Parse(new[]{capLine}).First();
            Check(capDict!=null&&capDict.Keys.Count(k=>Enum.IsDefined(typeof(DiagKey),k))<=DiagnosticLog.MaxFields&&!capDict.ContainsKey(keys.Where(k=>k!=DiagKey.Value).ElementAt(35).ToString()),"fields beyond 32 dropped ("+(capDict==null?0:capDict.Count)+" json keys)");
            string exLine=lines.FirstOrDefault(l=>l.Contains("\"ev\":\"MainCleanup\"")&&l.Contains(corF.ToString("x16")));
            Check(exLine!=null&&Count(exLine,"\"ExHttpStatus\":")==1&&exLine.Contains("\"ExHttpStatus\":777")&&exLine.Contains("\"ExType\":\"MediaHttp\""),"site value beats exception fact with same key; no duplicate JSON keys");
            HookAsserts(lines,corH);

            // Cross-instance/cross-session contention: another holder of the exact lock file makes the writer drop metrics
            // (immediate, no wait) but carry a bounded number of priority lines to the next attempt.
            long contentionBefore=DiagnosticLog.ContentionDrops;
            using(HoldLock(dir)){
                DiagnosticLog.Metric(DiagEvent.CaptureMetrics,c2,DiagValue.Of(DiagKey.Value,777001));
                Thread.Sleep(300);
                DiagnosticLog.Event(DiagEvent.CaptureProofExpired,c2,DiagValue.Of(DiagKey.Stage,DiagExpiryStage.AfterCapture),DiagValue.Of(DiagKey.Value,777002));
                Thread.Sleep(300);
                Check(DiagnosticLog.ContentionDrops>contentionBefore,"metric dropped under lock-file contention (observable count)");
            }
            Marker(2);
            Check(WaitMarker(2),"writer recovers after contention");
            all=String.Join("\n",AllLines());
            Check(!all.Contains("777001"),"contended metric was dropped, not written later");
            Check(all.Contains("777002"),"contended priority event carried and written after release");

            // Burst: producer never blocks; bounded queue drops metrics and the drop count is logged.
            var sw=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<200000;i++)DiagnosticLog.Metric(DiagEvent.CaptureMetrics,c2,DiagValue.Of(DiagKey.Count,i),DiagValue.Of(DiagKey.CaptureMs,12),DiagValue.Of(DiagKey.EncodeMs,30));
            long burstMs=sw.ElapsedMilliseconds;
            Check(DiagnosticLog.MetricDrops>0,"bounded queue drops excess metrics ("+DiagnosticLog.MetricDrops+")");
            Check(burstMs<5000,"200k non-blocking enqueue calls returned in "+burstMs+"ms");
            Marker(3);
            Check(WaitMarker(3),"priority marker still written after metric burst");
            Thread.Sleep(1200);
            string lockPath=Path.Combine(dir,DiagnosticLog.LockFileName);
            Check(File.Exists(lockPath)&&new FileInfo(lockPath).Length==0,"lock file present and zero bytes");
            var files=Directory.GetFiles(dir).Where(f=>Path.GetFileName(f)!=DiagnosticLog.LockFileName).ToArray();
            Check(Directory.GetFiles(dir).Length<=DiagnosticLog.FileCount+1&&files.Length<=DiagnosticLog.FileCount&&files.All(f=>Regex.IsMatch(Path.GetFileName(f),@"^remotebridge-diag-[0-2]\.jsonl$")),"only the three ring files plus lock exist ("+files.Length+")");
            Check(files.All(f=>new FileInfo(f).Length<=FileBytes),"each ring file within its size cap");
            Check(files.Sum(f=>new FileInfo(f).Length)<=FileBytes*DiagnosticLog.FileCount,"total ring size bounded");
            Check(String.Join("\n",AllLines()).Contains("\"ev\":\"LogDrops\""),"drop counters written as LogDrops event");
            Check(AllLines().All(ValidLine),"ring rotation keeps whole valid lines");

            // Bounded shutdown; afterwards logging is a no-op.
            sw.Restart();DiagnosticLog.Shutdown(1500);
            Check(sw.ElapsedMilliseconds<2500,"shutdown bounded ("+sw.ElapsedMilliseconds+"ms)");
            Check(!DiagnosticLog.Enabled,"disabled after shutdown");
            string after=String.Join("\n",AllLines());
            Check(after.Contains("\"ev\":\"ProcessShutdown\""),"shutdown event flushed");
            DiagnosticLog.Event(DiagEvent.SharingStart,c2,DiagValue.Of(DiagKey.Value,888001));Thread.Sleep(200);
            Check(!String.Join("\n",AllLines()).Contains("888001"),"events after shutdown ignored");

            ChildChecks(childRoot);
        }
        catch(Exception ex){Console.WriteLine("FAIL unexpected "+ex.GetType().Name);failures++;}
        finally{try{if(dir!=null&&Directory.Exists(dir))Directory.Delete(dir,true);}catch{}try{if(Directory.Exists(childRoot))Directory.Delete(childRoot,true);}catch{}}
        Console.WriteLine(failures==0?"DIAGNOSTIC_LOG_SMOKE PASS":"DIAGNOSTIC_LOG_SMOKE FAIL "+failures);
        return failures==0?0:1;
    }

    const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static int Count(string s,string token){int n=0;for(int i=s.IndexOf(token,StringComparison.Ordinal);i>=0;i=s.IndexOf(token,i+token.Length,StringComparison.Ordinal))n++;return n;}
    static long corA,corB,corT;

    // Actual hooks on unused sessions: generation 0 (no SetSharing network), no Start/capture/native input.
    static long HookChecks()
    {
        var prof=ServerProfile.Parse("{\"Version\":1,\"Name\":\"diag\",\"RendezvousUrl\":\"https://localhost:9999\",\"RelayUrl\":\"https://localhost:9999\",\"CertificateSha256\":\""+new string('A',64)+"\"}");
        var client=new ControlPlaneClient(prof);client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
        corA=DiagnosticLog.NewCorrelation();corB=DiagnosticLog.NewCorrelation();corT=DiagnosticLog.NewCorrelation();
        // (A) opaque 404 candidate first, then an independent timeout: settle must keep the fatal and its LastError.
        using(var s=new MediaSharingSession(client,"session-diag-a",CaptureScope.ForDesktopShare())){
            Set(s,"cts",new CancellationTokenSource());Set(s,"generation",0L);Set(s,"diagCorrelation",corA);
            RunWorker(s,new OpaquePacketHttpException(404,null));RunWorker(s,new TimeoutException("diag-timeout"));Settle(s);
            Check(Get(s,"firstCandidateError") is OpaquePacketHttpException&&Get(s,"firstIndependentFatalError") is TimeoutException,"(A) worker classified candidate then independent fatal");
            Check(s.LastError=="diag-timeout"&&s.EndReason==null,"(A) settle: independent fatal precedence, LastError retained");
        }
        // (B) typed media 409 session-ended: normal end, no error.
        using(var s=new MediaSharingSession(client,"session-diag-b",CaptureScope.ForDesktopShare())){
            Set(s,"cts",new CancellationTokenSource());Set(s,"generation",0L);Set(s,"diagCorrelation",corB);
            RunWorker(s,new MediaHttpException(409,null,"session-ended"));Settle(s);
            Check(s.LastError==null&&s.EndReason=="session-ended","(B) typed 409 session-ended settles as normal end");
        }
        // Input timeout: current poll ResponseMs and previous LastResponseMs are distinct keys.
        using(var s=new MediaSharingSession(client,"session-diag-t",CaptureScope.ForDesktopShare())){
            Set(s,"diagCorrelation",corT);typeof(MediaSharingSession).GetProperty("LastInputLeaseResponseMs",F).SetValue(s,37);
            typeof(MediaSharingSession).GetMethod("DiagInputTimeout",F).Invoke(s,new object[]{DiagInputTimeoutSite.PollCanceled,700L,12L,100,50L,-1,false});
        }
        return corA;
    }
    static void RunWorker(MediaSharingSession s,Exception ex){
        var worker=typeof(MediaSharingSession).GetMethod("Worker",F);
        var root=(CancellationTokenSource)Get(s,"cts");var ws=CancellationTokenSource.CreateLinkedTokenSource(root.Token);
        Func<CancellationToken,Task> body=t=>{throw ex;};
        Check(((Task)worker.Invoke(s,new object[]{body,ws})).Wait(5000),"worker invocation completed ("+ex.GetType().Name+")");
    }
    static void Settle(MediaSharingSession s){
        var settle=typeof(MediaSharingSession).GetMethod("SettleWorkerErrorAsync",F);
        Check(((Task)settle.Invoke(s,new object[]{(string)Get(s,"firstWorkerError")})).Wait(5000),"settle completed without network (generation 0)");
    }
    static void HookAsserts(List<string> lines,long ignored)
    {
        var all=Parse(lines).ToList();
        Func<long,string,List<Dictionary<string,object>>> ev=(c,name)=>all.Where(d=>d.ContainsKey("cor")&&(string)d["cor"]==c.ToString("x16")&&(string)d["ev"]==name).ToList();
        var firstA=ev(corA,"SharingWorkerFirstError");var classA=ev(corA,"SharingWorkerClassFirst");var fatalA=ev(corA,"SharingFatal");
        Check(firstA.Count==1&&(string)firstA[0]["Outcome"]=="Candidate"&&(string)firstA[0]["ExType"]=="OpaquePacketHttp"&&Convert.ToInt64(firstA[0]["ExHttpStatus"])==404,"(A) event: first observed error is opaque404 candidate");
        Check(classA.Count==1&&(string)classA[0]["Outcome"]=="Fatal"&&(string)classA[0]["ExType"]=="Timeout","(A) event: class-first independent timeout fatal");
        Check(fatalA.Count==1&&(string)fatalA[0]["Reason"]=="IndependentFatal"&&(string)fatalA[0]["ExType"]=="Timeout"&&ev(corA,"SharingNormalEnd").Count==0,"(A) event: settled IndependentFatal with timeout facts");
        var firstB=ev(corB,"SharingWorkerFirstError");var normalB=ev(corB,"SharingNormalEnd");
        Check(firstB.Count==1&&(string)firstB[0]["Outcome"]=="TypedNormal"&&(string)firstB[0]["ExEndReason"]=="SessionEnded","(B) event: worker typed normal 409");
        Check(normalB.Count==1&&(string)normalB[0]["Reason"]=="TypedNormal"&&(string)normalB[0]["EndReason"]=="SessionEnded"&&ev(corB,"SharingFatal").Count==0,"(B) event: SharingNormalEnd TypedNormal, no fatal");
        string tLine=lines.FirstOrDefault(l=>l.Contains("\"ev\":\"InputBudgetTimeout\"")&&l.Contains(corT.ToString("x16")));
        Check(tLine!=null&&Count(tLine,"\"ResponseMs\":")==1&&tLine.Contains("\"ResponseMs\":12")&&tLine.Contains("\"LastResponseMs\":37"),"input timeout: current ResponseMs once, previous as LastResponseMs");
    }

    // ---- child processes (this exe only) ----
    static int Child(string[] a)
    {
        try
        {
            var sw=System.Diagnostics.Stopwatch.StartNew();
            if(a[0]=="locked"||a[0]=="denied"){
                DiagnosticLog.Initialize(a[1],FileBytes);bool on=DiagnosticLog.Enabled;
                for(int i=0;i<20;i++)DiagnosticLog.Event(DiagEvent.SharingStart,1,DiagValue.Of(DiagKey.Value,660000+i));
                DiagnosticLog.Failure(DiagEvent.SharingFatal,1,new IOException("x"),DiagValue.Of(DiagKey.Value,660100));
                Thread.Sleep(400);DiagnosticLog.Shutdown(1500);
                Console.WriteLine("CHILD "+a[0]+" enabled="+on+" contention="+DiagnosticLog.ContentionDrops+" io="+DiagnosticLog.IoFailures+" ms="+sw.ElapsedMilliseconds);
                return on&&!DiagnosticLog.Enabled?0:2;
            }
            if(a[0]=="writer"){
                DiagnosticLog.Initialize(a[1],WriterFileBytes);
                for(int i=0;i<1500;i++){
                    DiagnosticLog.Event(DiagEvent.CaptureMetrics,2,DiagValue.Of(DiagKey.Count,i),DiagValue.Of(DiagKey.CaptureMs,12),DiagValue.Of(DiagKey.EncodeMs,30),DiagValue.Of(DiagKey.Width,1920),DiagValue.Of(DiagKey.Height,1080),DiagValue.Of(DiagKey.Phase,DiagFramePhase.Upload));
                    if(i%25==0)Thread.Sleep(2);
                }
                DiagnosticLog.Shutdown(3000);
                Console.WriteLine("CHILD writer contention="+DiagnosticLog.ContentionDrops+" io="+DiagnosticLog.IoFailures);
                return 0;
            }
            return 3;
        }
        catch(Exception ex){Console.WriteLine("CHILD unexpected "+ex.GetType().Name);return 4;}
    }
    const long WriterFileBytes=16*1024;
    // The writer holds the lock only briefly per batch; retry for a bounded time to take it as the fixture holder.
    static FileStream HoldLock(string folder){
        var sw=System.Diagnostics.Stopwatch.StartNew();
        while(true){try{return new FileStream(Path.Combine(folder,DiagnosticLog.LockFileName),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){if(sw.ElapsedMilliseconds>3000)throw;Thread.Sleep(10);}}
    }
    static System.Diagnostics.Process StartChild(params string[] a){
        var psi=new System.Diagnostics.ProcessStartInfo(Assembly.GetEntryAssembly().Location,String.Join(" ",a.Select(x=>"\""+x+"\""))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
        return System.Diagnostics.Process.Start(psi);
    }
    static int Finish(System.Diagnostics.Process p,int timeoutMs,out string output){
        var read=p.StandardOutput.ReadToEndAsync();
        if(!p.WaitForExit(timeoutMs)){try{p.Kill();}catch{}output="timeout";return -1;}
        output=read.Wait(2000)?read.Result.Trim():"";return p.ExitCode;
    }
    static void ChildChecks(string root)
    {
        Directory.CreateDirectory(root);string output;
        // Locked log: another session/process holds the exact lock file; child enqueues and shuts down bounded, exit 0.
        string locked=Path.Combine(root,"locked");Directory.CreateDirectory(locked);
        using(HoldLock(locked)){
            var sw=System.Diagnostics.Stopwatch.StartNew();var p=StartChild("locked",locked);int code=Finish(p,15000,out output);
            Console.WriteLine("  "+output);
            Check(code==0&&sw.ElapsedMilliseconds<10000&&output.Contains("contention=")&&!output.Contains("contention=0 "),"locked log: child nonfatal, drops counted, bounded shutdown (exit "+code+", "+sw.ElapsedMilliseconds+"ms)");
        }
        Check(!Directory.GetFiles(locked).Where(f=>Path.GetFileName(f)!=DiagnosticLog.LockFileName).Any(f=>File.ReadAllText(f).Contains("660100")),"locked log: nothing written while lock held");
        // Unusable path: an existing file used as the log directory.
        string blocker=Path.Combine(root,"not-a-dir.txt");File.WriteAllText(blocker,"x");
        {
            var sw=System.Diagnostics.Stopwatch.StartNew();var p=StartChild("denied",blocker);int code=Finish(p,15000,out output);
            Console.WriteLine("  "+output);
            Check(code==0&&sw.ElapsedMilliseconds<10000&&output.Contains("io=")&&!output.Contains("io=0"),"unusable log path: child nonfatal, IO failures counted, bounded (exit "+code+")");
            Check(File.Exists(blocker)&&File.ReadAllText(blocker)=="x","unusable log path: existing file untouched");
        }
        // Simultaneous writer processes against one directory keep the ring caps and whole lines.
        string shared=Path.Combine(root,"shared");
        var ps=new[]{StartChild("writer",shared),StartChild("writer",shared),StartChild("writer",shared)};
        var codes=ps.Select(p=>Finish(p,20000,out output)).ToArray();
        Check(codes.All(c=>c==0),"3 concurrent writer children exit 0 ("+String.Join(",",codes)+")");
        var ring=Directory.Exists(shared)?Directory.GetFiles(shared).Where(f=>Path.GetFileName(f)!=DiagnosticLog.LockFileName).ToArray():new string[0];
        var ringLines=new List<string>();foreach(var f in ring)ringLines.AddRange(File.ReadAllLines(f).Where(l=>l.Length>0));
        int pids=Parse(ringLines).Select(d=>Convert.ToInt64(d["pid"])).Distinct().Count();
        Check(ring.Length>0&&ring.Length<=DiagnosticLog.FileCount&&ring.All(f=>Regex.IsMatch(Path.GetFileName(f),@"^remotebridge-diag-[0-2]\.jsonl$")),"concurrent writers: only ring files ("+ring.Length+")");
        Check(ring.All(f=>new FileInfo(f).Length<=WriterFileBytes)&&new FileInfo(Path.Combine(shared,DiagnosticLog.LockFileName)).Length==0,"concurrent writers: each file within cap, lock zero bytes ("+String.Join(",",ring.Select(f=>new FileInfo(f).Length))+")");
        Check(ringLines.Count>0&&ringLines.All(ValidLine),"concurrent writers: whole valid lines ("+ringLines.Count+" lines, "+pids+" pids)");
    }

    static bool Has(DiagValue[] f,DiagKey k,long v){return f.Any(x=>x.Key==k&&x.Code==null&&x.Number==v);}
    static bool HasCode(DiagValue[] f,DiagKey k,Enum v){return f.Any(x=>x.Key==k&&x.Code!=null&&x.Code.Equals(v));}
    static void Marker(long id){DiagnosticLog.Event(DiagEvent.ProcessStart,0,DiagValue.Of(DiagKey.Value,990000+id));}
    static bool WaitMarker(long id){
        string token="\"Value\":"+(990000+id);var sw=System.Diagnostics.Stopwatch.StartNew();
        while(sw.ElapsedMilliseconds<5000){if(String.Join("\n",AllLines()).Contains(token))return true;Thread.Sleep(50);}
        return false;
    }
    static List<string> AllLines(){
        var result=new List<string>();if(!Directory.Exists(dir))return result;
        foreach(var f in Directory.GetFiles(dir).OrderBy(x=>x)){
            try{using(var fs=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var r=new StreamReader(fs)){string l;while((l=r.ReadLine())!=null)if(l.Length>0)result.Add(l);}}catch(IOException){}
        }
        return result;
    }
    static IEnumerable<Dictionary<string,object>> Parse(IEnumerable<string> lines){var js=new JavaScriptSerializer();foreach(var l in lines)yield return js.Deserialize<Dictionary<string,object>>(l);}
    static readonly HashSet<string> Allowed=new HashSet<string>(new[]{"v","t","run","pid","mvid","seq","ev","cor"}.Concat(Enum.GetNames(typeof(DiagKey))));
    static readonly Regex Ident=new Regex(@"^[A-Za-z0-9_\-]{1,64}$");
    static bool ValidLine(string line){
        try{
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);
            if(Convert.ToInt32(d["v"])!=1||!Enum.IsDefined(typeof(DiagEvent),(string)d["ev"]))return false;
            foreach(var kv in d){
                if(!Allowed.Contains(kv.Key))return false;
                if(kv.Key=="t"){DateTime t;if(!DateTime.TryParse((string)kv.Value,null,System.Globalization.DateTimeStyles.RoundtripKind,out t)||t.Kind!=DateTimeKind.Utc)return false;continue;}
                if(kv.Key=="mvid"){Guid g;if(!Guid.TryParse((string)kv.Value,out g))return false;continue;}
                var s=kv.Value as string;if(s!=null&&!Ident.IsMatch(s))return false;
                if(s==null&&!(kv.Value is int||kv.Value is long||kv.Value is decimal))return false;
            }
            return true;
        }catch{return false;}
    }
}
