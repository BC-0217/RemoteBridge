using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace PeerDesk.Windows {
    internal sealed partial class SecureSessionAdapter {
        readonly TaskCompletionSource<byte[]> directOffer=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly SemaphoreSlim directSending=new SemaphoreSlim(1,1);
        DirectPeerConnection directConnection;SecurePeerChannel directCrypto;int directNegotiated;
        internal bool HasDirectScreen {get{return directConnection!=null;}}
        // Only bulk screen frames use direct TCP. Input and control retain the independent server authorization lease.
        internal async Task NegotiateDirectScreenAsync(SecurePeerIdentity identity,IPAddress explicitLocalAddress,bool enabled) {
            if(Interlocked.Exchange(ref directNegotiated,1)!=0)throw new InvalidOperationException("Direct choice already made.");
            DirectPeerListener listener=null;SecurePeerChannel fresh=null;DirectPeerConnection connection=null;
            byte[] context=DirectContext();bool offered=enabled;
            DiagDirect(DiagEvent.DirectNegotiation,enabled?DiagDirectOutcome.Started:DiagDirectOutcome.LocalDisabled,null);
            try {
                if(enabled&&!initiator)try{listener=new DirectPeerListener(identity,context,false,explicitLocalAddress,true,DateTimeOffset.UtcNow);}
                    catch(SocketException ex){offered=false;DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.ListenerUnavailable,ex);}
                byte[] offer=listener==null?new byte[0]:listener.Offer;
                using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){w.Write((byte)80);w.Write(offered);w.Write(offer.Length);w.Write(offer);await SendAsync(0,s.ToArray(),lifetime.Token).ConfigureAwait(false);}
                byte[] peer=await Await(directOffer.Task,5000,lifetime.Token).ConfigureAwait(false);
                using(var s=new MemoryStream(peer,false))using(var r=new BinaryReader(s)){
                    bool peerEnabled=r.ReadBoolean();int n=r.ReadInt32();if(n<0||n>2048||s.Length-s.Position!=n)throw new InvalidDataException("Invalid direct offer.");
                    byte[] candidate=r.ReadBytes(n);if(!offered||!peerEnabled){if(offered)DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.PeerDisabled,null);return;}
                    fresh=new SecurePeerChannel(identity,context,initiator);
                    if(initiator){
                        if(n==0)throw new InvalidDataException("Missing target direct offer.");
                        connection=await DirectPeerOffer.TryConnectAsync(candidate,crypto.PeerFingerprint,context,false,DateTimeOffset.UtcNow,fresh,lifetime.Token).ConfigureAwait(false);
                    }else {
                        if(n!=0)throw new InvalidDataException("Unexpected initiator offer.");
                        try{connection=await listener.AcceptAsync(fresh,crypto.PeerFingerprint,lifetime.Token).ConfigureAwait(false);}
                        catch(TimeoutException){DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.AcceptTimeout,null);return;}
                    }
                }
                if(connection==null){DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.ConnectFailed,null);return;}
                directCrypto=fresh;fresh=null;directConnection=connection;connection=null;
                DiagDirect(DiagEvent.DirectEstablished,DiagDirectOutcome.Established,null);
                if(initiator)_ = ReadDirectFramesAsync(directConnection,directCrypto);
            } catch(Exception ex){DiagDirect(DiagEvent.DirectTerminal,DiagDirectOutcome.NegotiationFailed,ex);Fail(ex,DiagAdapterOrigin.DirectNegotiate,-1);throw;}
            finally{if(connection!=null)connection.Dispose();if(fresh!=null)fresh.Dispose();if(listener!=null)listener.Dispose();}
        }
        byte[] DirectContext(){var original=Context(control,descriptor);var suffix=Encoding.ASCII.GetBytes("/direct-screen/1");var result=new byte[original.Length+suffix.Length];Buffer.BlockCopy(original,0,result,0,original.Length);Buffer.BlockCopy(suffix,0,result,original.Length,suffix.Length);return result;}
        bool ReceiveDirectOffer(byte[] plain){
            if(plain.Length==0||plain[0]!=80)return false;
            if(plain.Length<6||plain.Length>2054)throw new InvalidDataException("Direct offer bound.");
            var value=new byte[plain.Length-1];Buffer.BlockCopy(plain,1,value,0,value.Length);
            if(!directOffer.TrySetResult(value))throw new InvalidDataException("Duplicate direct offer.");return true;
        }
        async Task ReadDirectFramesAsync(DirectPeerConnection connection,SecurePeerChannel keys){
            try {while(!lifetime.IsCancellationRequested){
                byte[] packet=await DirectPeerOffer.ReadPacket(connection.Stream,524372,lifetime.Token).ConfigureAwait(false);
                Demand(1,false);byte[] plain=keys.Open(1,packet);
                // ReceiveFrame atomically drops an already-published logical frame after crypto validation.
                ReceiveFrame(plain);
            }}catch(InvalidDataException ex){DiagDirect(DiagEvent.DirectTerminal,DiagDirectOutcome.ReadInvalidData,ex);Fail(ex,DiagAdapterOrigin.DirectRead,-1);DropDirect(connection);}
            catch(Exception ex)when(ex is IOException||ex is SocketException||ex is ObjectDisposedException){if(!lifetime.IsCancellationRequested)DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.ReadTransport,ex);DropDirect(connection);}
            catch(Exception ex){DiagDirect(DiagEvent.DirectTerminal,DiagDirectOutcome.ReadOther,ex);Fail(ex,DiagAdapterOrigin.DirectRead,-1);DropDirect(connection);}
        }
        // Returns false when no direct epoch exists, or when a direct-sealed write hit a pure transport failure
        // (IOException/SocketException/ObjectDisposedException without local cancellation): the direct epoch is then
        // permanently dropped and the caller may send the SAME frame on relay with independent keys/sequence, after
        // re-checking the original guard. Any other post-Seal failure (cancel, guard/proof, auth, crypto) drops the
        // direct epoch and propagates as terminal with no fallback.
        async Task<bool> TrySendDirectFrameAsync(byte[] plain,CancellationToken ct,Action guard,FrameSendStage stage){
            var connection=directConnection;if(connection==null)return false;
            await directSending.WaitAsync(ct).ConfigureAwait(false);
            try {
                if(!ReferenceEquals(connection,directConnection))return false;Demand(1,true);
                if(guard!=null)guard();
                stage.Sealed=true;byte[] packet=directCrypto.Seal(1,plain);
                await DirectPeerOffer.WritePacket(connection.Stream,packet,524372,ct,guard).ConfigureAwait(false);return true;
            }catch(Exception ex)when(stage.Sealed){
                DropDirect(connection);
                if((ex is IOException||ex is SocketException||ex is ObjectDisposedException)&&!ct.IsCancellationRequested){DiagDirect(DiagEvent.DirectFallback,DiagDirectOutcome.WriteTransport,ex);stage.Sealed=false;return false;}
                DiagDirect(DiagEvent.DirectTerminal,DiagDirectOutcome.WriteTerminal,ex);
                throw;
            }
            finally{directSending.Release();}
        }
        // Direct-path diagnostics: outcome enum, role and exception category only (never addresses or offers).
        void DiagDirect(DiagEvent ev,DiagDirectOutcome outcome,Exception ex){
            var fields=new[]{DiagValue.Of(DiagKey.Outcome,outcome),DiagValue.Of(DiagKey.Initiator,initiator)};
            if(ex==null)DiagnosticLog.Event(ev,diagCorrelation,fields);else DiagnosticLog.Failure(ev,diagCorrelation,ex,fields);
        }
        void DropDirect(DirectPeerConnection expected){
            if(!ReferenceEquals(Interlocked.CompareExchange(ref directConnection,null,expected),expected))return;
            expected.Dispose();var keys=Interlocked.Exchange(ref directCrypto,null);if(keys!=null)keys.Dispose();
        }
    }
}
