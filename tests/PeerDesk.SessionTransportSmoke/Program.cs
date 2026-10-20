using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static readonly string Sid="00112233445566778899AABBCCDDEEFF";
    private static readonly string Epoch="0123456789abcdef0123456789abcdef";
    private static readonly byte[] Cipher=MakeCipher();
    private static X509Certificate2 cert;
    private static int requests;

    private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Run()
    {
        cert=CreateEphemeralLoopbackCert();
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(()=>Serve(listener,4));
        string url="https://localhost:"+port;
        string pin=CertPin(cert);
        var assembly=Assembly.Load("RemoteBridge");
        var profileType=assembly.GetType("PeerDesk.Windows.ServerProfile",true);
        var parse=profileType.GetMethod("Parse",BindingFlags.Public|BindingFlags.Static);
        string profileJson="{\"Version\":1,\"Name\":\"local\",\"RendezvousUrl\":\""+url+"\",\"RelayUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\"}";
        var profile=parse.Invoke(null,new object[]{profileJson});
        var cpType=assembly.GetType("PeerDesk.Windows.ControlPlaneClient",true);
        var cp=Activator.CreateInstance(cpType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{profile},null);
        cpType.GetMethod("RestoreCredentials",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(cp,new object[]{"device-target","bearer-test","temporary-password","2030-01-01T00:00:00Z"});
        var transportType=assembly.GetType("PeerDesk.Windows.SessionTransportClient",true);
        var transport=Activator.CreateInstance(transportType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{cp,(object)Sid},null);
        try
        {
            var configure=transportType.GetMethod("ConfigureAsync");
            var configureTask=(Task)configure.Invoke(transport,new object[]{15,7,3,CancellationToken.None});
            await configureTask.ConfigureAwait(false);
            var descriptor=configureTask.GetType().GetProperty("Result").GetValue(configureTask,null);
            var get=transportType.GetMethod("GetDescriptorAsync");
            var getTask=(Task)get.Invoke(transport,new object[]{CancellationToken.None});
            await getTask.ConfigureAwait(false);
            descriptor=getTask.GetType().GetProperty("Result").GetValue(getTask,null);
            var received=new TaskCompletionSource<bool>();
            Func<byte,byte[],Task> handler=(channel,data)=> { received.TrySetResult(channel==1&&Equal(data,Cipher)); return Task.CompletedTask; };
            var receive=transportType.GetMethod("ReceiveAsync");
            var receiveTask=(Task)receive.Invoke(transport,new object[]{descriptor,handler,CancellationToken.None});
            var send=transportType.GetMethod("SendAsync");
            var sendTask=(Task)send.Invoke(transport,new object[]{descriptor,(byte)1,Cipher,CancellationToken.None});
            await sendTask.ConfigureAwait(false);
            var arrived=await Task.WhenAny(received.Task,Task.Delay(5000)).ConfigureAwait(false);
            if(arrived!=received.Task||!received.Task.Result) throw new InvalidOperationException("Opaque stream packet did not arrive.");
            await receiveTask.ConfigureAwait(false);
            await server.ConfigureAwait(false);
            Console.WriteLine("PASS: net48 client over TLS 1.2 with exact certificate pin; configure, descriptor, packet, stream round-trip.");
        }
        finally
        {
            ((IDisposable)transport).Dispose();
            listener.Stop();
            cert.Dispose();
        }
    }

    private static async Task Serve(TcpListener listener,int count)
    {
        for(int i=0;i<count;i++)
        {
            using(var socket=await listener.AcceptTcpClientAsync().ConfigureAwait(false))
            using(var ssl=new SslStream(socket.GetStream(),false))
            {
                await ssl.AuthenticateAsServerAsync(cert,false,SslProtocols.Tls12,false).ConfigureAwait(false);
                var header=new MemoryStream(); int state=0;
                while(state<4)
                {
                    int b=ssl.ReadByte(); if(b<0) throw new EndOfStreamException("HTTP header ended early.");
                    header.WriteByte((byte)b);
                    state=state==0&&b==13?1:state==1&&b==10?2:state==2&&b==13?3:state==3&&b==10?4:b==13?1:0;
                    if(header.Length>16384) throw new InvalidDataException("HTTP header too large.");
                }
                string text=Encoding.ASCII.GetString(header.ToArray());
                string first=text.Split(new[]{ "\r\n" },StringSplitOptions.None)[0];
                int length=0;
                foreach(string line in text.Split(new[]{ "\r\n" },StringSplitOptions.None))
                    if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(15).Trim(),out length);
                byte[] body=new byte[length]; ReadExactly(ssl,body);
                byte[] response;
                byte[] responseBody=Encoding.UTF8.GetBytes(DescriptorJson());
                if(first.StartsWith("POST /api/v2/sessions/"+Sid+"/opaque/configure "))
                    response=Respond(200,"application/json",responseBody);
                else if(first.StartsWith("GET /api/v2/sessions/"+Sid+"/opaque/descriptor "))
                    response=Respond(200,"application/json",responseBody);
                else if(first.StartsWith("POST /api/v2/sessions/"+Sid+"/opaque/packet?epoch="+Epoch+" "))
                {
                    if(body.Length!=Cipher.Length+1||body[0]!=1) throw new InvalidDataException("Packet bytes changed.");
                    for(int j=0;j<Cipher.Length;j++) if(body[j+1]!=Cipher[j]) throw new InvalidDataException("Ciphertext changed.");
                    response=Respond(202,"application/json",new byte[0]);
                }
                else if(first.StartsWith("GET /api/v2/sessions/"+Sid+"/opaque/stream?epoch="+Epoch+" "))
                {
                    byte[] frame=new byte[89]; frame[3]=85; frame[4]=1; Buffer.BlockCopy(Cipher,0,frame,5,Cipher.Length);
                    response=Respond(200,"application/octet-stream",frame);
                }
                else throw new InvalidDataException("Unexpected endpoint: "+first);
                await ssl.WriteAsync(response,0,response.Length).ConfigureAwait(false);
                if(responseBody.Length>0 && (first.Contains("/configure ")||first.Contains("/descriptor ")))
                { }
                requests++;
            }
        }
        listener.Stop();
    }

    private static byte[] Respond(int code,string type,byte[] body)
    {
        string reason=code==200?"OK":code==202?"Accepted":"Error";
        byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+" "+reason+"\r\nContent-Type: "+type+"\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
        byte[] response=new byte[header.Length+body.Length]; Buffer.BlockCopy(header,0,response,0,header.Length); Buffer.BlockCopy(body,0,response,header.Length,body.Length); return response;
    }
    private static string DescriptorJson()
    {
        return "{\"sessionId\":\""+Sid+"\",\"initiatorDeviceId\":\"device-source\",\"targetDeviceId\":\"device-target\",\"transportEpoch\":\""+Epoch+"\",\"sharingGeneration\":2,\"permissionBits\":15,\"permissionRevision\":7,\"geometryVersion\":3}";
    }
    private static byte[] MakeCipher() { var b=new byte[84]; for(int i=0;i<b.Length;i++) b[i]=(byte)(i*13+7); return b; }
    private static bool Equal(byte[] a,byte[] b) { if(a.Length!=b.Length)return false; for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false; return true; }
    private static X509Certificate2 CreateEphemeralLoopbackCert()
    {
        byte[] pfx;
        using(var rsa=RSA.Create(2048))
        {
            var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
            var eku=new OidCollection(); eku.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
            using(var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(1)))
                pfx=generated.Export(X509ContentType.Pfx);
        }
        try { return new X509Certificate2(pfx,(string)null,X509KeyStorageFlags.DefaultKeySet); }
        finally { Array.Clear(pfx,0,pfx.Length); }
    }
    private static string CertPin(X509Certificate2 c) { using(var sha=SHA256.Create()) { var b=sha.ComputeHash(c.RawData); var s=new StringBuilder(); foreach(byte x in b)s.Append(x.ToString("X2")); return s.ToString(); } }
    private static void ReadExactly(Stream s,byte[] b) { int o=0; while(o<b.Length){int n=s.Read(b,o,b.Length-o); if(n==0)throw new EndOfStreamException(); o+=n;} }
}
