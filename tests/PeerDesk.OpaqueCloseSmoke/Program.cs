using System;using System.IO;using System.Collections.Concurrent;using System.Diagnostics;using System.Linq;using System.Net;using System.Net.Security;using System.Net.Sockets;using System.Reflection;using System.Runtime.Serialization;using System.Security.Authentication;using System.Security.Cryptography;using System.Security.Cryptography.X509Certificates;using System.Text;using System.Threading;using System.Threading.Tasks;using PeerDesk.Windows;
// Normal-close race: an untyped opaque refusal (HTTP 410) that beats the typed 409 must not become LastError, but only an
// authenticated typed end reason may clear it. Real ControlPlaneClient/SessionTransportClient over a pinned local TLS stub.
internal static class Program{
static int count;static void Check(bool x,string n){if(!x)throw new Exception(n);Console.WriteLine("PASS "+n);count++;}
static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
const string Sid="0123456789abcdef0123456789abcdef";
static readonly ConcurrentQueue<string> log=new ConcurrentQueue<string>();
// path -> raw response, or null = hang 4s then drop the connection
static volatile Func<string,string,string> respond=(m,p)=>Reply(404,"Not Found",null,"");
static string Reply(int code,string reason,string end,string body){return "HTTP/1.1 "+code+" "+reason+"\r\n"+(end!=null?"X-PeerDesk-Media-End: "+end+"\r\n":"")+"Cache-Control: no-store\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body;}
static string Lease200(){return Reply(200,"OK",null,"{\"generation\":7,\"captureLeaseVersion\":1,\"leaseMs\":250}");}
static int Hits(string suffix){return log.Count(x=>x.EndsWith(suffix,StringComparison.Ordinal));}
static void Clear(){string s;while(log.TryDequeue(out s)){}}
static void Serve(TcpClient c,X509Certificate2 cert){
 try{using(c)using(var ssl=new SslStream(c.GetStream(),false)){
  ssl.AuthenticateAsServer(cert,false,SslProtocols.Tls12,false);
  var head=new StringBuilder();var one=new byte[1];
  while(!head.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){if(ssl.Read(one,0,1)!=1)return;head.Append((char)one[0]);if(head.Length>16384)return;}
  var lines=head.ToString().Split(new[]{"\r\n"},StringSplitOptions.None);var first=lines[0].Split(' ');
  int length=0;foreach(var l in lines)if(l.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=Int32.Parse(l.Substring(15).Trim());
  var body=new byte[length];int got=0;while(got<length){int n=ssl.Read(body,got,length-got);if(n<=0)return;got+=n;}
  log.Enqueue(first[0]+" "+first[1]);
  string r=respond(first[0],first[1]);
  if(r==null){Thread.Sleep(4000);return;}
  var bytes=Encoding.UTF8.GetBytes(r);ssl.Write(bytes,0,bytes.Length);ssl.Flush();
 }}catch{}
}
static X509Certificate2 Cert(){
 using(var rsa=RSA.Create(2048)){
  var req=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
  var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");req.CertificateExtensions.Add(san.Build());
  req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
  using(var c=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1)))
   return new X509Certificate2(c.Export(X509ContentType.Pfx,""),"",X509KeyStorageFlags.Exportable);
 }
}
static MediaSharingSession Session(ControlPlaneClient c,bool secure){
 var m=new MediaSharingSession(c,Sid,CaptureScope.ForDesktopShare());Set(m,"generation",7L);Set(m,"captureLeased",secure);
 var binding=m.LiveCaptureBinding();var root=new CancellationTokenSource();Set(m,"cts",root);Set(m,"frozenCloseRoot",root);
 var frozenJson="{\"Version\":1,\"Name\":\"frozen-test\",\"RendezvousUrl\":\""+binding.ServerUrl+"\",\"RelayUrl\":\""+binding.ServerUrl+"\",\"CertificateSha256\":\""+binding.PinFingerprint+"\"}";
 var frozen=new ControlPlaneClient(ServerProfile.Parse(frozenJson));frozen.RestoreCredentials(c.DeviceId,c.Token,c.TemporaryPassword,c.PasswordExpiresAt);Set(m,"frozenCloseClient",frozen);Set(m,"frozenCloseBinding",binding);
 if(secure){var w=Get(m,"wire");w.GetType().GetProperty("Secure",F|BindingFlags.Public).SetValue(w,FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter)));}
 return m;
}
static void Detach(MediaSharingSession m){var w=Get(m,"wire");w.GetType().GetProperty("Secure",F|BindingFlags.Public).SetValue(w,null);}
static void Attach(MediaSharingSession m){var w=Get(m,"wire");w.GetType().GetProperty("Secure",F|BindingFlags.Public).SetValue(w,FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter)));}
// Synthetic adapter has no transport to dispose: detach around local cancel, then re-attach so settle still sees secure mode.
static void CancelLocal(MediaSharingSession m,string reason){Detach(m);m.CancelLocal(reason);Attach(m);}
static void Release(MediaSharingSession m){Detach(m);m.Dispose();}
static void Fail(MediaSharingSession m,Exception ex){
 var worker=typeof(MediaSharingSession).GetMethod("Worker",F);var ws=new CancellationTokenSource();
 Func<CancellationToken,Task> body=t=>{throw ex;};((Task)worker.Invoke(m,new object[]{body,ws})).Wait();
}
static void Settle(MediaSharingSession m){((Task)typeof(MediaSharingSession).GetMethod("SettleWorkerErrorAsync",F).Invoke(m,new object[]{(string)Get(m,"firstWorkerError")})).Wait();}
static int Main(){try{
 var cert=Cert();string pin=BitConverter.ToString(SHA256.Create().ComputeHash(cert.RawData)).Replace("-","");
 var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
 new Thread(()=>{while(true){TcpClient c;try{c=listener.AcceptTcpClient();}catch{return;}ThreadPool.QueueUserWorkItem(_=>Serve(c,cert));}}){IsBackground=true}.Start();
 var prof=ServerProfile.Parse("{\"Version\":1,\"Name\":\"stub\",\"RendezvousUrl\":\"https://localhost:"+port+"\",\"RelayUrl\":\"https://localhost:"+port+"\",\"CertificateSha256\":\""+pin+"\"}");
 var client=new ControlPlaneClient(prof);client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
 // The real opaque transport refusal the target sees when the viewer close revokes the session first.
 respond=(m,p)=>p.EndsWith("/opaque/descriptor",StringComparison.Ordinal)?Reply(410,"Gone",null,""):Reply(404,"Not Found",null,"");
 Exception opaque=null;using(var t=new SessionTransportClient(client,Sid)){try{t.GetDescriptorAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(Exception ex){opaque=ex;}}
 Check(opaque!=null&&opaque.Message=="Opaque endpoint returned HTTP 410."&&!(opaque is MediaHttpException),"real opaque 410 is untyped InvalidOperation");
 var typedEnd=new MediaHttpException(409,null,"session-ended");
 Check(typedEnd.EndReason=="session-ended","typed 409 session-ended carries end reason");

 // 1. Candidate opaque 404 is cleared only by authenticated typed 409 session-ended.
 Clear();respond=(m,p)=>Reply(409,"Conflict","session-ended","");
 var candidate=new OpaquePacketHttpException(404,null);
 var s=Session(client,true);Fail(s,candidate);Fail(s,typedEnd);
 Check((string)Get(s,"firstWorkerError")==candidate.Message&&Get(s,"firstWorkerEndReason")==null,"candidate opaque fallout recorded first, typed loser ignored");
 Settle(s);
 Check(s.LastError==null&&s.EndReason=="session-ended","candidate plus authenticated end: normal");
 Check(Hits("/capture-lease")==1&&Hits("/sharing")==0,"exactly one capture-lease probe, no sharing=false after typed normal end");
 Detach(s);s.StopAsync().Wait();Check(s.LastError==null&&s.EndReason=="session-ended"&&Hits("/sharing")==0,"StopAsync keeps confirmed end, no relabel");Release(s);

 // 2. Session still alive (probe 200): genuine opaque failure stays an error and sharing=false is sent.
 Clear();respond=(m,p)=>p.EndsWith("/capture-lease",StringComparison.Ordinal)?Lease200():Reply(200,"OK",null,"{\"generation\":7}");
 s=Session(client,true);Fail(s,candidate);Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null,"live session: opaque failure stays LastError");
 Check(Hits("/capture-lease")==1&&Hits("/sharing")==1,"live session: one probe then sharing=false");Release(s);

 // 3. Untyped 409 probe is never assumed normal.
 Clear();respond=(m,p)=>Reply(409,"Conflict",null,"");
 s=Session(client,true);Fail(s,candidate);Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null,"untyped 409 probe: error kept");Release(s);

 // 4. Typed non-normal reason is not treated as session-ended and does not clear the error.
 Clear();respond=(m,p)=>p.EndsWith("/capture-lease",StringComparison.Ordinal)?Reply(409,"Conflict","expired",""):Reply(409,"Conflict",null,"");
 s=Session(client,true);Fail(s,candidate);Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null,"typed expired: error kept, normal end not inferred");Release(s);

 // 5. Typed reason from a spoofable source is ignored: header on a non-409 response.
 Clear();respond=(m,p)=>p.EndsWith("/capture-lease",StringComparison.Ordinal)?Reply(410,"Gone","session-ended",""):Reply(409,"Conflict",null,"");
 s=Session(client,true);Fail(s,candidate);Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null,"end header on non-409 not trusted");Release(s);

 // 6. Probe hang is bounded (~2s) and keeps the error.
 Clear();respond=(m,p)=>p.EndsWith("/capture-lease",StringComparison.Ordinal)?null:Reply(409,"Conflict",null,"");
 s=Session(client,true);Fail(s,candidate);var sw=Stopwatch.StartNew();Settle(s);
 Check(s.LastError==candidate.Message&&sw.ElapsedMilliseconds<1800,"probe timeout bounded ("+sw.ElapsedMilliseconds+"ms), error kept");Release(s);

 // 7. Non-secure sessions never probe: legacy semantics unchanged.
 Clear();respond=(m,p)=>Reply(409,"Conflict","session-ended","");
 s=Session(client,false);Fail(s,new InvalidOperationException("legacy failure"));Settle(s);
 Check(s.LastError=="legacy failure"&&Hits("/capture-lease")==0,"non-secure: no probe, error kept");Release(s);

 // 8. Local cancellation alone cannot classify opaque fallout as a confirmed server end.
 Clear();respond=(m,p)=>Reply(409,"Conflict",null,"");
 s=Session(client,true);Fail(s,candidate);CancelLocal(s,"session-ended");Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null&&Hits("/capture-lease")==0&&Hits("/sharing")==1,"CancelLocal session-ended leaves candidate error, no probe");
 Release(s);
 s=Session(client,true);Fail(s,candidate);CancelLocal(s,"eof");Settle(s);
 Check(s.LastError==candidate.Message&&s.EndReason==null&&Hits("/capture-lease")==0,"unknown CancelLocal reason not trusted");Release(s);

 // 9. MAC/integrity failure always wins over a typed normal-end candidate, independent of arrival order.
 for(int order=0;order<3;order++){
  Clear();respond=(m,p)=>Reply(409,"Conflict","session-ended","");
  s=Session(client,true);var mac=new InvalidDataException("MAC fixture");
  if(order==0){Fail(s,mac);Fail(s,typedEnd);}
  else if(order==1){Fail(s,typedEnd);Fail(s,mac);}
  else {Fail(s,candidate);Fail(s,mac);Fail(s,typedEnd);}
  Settle(s);
  Check(s.LastError=="MAC fixture"&&s.EndReason==null&&Hits("/capture-lease")==0,"MAC wins order "+order+" with no probe");Release(s);
 }
 // 10. Generic opaque HTTP 410 is not a candidate, even after CancelLocal confirms session-ended.
 Clear();respond=(m,p)=>Reply(409,"Conflict","session-ended","");
 s=Session(client,true);Fail(s,opaque);CancelLocal(s,"session-ended");Settle(s);
 Check(s.LastError=="Opaque endpoint returned HTTP 410."&&s.EndReason==null&&Hits("/capture-lease")==0,"generic 410 remains error after local normal-end signal");Release(s);
 // 11. Typed normal worker result alone still classifies directly without a probe.
 Clear();s=Session(client,true);Fail(s,typedEnd);Settle(s);
 Check(s.LastError==null&&s.EndReason=="session-ended"&&Hits("/capture-lease")==0,"typed worker 409 alone: normal, no probe");Release(s);

 listener.Stop();Console.WriteLine("OpaqueClose smoke passed: "+count);return 0;}catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}}
}
