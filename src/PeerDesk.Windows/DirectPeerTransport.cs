using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
namespace PeerDesk.Windows {
    internal sealed class DirectPeerConnection:IDisposable {
        readonly TcpClient client;readonly CancellationTokenRegistration lifetime;
        internal NetworkStream Stream {get;private set;}
        internal DirectPeerConnection(TcpClient tcp,CancellationToken cancellation){client=tcp;client.NoDelay=true;Stream=tcp.GetStream();lifetime=cancellation.Register(()=>{try{client.Close();}catch{}});}
        public void Dispose(){lifetime.Dispose();client.Close();}
    }
    // Reachable-IP TCP path only, not universal NAT traversal. The relay remains the fallback.
    // Only signature-verified session-bound offers may cause an outbound connection.
    internal sealed class DirectPeerListener:IDisposable {
        readonly TcpListener listener;readonly byte[] offer;readonly bool initiator;
        readonly byte[] context;bool disposed;
        internal byte[] Offer {get{return(byte[])offer.Clone();}}
        internal DirectPeerListener(SecurePeerIdentity identity,byte[] exactContext,bool isInitiator,IPAddress explicitLocalAddress,bool userEnabledDirect,DateTimeOffset now){
            if(!userEnabledDirect)throw new InvalidOperationException("Direct connections require explicit opt-in.");
            ValidateAddress(explicitLocalAddress);
            if(exactContext==null||exactContext.Length<32||exactContext.Length>4096)throw new ArgumentException("Bound session context required.");
            initiator=isInitiator;context=(byte[])exactContext.Clone();
            listener=new TcpListener(explicitLocalAddress,0);listener.Start(2);
            var endpoint=(IPEndPoint)listener.LocalEndpoint;
            try{offer=DirectPeerOffer.Create(identity,context,initiator,endpoint,now);}catch{listener.Stop();throw;}
        }
        internal async Task<DirectPeerConnection> AcceptAsync(SecurePeerChannel channel,string trustedPeerPin,CancellationToken ct){
            if(disposed)throw new ObjectDisposedException("DirectPeerListener");
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct)){
                timeout.CancelAfter(3000);TcpClient tcp=null;
                using(timeout.Token.Register(()=>listener.Stop())){
                    try{tcp=await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        var accepted=tcp;
                        using(timeout.Token.Register(()=>accepted.Close()))await DirectPeerOffer.Authenticate(tcp,channel,trustedPeerPin,timeout.Token).ConfigureAwait(false);
                        var result=new DirectPeerConnection(tcp,ct);tcp=null;return result;
                    }catch(Exception ex) when(timeout.IsCancellationRequested&&(ex is SocketException||ex is IOException||ex is ObjectDisposedException)){ct.ThrowIfCancellationRequested();throw new TimeoutException("Direct peer handshake timed out.");}
                    finally{if(tcp!=null)tcp.Close();}
                }
            }
        }
        internal static void ValidateAddress(IPAddress address){
            if(address==null||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.Equals(IPAddress.None)||address.Equals(IPAddress.Broadcast)||address.IsIPv6Multicast)throw new ArgumentException("Direct candidate requires a specific unicast IP.");
            byte[] b=address.GetAddressBytes();if(b.Length!=4&&b.Length!=16)throw new ArgumentException("Invalid address family.");
            if(b.Length==4&&(b[0]==0||b[0]>=224))throw new ArgumentException("Direct multicast/unspecified IP denied.");
        }
        public void Dispose(){if(disposed)return;disposed=true;listener.Stop();}
    }
    internal static class DirectPeerOffer {
        // This helper never adds firewall rules, opens a router port, changes an AWS security group, or sends screen data.
        internal static byte[] Create(SecurePeerIdentity identity,byte[] context,bool initiator,IPEndPoint endpoint,DateTimeOffset now){
            if(endpoint.Port<1024)throw new ArgumentException("Candidate port must be unprivileged.");
            byte[] body;
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){
                w.Write(new byte[]{82,66,68,49});w.Write((byte)(initiator?1:2));w.Write(SecurePeerIdentity.Hash(context));
                var key=identity.PublicKey;w.Write(key.Length);w.Write(key);w.Write(now.ToUnixTimeSeconds());w.Write(now.AddSeconds(60).ToUnixTimeSeconds());
                var nonce=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(nonce);w.Write(nonce);
                var address=endpoint.Address.GetAddressBytes();w.Write((byte)address.Length);w.Write(address);w.Write((ushort)endpoint.Port);body=s.ToArray();
            }
            var signature=identity.Sign(body);
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){w.Write(body.Length);w.Write(body);w.Write(signature.Length);w.Write(signature);return s.ToArray();}
        }
        internal static IPEndPoint Verify(byte[] encoded,string trustedPeerPin,byte[] exactContext,bool expectedInitiator,DateTimeOffset now){
            try{
                if(encoded==null||encoded.Length>2048||exactContext==null||exactContext.Length<32||exactContext.Length>4096||trustedPeerPin==null||trustedPeerPin.Length!=64)throw Failure();
                byte[] body,signature;
                using(var s=new MemoryStream(encoded,false))using(var r=new BinaryReader(s)){
                    int n=r.ReadInt32();if(n<90||n>1200)throw Failure();body=Exact(r,n);n=r.ReadInt32();if(n<256||n>512)throw Failure();signature=Exact(r,n);if(s.Position!=s.Length)throw Failure();
                }
                using(var s=new MemoryStream(body,false))using(var r=new BinaryReader(s)){
                    if(!Equal(Exact(r,4),new byte[]{82,66,68,49})||r.ReadByte()!=(expectedInitiator?1:2)||!Equal(Exact(r,32),SecurePeerIdentity.Hash(exactContext)))throw Failure();
                    int n=r.ReadInt32();if(n<1||n>600)throw Failure();byte[] key=Exact(r,n);
                    if(!String.Equals(SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(key)),trustedPeerPin,StringComparison.OrdinalIgnoreCase)||!SecurePeerIdentity.Verify(key,body,signature))throw Failure();
                    long issued=r.ReadInt64(),expires=r.ReadInt64(),utc=now.ToUnixTimeSeconds();if(issued<0||issued>utc+5||expires<=utc||expires-issued!=60)throw Failure();Exact(r,32);
                    n=r.ReadByte();if(n!=4&&n!=16)throw Failure();var address=new IPAddress(Exact(r,n));DirectPeerListener.ValidateAddress(address);int port=r.ReadUInt16();if(port<1024||s.Position!=s.Length)throw Failure();return new IPEndPoint(address,port);
                }
            }catch(Exception ex)when(ex is IOException||ex is ArgumentException||ex is CryptographicException){throw Failure();}
        }
        // null means reachability failed BEFORE authenticated handshake. Crypto/auth failure is terminal, not hidden by relay.
        internal static async Task<DirectPeerConnection> TryConnectAsync(byte[] signedOffer,string peerPin,byte[] context,bool peerIsInitiator,DateTimeOffset now,SecurePeerChannel crypto,CancellationToken ct){
            var endpoint=Verify(signedOffer,peerPin,context,peerIsInitiator,now);var tcp=new TcpClient(endpoint.AddressFamily){NoDelay=true};bool connected=false;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct)){
                timeout.CancelAfter(3000);
                var ownedTcp=tcp;
                using(timeout.Token.Register(()=>ownedTcp.Close())){
                    try{
                        await tcp.ConnectAsync(endpoint.Address,endpoint.Port).ConfigureAwait(false);connected=true;
                        await Authenticate(tcp,crypto,peerPin,timeout.Token).ConfigureAwait(false);
                        var result=new DirectPeerConnection(tcp,ct);tcp=null;return result;
                    }catch(Exception ex)when(ex is SocketException||ex is IOException||ex is ObjectDisposedException){
                        ct.ThrowIfCancellationRequested();
                        if(connected)throw new IOException("Direct handshake interrupted; discard this crypto epoch before relay fallback.",ex);
                        return null;
                    }finally{if(tcp!=null)tcp.Close();}
                }
            }
        }
        internal static async Task Authenticate(TcpClient tcp,SecurePeerChannel crypto,string pin,CancellationToken ct){
            if(crypto==null||crypto.Established)throw new InvalidOperationException("A fresh crypto epoch is required.");
            var stream=tcp.GetStream();await WritePacket(stream,crypto.Hello,4096,ct).ConfigureAwait(false);
            var hello=await ReadPacket(stream,4096,ct).ConfigureAwait(false);crypto.Establish(hello,pin);
            await WritePacket(stream,crypto.CreateFinished(),4096,ct).ConfigureAwait(false);
            crypto.ReceiveFinished(await ReadPacket(stream,4096,ct).ConfigureAwait(false));
            if(!crypto.KeyConfirmed)throw Failure();
        }
        internal static Task WritePacket(Stream stream,byte[] bytes,int max,CancellationToken ct){return WritePacket(stream,bytes,max,ct,null);}
        // guard runs before the first header byte and before every later write/flush boundary; a throw leaves the stream for the caller to drop.
        internal static async Task WritePacket(Stream stream,byte[] bytes,int max,CancellationToken ct,Action guard){
            if(bytes==null||bytes.Length<1||bytes.Length>max)throw new InvalidDataException("Direct packet bound.");
            int n=bytes.Length;byte[] header={(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n};
            if(guard!=null)guard();await stream.WriteAsync(header,0,4,ct).ConfigureAwait(false);
            if(guard!=null)guard();await stream.WriteAsync(bytes,0,n,ct).ConfigureAwait(false);
            if(guard!=null)guard();await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        internal static async Task<byte[]> ReadPacket(Stream stream,int max,CancellationToken ct){
            var header=new byte[4];await ReadExact(stream,header,ct).ConfigureAwait(false);
            long n=((long)header[0]<<24)|((long)header[1]<<16)|((long)header[2]<<8)|header[3];
            if(n<1||n>max)throw new InvalidDataException("Direct packet bound.");var result=new byte[(int)n];await ReadExact(stream,result,ct).ConfigureAwait(false);return result;
        }
        static async Task ReadExact(Stream s,byte[] b,CancellationToken ct){int offset=0;while(offset<b.Length){int n=await s.ReadAsync(b,offset,b.Length-offset,ct).ConfigureAwait(false);if(n==0)throw new EndOfStreamException();offset+=n;}}
        static byte[] Exact(BinaryReader r,int n){var b=r.ReadBytes(n);if(b.Length!=n)throw Failure();return b;}
        static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0;}
        static CryptographicException Failure(){return new CryptographicException("Direct candidate authentication failed.");}
    }
}
