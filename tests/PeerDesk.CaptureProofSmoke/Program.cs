using System;using System.Collections.Generic;using System.Diagnostics;using System.Reflection;using System.Runtime.Serialization;using System.Threading;using PeerDesk.Windows;
internal static class Program{
static int count;static void Check(bool x,string n){if(!x)throw new Exception(n);Console.WriteLine("PASS "+n);count++;}
static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
static bool Throws(Action a){try{a();return false;}catch{return true;}}
static bool ThrowsExpired(Action a){try{a();return false;}catch(CaptureProofExpiredException){return true;}catch{return false;}}
static bool ThrowsFatal(Action a){try{a();return false;}catch(CaptureProofExpiredException){return false;}catch{return true;}}
static Dictionary<string,object> Lease(){return new Dictionary<string,object>{{"generation",7},{"captureLeaseVersion",1},{"leaseMs",250}};}
static Dictionary<string,object> Caps(){return new Dictionary<string,object>{{"generation",7},{"minFrameIntervalMs",100},{"inputLeasePollSupported",true},{"statefulInputVersion",1},{"inputPollIntervalMs",50},{"mediaOptimizationVersion",1},{"captureLeaseVersion",1},{"captureLeaseMs",250}};}
static CaptureProofBinding B(long gen=7,long epoch=1,long rev=2,string tok="T"){return new CaptureProofBinding("s","c","d",tok,rev,"https://h","PIN",epoch,gen,"scope");}
static SessionTransportDescriptor D(string sid,string target,bool strict,long gen=7){return strict?new SessionTransportDescriptor(sid,"init",target,"e",gen,1,1,1,1,250,100,1):new SessionTransportDescriptor(sid,"init",target,"e",gen,1,1,1,0,0,0,0);}
// Synthetic key-confirmed adapter: no network, no handshake; only Ready/Descriptor are exercised.
static SecureSessionAdapter Adapter(SessionTransportDescriptor d){
var ch=FormatterServices.GetUninitializedObject(typeof(SecurePeerChannel));Set(ch,"gate",new object());Set(ch,"established",true);Set(ch,"sentFinished",true);Set(ch,"receivedFinished",true);
var a=(SecureSessionAdapter)FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter));Set(a,"crypto",ch);Set(a,"lifetime",new CancellationTokenSource());Set(a,"sync",new object());Set(a,"descriptor",d);
Check(a.Ready,"synthetic adapter ready");count--;return a;}
static int Main(){try{
// strict exact-3 capture lease parsing
Check(MediaWire.ParseCaptureLease(Lease(),7)==7,"exact capture lease accepted");
var d=Lease();d.Remove("leaseMs");d["captureLeaseMs"]=250;Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"captureLeaseMs name rejected");
d=Lease();d["extra"]=1;Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"extra field rejected");
d=Lease();d.Remove("captureLeaseVersion");Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"missing version rejected");
d=Lease();d["leaseMs"]=100;Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"leaseMs100 rejected");
d=Lease();d["leaseMs"]="250";Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"string leaseMs rejected");
d=Lease();d["captureLeaseVersion"]=2;Check(Throws(()=>MediaWire.ParseCaptureLease(d,7)),"version2 rejected");
Check(Throws(()=>MediaWire.ParseCaptureLease(Lease(),8)),"generation mismatch rejected");
Check(Throws(()=>MediaWire.ParseCaptureLease(null,7)),"null rejected");
Check(MediaWire.ParseCaptureLeaseJson("{\"generation\":7,\"captureLeaseVersion\":1,\"leaseMs\":250}",7)==7,"raw json accepted");
Check(Throws(()=>MediaWire.ParseCaptureLeaseJson("{\"generation\":7,\"captureLeaseVersion\":1,\"leaseMs\":250,\"leaseMs\":250}",7)),"duplicate raw key rejected");
Check(Throws(()=>MediaWire.ParseCaptureLeaseJson("{\"generation\":8,\"generation\":7,\"captureLeaseVersion\":1,\"leaseMs\":250}",7)),"duplicate last-wins generation rejected");
Check(Throws(()=>MediaWire.ParseCaptureLeaseJson("{\"generation\":7,\"captureLeaseVersion\":1,\"lease\\u004ds\":250,\"leaseMs\":250}",7)),"escaped duplicate rejected");
Check(Throws(()=>MediaWire.ParseCaptureLeaseJson("{\"generation\":7,\"captureLeaseVersion\":1,\"leaseMs\":250} {}",7)),"trailing data rejected");
// strict capture capabilities
Check(MediaWire.ParseCapabilities(Caps()).CaptureLeased,"strict capture caps accepted");
var c=Caps();c["captureLeaseMs"]=100;Check(Throws(()=>MediaWire.ParseCapabilities(c)),"caps captureLeaseMs100 rejected");
c=Caps();c["minFrameIntervalMs"]=50;Check(Throws(()=>MediaWire.ParseCapabilities(c)),"capture caps 50ms frame rejected");
c=Caps();c.Remove("captureLeaseVersion");Check(Throws(()=>MediaWire.ParseCapabilities(c)),"captureLeaseMs without version rejected");
c=Caps();c["statefulInputVersion"]=0;c.Remove("inputPollIntervalMs");Check(Throws(()=>MediaWire.ParseCapabilities(c)),"capture without stateful rejected");
const string capsJson="{\"generation\":7,\"minFrameIntervalMs\":100,\"inputLeasePollSupported\":true,\"statefulInputVersion\":1,\"inputPollIntervalMs\":50,\"mediaOptimizationVersion\":1,\"captureLeaseVersion\":1,\"captureLeaseMs\":250";
Check(MediaWire.ParseCaptureLeaseCapabilitiesJson(capsJson+"}").CaptureLeased,"raw capture caps accepted");
Check(Throws(()=>MediaWire.ParseCaptureLeaseCapabilitiesJson(capsJson+",\"captureLeaseMs\":250}")),"duplicate raw caps key rejected");
Check(Throws(()=>MediaWire.ParseCaptureLeaseCapabilitiesJson(capsJson.Replace("{\"generation\":7","{\"generation\":9,\"generation\":7")+"}")),"duplicate last-wins caps generation rejected");
// old optimized100ms input lease unchanged
Check(MediaWire.MaxInputLeaseMs==100&&MediaWire.CaptureLeaseMs==250&&MediaWire.MinFrameIntervalFloorMs==100,"input100 capture250 cadence100 constants");
// proof expiry and binding
var p=CaptureProof.Create(7,B(),Stopwatch.GetTimestamp());
p.Check(B());Check(!p.Expired,"fresh proof valid");
Check(Throws(()=>CaptureProof.Create(8,B(),Stopwatch.GetTimestamp())),"proof generation mismatch");
Check(ThrowsFatal(()=>p.Check(B(epoch:2))),"epoch change fatal");
Check(ThrowsFatal(()=>p.Check(B(rev:4))),"credential revision change fatal");
Check(ThrowsFatal(()=>p.Check(B(tok:"U"))),"token change fatal");
Check(ThrowsFatal(()=>p.Check(B(gen:8))),"generation change fatal");
Check(ThrowsFatal(()=>p.Check(null)),"null live context fatal");
var old=CaptureProof.Create(7,B(),Stopwatch.GetTimestamp()-260*Stopwatch.Frequency/1000);
Check(ThrowsExpired(()=>old.Check(B())),"request-start250 pure expiry");
Check(ThrowsFatal(()=>old.Check(B(epoch:9))),"binding mismatch beats expiry");
var edge=CaptureProof.Create(7,B(),Stopwatch.GetTimestamp());Thread.Sleep(270);Check(ThrowsExpired(()=>edge.Check(B())),"held proof never renewed");
// session context, drops, fatal paths
var prof=ServerProfile.Parse("{\"Version\":1,\"Name\":\"test\",\"RendezvousUrl\":\"https://localhost:9999\",\"RelayUrl\":\"https://localhost:9999\",\"CertificateSha256\":\""+new string('A',64)+"\"}");
var client=new ControlPlaneClient(prof);client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
using(var m=new MediaSharingSession(client,"session-a",CaptureScope.ForDesktopShare())){
Set(m,"generation",7L);
var b1=m.LiveCaptureBinding();Check(b1.SameAs(m.LiveCaptureBinding()),"live binding stable");
Check(b1.TokenFingerprint.Length==64&&b1.TokenFingerprint.IndexOf("synthetic",StringComparison.OrdinalIgnoreCase)<0,"token fingerprint not raw token");
// prepared capture-v1 attach requires a strict descriptor for this session and this target device
Set(m,"preparedCaps",MediaWire.ParseCapabilities(Caps()));
Check(Throws(()=>m.AttachSecure(Adapter(D("session-a",client.DeviceId,false)))),"attach legacy descriptor rejected");
Check(Throws(()=>m.AttachSecure(Adapter(D("session-b",client.DeviceId,true)))),"attach other-session descriptor rejected");
Check(Throws(()=>m.AttachSecure(Adapter(D("session-a","999",true)))),"attach other-target descriptor rejected");
Check(Throws(()=>m.AttachSecure(Adapter(D("session-a",client.DeviceId,true,8)))),"attach other-generation descriptor rejected");
m.AttachSecure(Adapter(D("session-a",client.DeviceId,true)));Check(true,"attach strict matching descriptor accepted");
var wireObj=m.GetType().GetField("wire",F).GetValue(m);wireObj.GetType().GetProperty("Secure",F|BindingFlags.Public).SetValue(wireObj,null);Set(m,"preparedCaps",null);
var proof=CaptureProof.Create(7,b1,Stopwatch.GetTimestamp());
Check(ThrowsFatal(()=>m.CheckCaptureProof(proof,CancellationToken.None)),"inactive capture-v1 fatal");
Set(m,"running",true);Set(m,"captureLeased",true);
m.CheckCaptureProof(proof,CancellationToken.None);Check(true,"active fresh proof passes");
using(var cts=new CancellationTokenSource()){cts.Cancel();Check(Throws(()=>m.CheckCaptureProof(proof,cts.Token))&&!ThrowsExpired(()=>m.CheckCaptureProof(proof,cts.Token)),"cancel fatal not expiry");}
Set(m,"generation",8L);Check(ThrowsFatal(()=>m.CheckCaptureProof(proof,CancellationToken.None)),"generation change fatal in session");Set(m,"generation",7L);
var stale=CaptureProof.Create(7,m.LiveCaptureBinding(),Stopwatch.GetTimestamp()-300*Stopwatch.Frequency/1000);
Check(ThrowsExpired(()=>m.CheckCaptureProof(stale,CancellationToken.None)),"session pure expiry retryable");
m.ObserveCaptureProofExpiry();m.ObserveCaptureProofExpiry();Check(Throws(()=>m.ObserveCaptureProofExpiry()),"third pre-seal expiry fatal");
Set(m,"captureProofExpiryDrops",0);m.ObserveCaptureProofExpiry();m.ObserveCaptureProofExpiry();Set(m,"captureProofExpiryDrops",0);m.ObserveCaptureProofExpiry();m.ObserveCaptureProofExpiry();Check(true,"successful-send reset allows new window");
Set(m,"running",false);
m.CancelLocal();Check(ThrowsFatal(()=>proof.Check(m.LiveCaptureBinding())),"stop bumps local epoch: proof dead");
}
// local cancel vs worker errors: reflection-only Worker, no network (generation 0 skips sharing=false)
using(var w=new MediaSharingSession(client,"session-w",CaptureScope.ForDesktopShare())){
var worker=typeof(MediaSharingSession).GetMethod("Worker",F);
Func<Exception,string> run=ex=>{var root=(CancellationTokenSource)w.GetType().GetField("cts",F).GetValue(w);var ws=CancellationTokenSource.CreateLinkedTokenSource(root.Token);
Func<CancellationToken,System.Threading.Tasks.Task> body=t=>{throw ex;};((System.Threading.Tasks.Task)worker.Invoke(w,new object[]{body,ws})).Wait();return (string)w.GetType().GetField("firstWorkerError",F).GetValue(w);};
Action fresh=()=>{Set(w,"cts",new CancellationTokenSource());Set(w,"localCancelSource",null);Set(w,"firstWorkerError",null);Set(w,"firstWorkerEndReason",null);};
fresh();Check(run(new ObjectDisposedException("secure"))!=null,"ODE without local intent is error");
fresh();((CancellationTokenSource)w.GetType().GetField("cts",F).GetValue(w)).Cancel();Check(run(new ObjectDisposedException("secure"))!=null,"ODE after cancel without recorded intent is error");
fresh();Set(w,"localCancelSource",new CancellationTokenSource());Check(run(new ObjectDisposedException("secure"))!=null,"ODE with intent for other cts is error");
fresh();w.CancelLocal();Check(run(new ObjectDisposedException("secure"))==null,"ODE after local intent+cancel suppressed");
fresh();w.CancelLocal();Check(run(new OperationCanceledException())==null,"OCE after local intent+cancel suppressed");
fresh();w.CancelLocal();Check(run(new System.IO.IOException("eof"))=="eof","IOException after local cancel stays error");
fresh();w.CancelLocal();Check(run(new System.Security.Authentication.AuthenticationException("mac"))=="mac","MAC/auth after local cancel stays error");
fresh();Set(w,"firstWorkerError","earlier");w.CancelLocal();Check(run(new ObjectDisposedException("secure"))=="earlier"&&run(new System.IO.IOException("eof"))=="earlier","earlier worker error preserved");
var lastError=typeof(MediaSharingSession).GetProperty("LastError",F);var endReason=typeof(MediaSharingSession).GetProperty("EndReason",F);
fresh();Set(w,"generation",0L);lastError.SetValue(w,"earlier");endReason.SetValue(w,null);w.StopAsync().Wait();
Check(w.LastError=="earlier"&&w.EndReason==null,"StopAsync preserves earlier error, no user-stopped relabel");
fresh();lastError.SetValue(w,null);endReason.SetValue(w,null);w.StopAsync().Wait();
Check(w.LastError==null&&w.EndReason=="user-stopped","clean StopAsync is user-stopped");
fresh();lastError.SetValue(w,null);endReason.SetValue(w,null);w.CancelLocal("eof");Check(w.EndReason==null,"unknown end reason not trusted");
w.CancelLocal("session-ended");Check(w.EndReason=="session-ended","confirmed session-ended trusted");
}
Console.WriteLine("CaptureProof smoke passed: "+count);return 0;}catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}}
}
