using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;

// Actual Viewer queue -> MediaWire/adapter -> CBC/HMAC -> pinned localhost HTTP.
// Key confirmation and descriptor assignment are fixture-controlled, not full relay/login handshake.
internal static class TlsFlow
{
    static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Set(object o,string name,object value){o.GetType().GetField(name,F).SetValue(o,value);}
    static object Get(object o,string name){return o.GetType().GetField(name,F).GetValue(o);}
    static X509Certificate2 Certificate()
    {
        using(var rsa=RSA.Create(2048)){
            var req=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
            using(var c=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1)))
                return new X509Certificate2(c.Export(X509ContentType.Pfx,""),"",X509KeyStorageFlags.Exportable);
        }
    }
    sealed class Stub:IDisposable
    {
        internal readonly TcpListener Listener=new TcpListener(IPAddress.Loopback,0);
        internal readonly List<InputCommand> Received=new List<InputCommand>();
        internal readonly Dictionary<long,long> Enqueued=new Dictionary<long,long>();
        internal readonly Stopwatch Clock=Stopwatch.StartNew();
        internal SecurePeerChannel Peer;
        internal Exception Failure;
        internal int Posts,MultiBatches,MaxBatch; internal volatile string Phase="listening";
        internal long MaximumAge;
        readonly X509Certificate2 cert;
        internal Stub(X509Certificate2 c){cert=c;Listener.Start();new Thread(()=>{while(true){TcpClient tcp;try{tcp=Listener.AcceptTcpClient();}catch{return;}ThreadPool.QueueUserWorkItem(_=>Serve(tcp));}}){IsBackground=true}.Start();}
        internal int Port{get{return ((IPEndPoint)Listener.LocalEndpoint).Port;}}
        void Serve(TcpClient tcp)
        {
            Phase="accepted"; try{using(tcp)using(var ssl=new SslStream(tcp.GetStream(),false)){
                ssl.ReadTimeout=5000;ssl.WriteTimeout=5000;ssl.AuthenticateAsServer(cert,false,SslProtocols.Tls12,false);Phase="tls-ready";
                var h=new StringBuilder();var one=new byte[1];
                while(!h.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){if(ssl.Read(one,0,1)!=1)return;h.Append((char)one[0]);if(h.Length>16384)throw new Exception("header bound");}
                Phase="headers-read";var lines=h.ToString().Split(new[]{"\r\n"},StringSplitOptions.None);int length=0;
                foreach(var line in lines)if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Substring(15).Trim());
                if(length<85||length>16469)throw new Exception("body bound");
                var body=new byte[length];int got=0;while(got<length){int n=ssl.Read(body,got,length-got);if(n<=0)throw new EndOfStreamException();got+=n;}
                Phase="body-read";if(body[0]!=2)throw new Exception("unexpected channel");
                var cipher=body.Skip(1).ToArray();var plain=Peer.Open(2,cipher);
                InputCommand[] items=SecureSessionAdapter.DecodeInputBatch(plain,7,Received.Count==0?0:Received[Received.Count-1].Sequence);
                lock(Received){Posts++;if(items.Length>1)MultiBatches++;MaxBatch=Math.Max(MaxBatch,items.Length);
                    foreach(var c in items){Received.Add(c);MaximumAge=Math.Max(MaximumAge,Clock.ElapsedMilliseconds-Enqueued[c.Sequence]);}}
                Thread.Sleep(120); // bounded per-request response latency; no retry/drop/authority relaxation
                byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                ssl.Write(response,0,response.Length);ssl.Flush();
            }}catch(Exception e){Failure=e;}
        }
        public void Dispose(){Listener.Stop();}
    }
    internal static void Run(bool firstOnly=false)
    {
        using(var cert=Certificate())using(var stub=new Stub(cert))
        using(var alice=new SecurePeerIdentity())using(var bob=new SecurePeerIdentity())
        {
            string pin;using(var sha=SHA256.Create())pin=BitConverter.ToString(sha.ComputeHash(cert.RawData)).Replace("-","");
            var profile=ServerProfile.Parse("{\"Version\":1,\"Name\":\"inputflow-test\",\"RendezvousUrl\":\"https://localhost:"+stub.Port+"\",\"RelayUrl\":\"https://localhost:"+stub.Port+"\",\"CertificateSha256\":\""+pin+"\"}");
            var client=new ControlPlaneClient(profile);client.RestoreCredentials("123","synthetic-bearer","SyntheticPw","2099-01-01T00:00:00Z");
            const string sid="0123456789abcdef0123456789abcdef",epoch="abcdef0123456789abcdef0123456789";
            var descriptor=new SessionTransportDescriptor(sid,"123","456",epoch,7,3,1,1,1,250,100,1);
            using(var transport=new SessionTransportClient(client,sid))
            using(var adapter=new SecureSessionAdapter(client,transport,descriptor,alice,CancellationToken.None))
            using(var peer=new SecurePeerChannel(bob,SecureSessionAdapter.Context(client,descriptor),false))
            {
                Set(transport,"current",descriptor);var sender=(SecurePeerChannel)Get(adapter,"crypto");
                sender.Establish(peer.Hello,bob.Fingerprint);peer.Establish(sender.Hello,alice.Fingerprint);
                byte[] a=sender.CreateFinished(),b=peer.CreateFinished();peer.ReceiveFinished(a);sender.ReceiveFinished(b);
                Set(adapter,"capsOfferSent",1);
                byte[] ack=new byte[11];ack[0]=91;ack[1]=1;BitConverter.GetBytes(7L).CopyTo(ack,2);ack[10]=1;
                typeof(SecureSessionAdapter).GetMethod("ReceiveInputControl",F).Invoke(adapter,new object[]{ack});
                stub.Peer=peer;
                using(var viewer=new ViewerForm(client,sid,adapter))
                using(var context=new ApplicationContext())
                using(var kickoff=new System.Windows.Forms.Timer{Interval=1})
                {
                    Set(viewer,"generation",7L);Set(viewer,"frameValid",true);Set(viewer,"statefulVersion",1);
                    var enqueue=typeof(ViewerForm).GetMethod("Enqueue",F);Exception failure=null;
                    var expected=new List<string>();long sequence=0;
                    Action<string,int> input=(kind,key)=>{
                        var c=new InputCommand{Kind=kind,KeyCode=key,Text="",X=0,Y=0,Button=0};
                        lock(stub.Received){stub.Enqueued[++sequence]=stub.Clock.ElapsedMilliseconds;}
                        if(!(bool)enqueue.Invoke(viewer,new object[]{c}))throw new Exception("viewer refused "+kind+" count="+expected.Count+" adapter="+adapter.Failure+" stub="+stub.Failure+" status="+((Label)Get(viewer,"state")).Text);
                        expected.Add(kind+":"+key);
                    };
                    kickoff.Tick+=async (s,e)=>{
                        kickoff.Stop();var elapsed=Stopwatch.StartNew();
                        try{
                            input("move",0);
                            while(((ICollection)Get(viewer,"pending")).Count!=0){if(elapsed.ElapsedMilliseconds>4500)throw new Exception("first POST timeout phase="+stub.Phase+" posts="+stub.Posts+" adapter="+adapter.Failure+" stub="+stub.Failure+" batching="+adapter.InputBatchingReady+" gate="+((SemaphoreSlim)Get(adapter,"sending")).CurrentCount);await Task.Delay(10);}
                            Console.WriteLine("FIRST_POST_PASS elapsedMs="+elapsed.ElapsedMilliseconds+" phase="+stub.Phase);
                            if(firstOnly)return;
                            lock(stub.Received){stub.MaximumAge=0;} // Exclude the separately reported cold-start move from steady-state age.
                            elapsed.Restart();
                            for(int n=0;n<75;n++){
                                input("keyDown",8);
                                if(n==25){input("mouseDown",0);input("move",0);input("mouseUp",0);}
                                await Task.Delay(33);
                            }
                            input("keyUp",8);
                            while(((ICollection)Get(viewer,"pending")).Count!=0){
                                if(elapsed.ElapsedMilliseconds>6500)throw new TimeoutException("viewer pump deadline");
                                if(stub.Failure!=null)throw new Exception("TLS stub failed",stub.Failure);
                                if((bool)Get(viewer,"inputFailed"))throw new Exception("viewer input failed");
                                await Task.Delay(10);
                            }
                            lock(stub.Received){
                                if(!stub.Received.Select(c=>c.Kind+":"+c.KeyCode).SequenceEqual(expected))throw new Exception("received logical events differ");
                                if(!stub.Received.Select(c=>c.Sequence).SequenceEqual(Enumerable.Range(1,expected.Count).Select(x=>(long)x)))throw new Exception("sequence mismatch");
                                if(stub.MultiBatches==0||stub.Posts>=expected.Count||stub.MaxBatch>32||stub.MaximumAge>=750)throw new Exception("batch efficiency/age bound not met posts="+stub.Posts+" multi="+stub.MultiBatches+" maxBatch="+stub.MaxBatch+" steadyAgeMs="+stub.MaximumAge);
                                Console.WriteLine("VIEWER_TLS_FLOW_PASS events="+expected.Count+" keyDown=75 keyUp=1 mouse=3 posts="+stub.Posts+" multiBatches="+stub.MultiBatches+" maxBatch="+stub.MaxBatch+" maxAgeMs="+stub.MaximumAge+" elapsedMs="+elapsed.ElapsedMilliseconds+" responseDelayMs=120 nominalIntervalMs=33 keyConfirmation=in-memory nativeInput=false");
                            }
                        }catch(Exception ex){failure=ex;}finally{((CancellationTokenSource)Get(viewer,"stop")).Cancel();context.ExitThread();}
                    };
                    kickoff.Start();Application.Run(context);if(failure!=null)throw failure;
                }
            }
        }
    }
}
