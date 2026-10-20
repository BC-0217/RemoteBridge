using System;using System.Collections.Concurrent;using System.Collections.Generic;using System.IO;using System.Net;using System.Net.Security;using System.Net.Sockets;using System.Reflection;
using System.Security.Authentication;using System.Security.Cryptography;using System.Security.Cryptography.X509Certificates;using System.Text;using System.Threading;using System.Threading.Tasks;using PeerDesk.Windows;
// Drives the real SecureSessionAdapter.SendFrameAsync with real SessionTransportClient over an owned pinned-HTTPS loopback
// relay and a real loopback direct TCP connection. Only the Hello/Finished exchange and the direct epoch install are
// done via reflection (initialized crypto state); every send boundary under test is product code. Nothing is skipped.
internal static class Program {
 const string Sid="00112233445566778899aabbccddeeff",Epoch="0123456789abcdef0123456789abcdef",Initiator="device-source",Target="device-target";
 static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
 static readonly ConcurrentQueue<byte[]> relayed=new ConcurrentQueue<byte[]>();
 static volatile bool strictMode=true;static X509Certificate2 cert;static string url,pin;static int n;
 static void Check(bool b,string s){if(!b)throw new Exception("FAIL "+s);n++;Console.WriteLine("PASS "+s);}
 static int Main(){var sw=System.Diagnostics.Stopwatch.StartNew();try{var run=Run();if(!run.Wait(TimeSpan.FromSeconds(180)))throw new TimeoutException("budget 180s exceeded");Console.WriteLine("CAPTURE_SEND_GUARD_PASS count="+n+" ms="+sw.ElapsedMilliseconds);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}

 sealed class Guard{internal int Calls;internal int ExpireFrom=int.MaxValue;
  internal void Invoke(){Calls++;if(Calls>=ExpireFrom)throw new CaptureProofExpiredException();}}
 sealed class Rig:IDisposable{
  internal SecureSessionAdapter Adapter;internal SecurePeerChannel Peer;internal SecurePeerIdentity Mine=new SecurePeerIdentity(),Theirs=new SecurePeerIdentity();
  internal SecurePeerChannel DirectPeer;internal NetworkStream DirectPeerStream;internal TcpClient DirectLocal,DirectRemote;internal TcpListener DirectListener;
  public void Dispose(){Adapter.Dispose();Peer.Dispose();if(DirectPeer!=null)DirectPeer.Dispose();if(DirectRemote!=null)DirectRemote.Close();if(DirectListener!=null)DirectListener.Stop();Mine.Dispose();Theirs.Dispose();}
 }
 static async Task<Rig> NewRig(bool strict,bool direct){
  strictMode=strict;
  var profile=ServerProfile.Parse("{\"Version\":1,\"Name\":\"guard\",\"RendezvousUrl\":\""+url+"\",\"RelayUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\"}");
  var cp=new ControlPlaneClient(profile);cp.RestoreCredentials(Target,"synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
  var transport=new SessionTransportClient(cp,Sid);
  var d=await transport.ConfigureAsync(15,7,3,CancellationToken.None).ConfigureAwait(false);
  Check(d.IsStrictCaptureV1==strict,(strict?"strict 12-field":"legacy 8-field")+" descriptor via real configure");
  var r=new Rig();r.Adapter=new SecureSessionAdapter(cp,transport,d,r.Mine,CancellationToken.None);
  var mine=(SecurePeerChannel)typeof(SecureSessionAdapter).GetField("crypto",F).GetValue(r.Adapter);
  r.Peer=new SecurePeerChannel(r.Theirs,SecureSessionAdapter.Context(cp,d),true);
  Establish(mine,r.Mine,r.Peer,r.Theirs);
  Check(r.Adapter.Ready&&!r.Adapter.IsInitiator,"adapter keys confirmed as frame sender");
  if(direct){
   byte[] ctx=(byte[])typeof(SecureSessionAdapter).GetMethod("DirectContext",F).Invoke(r.Adapter,null);
   var local=new SecurePeerChannel(r.Mine,ctx,false);r.DirectPeer=new SecurePeerChannel(r.Theirs,ctx,true);Establish(local,r.Mine,r.DirectPeer,r.Theirs);
   r.DirectListener=new TcpListener(IPAddress.Loopback,0);r.DirectListener.Start();
   r.DirectLocal=new TcpClient();var accept=r.DirectListener.AcceptTcpClientAsync();
   await r.DirectLocal.ConnectAsync(IPAddress.Loopback,((IPEndPoint)r.DirectListener.LocalEndpoint).Port).ConfigureAwait(false);
   r.DirectRemote=await accept.ConfigureAwait(false);r.DirectPeerStream=r.DirectRemote.GetStream();
   typeof(SecureSessionAdapter).GetField("directCrypto",F).SetValue(r.Adapter,local);
   typeof(SecureSessionAdapter).GetField("directConnection",F).SetValue(r.Adapter,new DirectPeerConnection(r.DirectLocal,CancellationToken.None));
   Check(r.Adapter.HasDirectScreen,"direct epoch installed with independent keys");
  }
  return r;
 }
 static void Establish(SecurePeerChannel a,SecurePeerIdentity ai,SecurePeerChannel b,SecurePeerIdentity bi){
  a.Establish(b.Hello,bi.Fingerprint);b.Establish(a.Hello,ai.Fingerprint);var fa=a.CreateFinished();var fb=b.CreateFinished();a.ReceiveFinished(fb);b.ReceiveFinished(fa);
  if(!a.KeyConfirmed||!b.KeyConfirmed)throw new Exception("keys not confirmed");
 }
 static FramePacket Frame(long seq){return new FramePacket{Generation=2,Sequence=seq,Width=2,Height=2,Jpeg=new byte[]{0xFF,0xD8,(byte)seq,0xFF,0xD9}};}
 static long OpenSeq(SecurePeerChannel peer,byte[] cipher){var plain=peer.Open(1,cipher);return BitConverter.ToInt64(plain,8);}
 static List<byte[]> Drain(){var l=new List<byte[]>();byte[] p;while(relayed.TryDequeue(out p))l.Add(p);return l;}
 static async Task<Exception> Catch(Task t){try{await t.ConfigureAwait(false);return null;}catch(Exception e){return e;}}
 static bool IsPureExpiry(Exception e){return e!=null&&e.GetType()==typeof(CaptureProofExpiredException);}
 static bool IsWrappedFatal(Exception e){return e!=null&&!(e is CaptureProofExpiredException)&&e is InvalidOperationException&&e.InnerException is CaptureProofExpiredException;}

 static async Task Run(){
  cert=CreateCert();pin=CertPin(cert);var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
  url="https://localhost:"+((IPEndPoint)listener.LocalEndpoint).Port;var serve=Task.Run(()=>Serve(listener));
  try{
   // Descriptor parser: strict 12 / legacy 8, partial and wrong-value shapes rejected (real parser, no skip path).
   Func<string,SessionTransportDescriptor> P=j=>{try{return SessionTransportClient.ParseDescriptorValues(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(j).ToLongs());}catch(InvalidDataException){return null;}};
   string b="\"sessionId\":\""+Sid+"\",\"initiatorDeviceId\":\"i\",\"targetDeviceId\":\"t\",\"transportEpoch\":\""+Epoch+"\",\"sharingGeneration\":1,\"permissionBits\":3,\"permissionRevision\":1,\"geometryVersion\":1";
   string v1=",\"captureLeaseVersion\":1,\"captureLeaseMs\":250,\"minFrameIntervalMs\":100,\"transportKeepaliveVersion\":1";
   var legacy=P("{"+b+"}");var strictD=P("{"+b+v1+"}");
   Check(legacy!=null&&!legacy.IsStrictCaptureV1,"legacy 8-field descriptor parses as non-v1");
   Check(strictD!=null&&strictD.IsStrictCaptureV1,"strict 12-field descriptor parses as capture v1");
   Check(P("{"+b+",\"captureLeaseVersion\":1,\"captureLeaseMs\":250,\"minFrameIntervalMs\":100}")==null,"partial capture fields rejected");
   Check(P("{"+b+v1.Replace("\"captureLeaseMs\":250","\"captureLeaseMs\":0")+"}")==null,"zero captureLeaseMs rejected");
   Check(P("{"+b+v1.Replace("\"transportKeepaliveVersion\":1","\"transportKeepaliveVersion\":0")+"}")==null,"zero keepalive rejected");
   Check(P("{"+b+v1.Replace("\"minFrameIntervalMs\":100","\"minFrameIntervalMs\":60")+"}")==null,"wrong minFrameIntervalMs rejected");

   // 1. Strict v1 requires the guard; channel-1 SendAsync bypass rejected. Legacy keeps guard optional.
   using(var r=await NewRig(true,false)){
    var e=await Catch(r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None));
    Check(e is InvalidOperationException&&!(e is CaptureProofExpiredException)&&Drain().Count==0,"strict v1 SendFrameAsync without guard rejected, nothing sent");
    e=await Catch(r.Adapter.SendAsync(1,new byte[]{1,2,3},CancellationToken.None));
    Check(e is InvalidOperationException&&Drain().Count==0,"strict v1 SendAsync(channel 1) bypass rejected");
    Check(r.Adapter.Ready,"guard misuse rejections do not consume a sequence");
    // 2. Pre-seal expiry: pure CaptureProofExpiredException, no Fail, no adapter counter (5 > old cap of 3), seq continuous.
    for(int i=0;i<5;i++){var g=new Guard{ExpireFrom=1};e=await Catch(r.Adapter.SendFrameAsync(Frame(10+i),CancellationToken.None,g.Invoke));
     if(!IsPureExpiry(e)||!r.Adapter.Ready)Check(false,"pre-seal expiry #"+(i+1)+" stays pure/ready");}
    var g2=new Guard{ExpireFrom=2};e=await Catch(r.Adapter.SendFrameAsync(Frame(20),CancellationToken.None,g2.Invoke));
    Check(IsPureExpiry(e)&&r.Adapter.Ready&&g2.Calls==2,"relay pre-Seal recheck expiry is pure drop");
    Check(Drain().Count==0,"5+1 pre-seal expiries: zero relay bytes, adapter has no own drop counter");
    var ok=new Guard();await r.Adapter.SendFrameAsync(Frame(21),CancellationToken.None,ok.Invoke);
    var got=Drain();Check(got.Count==1&&OpenSeq(r.Peer,got[0])==21,"next valid frame opens at continuous relay crypto sequence");
    Check(ok.Calls==4,"relay guard ran at entry, pre-Seal, queue tail and pre-body (4 calls)");
   }
   using(var r=await NewRig(false,false)){
    await r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None);var got=Drain();
    Check(got.Count==1&&OpenSeq(r.Peer,got[0])==1,"legacy 8-field: guard optional, frame delivered");
   }
   // 3. Relay post-Seal expiry at queue tail and at pre-body: wrapped fatal, adapter not ready, never a drop.
   foreach(int at in new[]{3,4}){
    using(var r=await NewRig(true,false)){
     var g=new Guard{ExpireFrom=at};var e=await Catch(r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None,g.Invoke));
     await Task.Delay(300).ConfigureAwait(false);
     Console.WriteLine("  observed: "+(e==null?"none":e.GetType().Name+" / "+(e.InnerException==null?"-":e.InnerException.GetType().Name))+" calls="+g.Calls);
     Check(IsWrappedFatal(e)&&!r.Adapter.Ready&&r.Adapter.Failure!=null&&Drain().Count==0,"relay post-Seal expiry ("+(at==3?"queue tail":"after GetRequestStream, before body")+") is wrapped fatal, ready=false, no body");
     var again=await Catch(r.Adapter.SendFrameAsync(Frame(2),CancellationToken.None,new Guard().Invoke));
     Check(again!=null&&Drain().Count==0,"post-Seal failure is terminal: no retry on same epoch");
    }
   }
   // 4. Direct happy path uses direct keys, not relay.
   using(var r=await NewRig(true,true)){
    var g=new Guard();await r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None,g.Invoke);
    var pkt=await DirectPeerOffer.ReadPacket(r.DirectPeerStream,524372,CancellationToken.None).ConfigureAwait(false);
    Check(OpenSeq(r.DirectPeer,pkt)==1&&Drain().Count==0&&g.Calls==5,"direct frame delivered on direct epoch (guard: entry, pre-Seal, header, body, flush)");
    // 5. Direct IOException with fresh original proof: direct dropped, SAME frame on relay after guard recheck.
    r.DirectLocal.Client.Shutdown(SocketShutdown.Send);
    var f=new Guard();await r.Adapter.SendFrameAsync(Frame(2),CancellationToken.None,f.Invoke);
    var got=Drain();
    Check(!r.Adapter.HasDirectScreen&&r.Adapter.Ready,"direct transport failure drops direct epoch permanently, adapter ready");
    Check(got.Count==1&&OpenSeq(r.Peer,got[0])==2,"same frame falls back to relay with independent relay keys/seq");
    Check(f.Calls==6,"fallback re-checked original guard at relay pre-Seal, queue tail, pre-body (entry + 2 direct + 3 relay)");
   }
   // 6. Direct IOException, original proof expired by fallback time: pure drop, relay never sealed, relay seq continuous.
   using(var r=await NewRig(true,true)){
    r.DirectLocal.Client.Shutdown(SocketShutdown.Send);
    var g=new Guard{ExpireFrom=4};var e=await Catch(r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None,g.Invoke));
    Check(IsPureExpiry(e)&&r.Adapter.Ready&&!r.Adapter.HasDirectScreen&&Drain().Count==0,"direct drop then expired original proof: pure drop, send 0 relay bytes, ready");
    await r.Adapter.SendFrameAsync(Frame(2),CancellationToken.None,new Guard().Invoke);var got=Drain();
    Check(got.Count==1&&OpenSeq(r.Peer,got[0])==2,"relay was not sealed by the dropped frame: next frame opens in order");
   }
   // 7. Direct post-Seal guard expiry (not a transport error): direct epoch dropped, wrapped fatal, no relay fallback.
   using(var r=await NewRig(true,true)){
    var g=new Guard{ExpireFrom=4};var e=await Catch(r.Adapter.SendFrameAsync(Frame(1),CancellationToken.None,g.Invoke));
    Check(IsWrappedFatal(e)&&!r.Adapter.HasDirectScreen&&!r.Adapter.Ready&&Drain().Count==0&&g.Calls==4,"direct post-Seal expiry: wrapped fatal, no fallback, terminal");
   }
   // 8. Local cancellation during direct is never a fallback trigger.
   using(var r=await NewRig(true,true))using(var cts=new CancellationTokenSource()){
    var g=new Guard();Action cancelAtHeader=()=>{g.Invoke();if(g.Calls==3)cts.Cancel();};
    var e=await Catch(r.Adapter.SendFrameAsync(Frame(1),cts.Token,cancelAtHeader));
    Check(e is OperationCanceledException&&!r.Adapter.HasDirectScreen&&Drain().Count==0,"local cancel after direct Seal: no relay fallback");
   }
  }finally{listener.Stop();}
 }

 static async Task Serve(TcpListener listener){
  while(true){TcpClient s;try{s=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch{return;}
   var _=Task.Run(()=>Handle(s));}
 }
 static async Task Handle(TcpClient socket){
  try{using(socket)using(var ssl=new SslStream(socket.GetStream(),false)){
   await ssl.AuthenticateAsServerAsync(cert,false,SslProtocols.Tls12,false).ConfigureAwait(false);
   var header=new MemoryStream();int state=0;
   while(state<4){int c=ssl.ReadByte();if(c<0)return;header.WriteByte((byte)c);state=state==0&&c==13?1:state==1&&c==10?2:state==2&&c==13?3:state==3&&c==10?4:c==13?1:0;}
   string text=Encoding.ASCII.GetString(header.ToArray());string first=text.Split(new[]{"\r\n"},StringSplitOptions.None)[0];int length=0;
   foreach(string line in text.Split(new[]{"\r\n"},StringSplitOptions.None))if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))Int32.TryParse(line.Substring(15).Trim(),out length);
   var body=new byte[length];int o=0;while(o<length){int k=ssl.Read(body,o,length-o);if(k==0)return;o+=k;}
   byte[] response;
   if(first.StartsWith("POST /api/v2/sessions/"+Sid+"/opaque/configure "))response=Respond(200,Encoding.UTF8.GetBytes(Descriptor()));
   else if(first.StartsWith("POST /api/v2/sessions/"+Sid+"/opaque/packet?epoch="+Epoch+" ")){
    if(body.Length<85||body[0]!=1)return;var c=new byte[body.Length-1];Buffer.BlockCopy(body,1,c,0,c.Length);relayed.Enqueue(c);response=Respond(202,new byte[0]);}
   else return;
   await ssl.WriteAsync(response,0,response.Length).ConfigureAwait(false);
  }}catch{}
 }
 static string Descriptor(){return "{\"sessionId\":\""+Sid+"\",\"initiatorDeviceId\":\""+Initiator+"\",\"targetDeviceId\":\""+Target+"\",\"transportEpoch\":\""+Epoch+"\",\"sharingGeneration\":2,\"permissionBits\":15,\"permissionRevision\":7,\"geometryVersion\":3"+
  (strictMode?",\"captureLeaseVersion\":1,\"captureLeaseMs\":250,\"minFrameIntervalMs\":100,\"transportKeepaliveVersion\":1":"")+"}";}
 static byte[] Respond(int code,byte[] body){var h=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+" "+(code==200?"OK":"Accepted")+"\r\nContent-Type: application/json\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
  var r=new byte[h.Length+body.Length];Buffer.BlockCopy(h,0,r,0,h.Length);Buffer.BlockCopy(body,0,r,h.Length,body.Length);return r;}
 static X509Certificate2 CreateCert(){byte[] pfx;using(var rsa=RSA.Create(2048)){var req=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
   var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);req.CertificateExtensions.Add(san.Build());
   var eku=new OidCollection();eku.Add(new Oid("1.3.6.1.5.5.7.3.1"));req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
   using(var gen=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(1)))pfx=gen.Export(X509ContentType.Pfx);}
  return new X509Certificate2(pfx,(string)null,X509KeyStorageFlags.DefaultKeySet);}
 static string CertPin(X509Certificate2 c){using(var sha=SHA256.Create()){var sb=new StringBuilder();foreach(byte x in sha.ComputeHash(c.RawData))sb.Append(x.ToString("X2"));return sb.ToString();}}
 static Dictionary<string,object> ToLongs(this Dictionary<string,object> d){var r=new Dictionary<string,object>(StringComparer.Ordinal);foreach(var kv in d)r[kv.Key]=kv.Value is int?(object)(long)(int)kv.Value:kv.Value;return r;}
}
