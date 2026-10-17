using System;using System.Collections.Generic;using System.Diagnostics;using System.Reflection;using System.Threading;using PeerDesk.Windows;
internal static class Program{
static int count;static void Check(bool x,string n){if(!x)throw new Exception(n);Console.WriteLine("PASS "+n);count++;}
static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
static object Call(object o,string n,params object[] v){return o.GetType().GetMethod(n,F).Invoke(o,v);}
static bool Throws(Action a){try{a();return false;}catch{return true;}}
static Dictionary<string,object> Caps(){return new Dictionary<string,object>{{"generation",1},{"minFrameIntervalMs",50},{"inputLeasePollSupported",true},{"statefulInputVersion",1},{"inputPollIntervalMs",50},{"mediaOptimizationVersion",1}};}
static int Main(){try{
var c=Caps();Check(MediaWire.ParseCapabilities(c).MediaOptimizationVersion==1,"strict optimized capability");
c["mediaOptimizationVersion"]="1";Check(Throws(()=>MediaWire.ParseCapabilities(c)),"string opt rejected");
c=Caps();c["statefulInputVersion"]=0;Check(Throws(()=>MediaWire.ParseCapabilities(c)),"fast legacy rejected");
c=Caps();c.Remove("mediaOptimizationVersion");Check(Throws(()=>MediaWire.ParseCapabilities(c)),"unnegotiated50ms rejected");
c=Caps();c["minFrameIntervalMs"]=100;Check(Throws(()=>MediaWire.ParseCapabilities(c)),"opt wrong interval rejected");
var p=ServerProfile.Parse("{\"Version\":1,\"Name\":\"test\",\"RendezvousUrl\":\"https://localhost:9999\",\"RelayUrl\":\"https://localhost:9999\",\"CertificateSha256\":\""+new string('A',64)+"\"}");
var client=new ControlPlaneClient(p);client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
using(var m=new MediaSharingSession(client,"session-a",CaptureScope.ForDesktopShare())){
Set(m,"optimizedMedia",true);Set(m,"generation",7L);
Func<bool> fresh=()=> (bool)Call(m,"FreshFrameAuthorization",CancellationToken.None);
Action<long,long,int,Stopwatch,CancellationToken> publish=(e,g,l,t,ct)=>Call(m,"PublishFrameAuthorization",e,g,l,t,ct,client.CredentialRevision);
Check(!fresh(),"no unauthenticated capture proof");
var time=Stopwatch.StartNew();publish(0,7,100,time,CancellationToken.None);Check(fresh(),"fresh input lease authorizes same-session frame");
Thread.Sleep(110);Check(!fresh(),"original request100ms expiry");
publish(0,7,100,time,CancellationToken.None);Check(!fresh(),"republish never renews expired clock");
publish(0,8,100,Stopwatch.StartNew(),CancellationToken.None);Check(!fresh(),"wrong generation rejected");
publish(1,7,100,Stopwatch.StartNew(),CancellationToken.None);Check(!fresh(),"wrong epoch rejected");
publish(0,7,101,Stopwatch.StartNew(),CancellationToken.None);Check(!fresh(),"lease over100 rejected");
using(var ct=new CancellationTokenSource()){ct.Cancel();publish(0,7,100,Stopwatch.StartNew(),ct.Token);Check(!fresh(),"cancelled late response rejected");}
publish(0,7,100,Stopwatch.StartNew(),CancellationToken.None);Check(fresh(),"fresh proof restored");
m.CancelLocal();Check(!fresh(),"stop invalidates proof");
publish(0,7,100,Stopwatch.StartNew(),CancellationToken.None);Check(!fresh(),"old in-flight epoch cannot revive after stop");
publish(1,7,100,Stopwatch.StartNew(),CancellationToken.None);Check(fresh(),"current epoch proof accepted for isolated unit fixture");
long oldRevision=client.CredentialRevision;client.RestoreCredentials("123","other-bearer","SyntheticPw","2099-01-01T00:00:00Z");Check(!fresh(),"same device bearer replacement invalidates proof");
Call(m,"PublishFrameAuthorization",1L,7L,100,Stopwatch.StartNew(),CancellationToken.None,oldRevision);Check(!fresh(),"inflight old registration cannot publish under new bearer");
client.RestoreCredentials("456","other-bearer","SyntheticPw","2099-01-01T00:00:00Z");Check(!fresh(),"registration device change invalidates proof");
}
Console.WriteLine("MEDIA_OPTIMIZATION_SMOKE_PASS count="+count);return 0;
}catch(Exception e){Console.Error.WriteLine(e);return 1;}}}