using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace PeerDesk.Windows {
    // Per-session opaque media endpoint. All application validation and permission checks occur after E2EE verification.
    internal sealed partial class SecureSessionAdapter:IDisposable {
        readonly ControlPlaneClient control;readonly SessionTransportClient transport;readonly SessionTransportDescriptor descriptor;
        readonly SecurePeerChannel crypto;readonly CancellationTokenSource lifetime;readonly SemaphoreSlim sending=new SemaphoreSlim(1,1);
        readonly object sync=new object();readonly Queue<PendingInput> inputs=new Queue<PendingInput>();long lastBoundAuthorityId;bool inputQueueClosed;
        readonly TaskCompletionSource<byte[]> hello=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<byte[]> finished=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<bool> readySignal=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<bool> firstFrame=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8};
        readonly bool initiator;Task receiver;FramePacket latest;Exception failure;int disposed;
        long lastInputSequence,lastFrameSequence;
        sealed class FrameSendStage{internal bool Sealed;}
        internal event Action<byte,byte[]> FeaturePayload;
        internal bool Ready {get{return crypto.KeyConfirmed&&!lifetime.IsCancellationRequested&&failure==null;}}
        internal SessionTransportDescriptor Descriptor {get{return descriptor;}}
        internal bool IsInitiator {get{return initiator;}}
        internal Exception Failure {get{lock(sync)return failure;}}
        // Random local diagnostic correlation shared with the sharing session/viewer; unrelated to session/device ids.
        readonly long diagCorrelation=DiagnosticLog.NewCorrelation();
        internal long DiagCorrelation {get{return diagCorrelation;}}
        internal SecureSessionAdapter(ControlPlaneClient c,SessionTransportClient t,SessionTransportDescriptor d,SecurePeerIdentity identity,CancellationToken authorization){
            control=c;transport=t;descriptor=d;
            initiator=d.InitiatorDeviceId==c.DeviceId;
            if(!initiator&&d.TargetDeviceId!=c.DeviceId)throw new UnauthorizedAccessException("Not a session participant.");
            lifetime=CancellationTokenSource.CreateLinkedTokenSource(authorization);
            crypto=new SecurePeerChannel(identity,Context(c,d),initiator);
        }
        internal static byte[] Context(ControlPlaneClient c,SessionTransportDescriptor d){
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s,new UTF8Encoding(false,true))){
                w.Write("RemoteBridge/secure-session/1");w.Write(c.ServerUrl);w.Write(c.CertificateSha256.ToUpperInvariant());
                w.Write(d.SessionId);w.Write(d.InitiatorDeviceId);w.Write(d.TargetDeviceId);w.Write(d.TransportEpoch);
                w.Write(d.SharingGeneration);w.Write(d.PermissionBits);w.Write(d.PermissionRevision);w.Write(d.GeometryVersion);
                // Strict capture v1 binds its negotiated fields into the signed Hello context; legacy contexts stay byte-identical.
                if(d.CaptureLeaseVersion!=0||d.CaptureLeaseMs!=0||d.MinFrameIntervalMs!=0||d.TransportKeepaliveVersion!=0){
                    if(!d.IsStrictCaptureV1)throw new InvalidDataException("Partial capture v1 descriptor.");
                    w.Write("capture/1");w.Write(d.CaptureLeaseVersion);w.Write(d.CaptureLeaseMs);w.Write(d.MinFrameIntervalMs);w.Write(d.TransportKeepaliveVersion);}
                return s.ToArray();
            }
        }
        internal async Task AuthenticateAsync(Func<byte[],byte[],Task<string>> confirmPeer){
            if(receiver!=null)throw new InvalidOperationException("Handshake already started.");
            receiver=transport.ReceiveAsync(descriptor,OnPacket,lifetime.Token);
            _ = receiver.ContinueWith(t=>{if(t.IsFaulted)Fail(t.Exception.InnerException,DiagAdapterOrigin.Receiver,-1);else if(!lifetime.IsCancellationRequested)Fail(new IOException("Secure stream ended."),DiagAdapterOrigin.ReceiverEnded,-1);},TaskScheduler.Default);
            try{
                byte[] local=crypto.Hello;await SendHandshake(1,local).ConfigureAwait(false);
                byte[] remote=await Await(hello.Task,20000,lifetime.Token).ConfigureAwait(false);
                string trusted=await confirmPeer(local,remote).ConfigureAwait(false);
                if(trusted==null)throw new OperationCanceledException("Peer identity was not approved.");
                lifetime.Token.ThrowIfCancellationRequested();crypto.Establish(remote,trusted);
                await SendHandshake(2,crypto.CreateFinished()).ConfigureAwait(false);
                crypto.ReceiveFinished(await Await(finished.Task,20000,lifetime.Token).ConfigureAwait(false));
                if(!crypto.KeyConfirmed)throw new InvalidOperationException("Session keys were not confirmed.");
                readySignal.TrySetResult(true);
            }catch(Exception ex){Fail(ex,DiagAdapterOrigin.Handshake,-1);throw;}
        }
        async Task SendHandshake(byte kind,byte[] value){
            var packet=new byte[value.Length+1];packet[0]=kind;Buffer.BlockCopy(value,0,packet,1,value.Length);
            await transport.SendAsync(descriptor,0,packet,lifetime.Token).ConfigureAwait(false);
        }
        async Task OnPacket(byte channel,byte[] packet){
            try{
                if(channel==0&&packet.Length>1&&(packet[0]==1||packet[0]==2)){
                    if(readySignal.Task.IsCompleted||packet.Length>4097)throw new InvalidDataException("Unexpected handshake packet.");
                    var value=new byte[packet.Length-1];Buffer.BlockCopy(packet,1,value,0,value.Length);
                    if(packet[0]==1){if(!hello.TrySetResult(value))throw new InvalidDataException("Duplicate Hello.");}
                    else if(!finished.TrySetResult(value))throw new InvalidDataException("Duplicate Finished.");
                    return;
                }
                // A peer may send its first data immediately after its Finished. Do not misclassify that scheduling race.
                await Await(readySignal.Task,20000,lifetime.Token).ConfigureAwait(false);
                Demand(channel,false);
                byte[] plain=crypto.Open(channel==0?(byte)5:channel,packet);
                if(channel==1)ReceiveFrame(plain);
                else if(channel==2)ReceiveInput(plain);
                else {if(channel==0&&ReceiveDirectOffer(plain))return;var handler=FeaturePayload;if(handler!=null)handler(channel,plain);}
            }catch(Exception ex){Fail(ex,DiagAdapterOrigin.Packet,channel);throw;}
        }
        void Demand(byte channel,bool outbound){
            if(!Ready)throw new InvalidOperationException("Secure session is not ready.");
            bool senderInitiator=outbound?initiator:!initiator;
            int bit=channel==1?1:channel==2?2:channel==3?4:channel==4?8:0;
            if(channel>4||(bit!=0&&(descriptor.PermissionBits&bit)==0)||channel==1&&senderInitiator||channel==2&&!senderInitiator)
                throw new UnauthorizedAccessException("Session permission or sender role denied.");
        }
        internal async Task SendAsync(byte channel,byte[] plaintext,CancellationToken ct){
            // Strict capture v1 frames must go through SendFrameAsync so the capture proof guard cannot be bypassed.
            if(channel==1&&descriptor.IsStrictCaptureV1)throw new InvalidOperationException("Capture v1 frames require SendFrameAsync.");
            Demand(channel,true);using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token)){
                await sending.WaitAsync(linked.Token).ConfigureAwait(false);
                try{Demand(channel,true);var packet=crypto.Seal(channel==0?(byte)5:channel,plaintext);
                    await transport.SendAsync(descriptor,channel,packet,linked.Token).ConfigureAwait(false);}
                catch(Exception ex){Fail(ex,DiagAdapterOrigin.Send,channel);throw;}finally{sending.Release();}
            }
        }
        // authorizationGuard checks the caller's original immutable capture proof (never refreshed here) at every boundary.
        // Before any live Seal only a pure CaptureProofExpiredException propagates without Fail, so the next valid Seal keeps
        // the sequence continuous; the caller owns the consecutive-drop budget. After Seal any failure is terminal for the
        // sealed epoch, and post-Seal expiry is rethrown as a different fatal exception so it can never be counted as a drop.
        internal async Task SendFrameAsync(FramePacket frame,CancellationToken ct,Action authorizationGuard=null){
            if(descriptor.IsStrictCaptureV1&&authorizationGuard==null)throw new InvalidOperationException("Capture v1 frames require the capture proof guard.");
            if(frame.Generation!=descriptor.SharingGeneration||frame.Jpeg==null||frame.Jpeg.Length>524000)throw new InvalidDataException("Secure frame bounds.");
            byte[] packet;using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){
                w.Write(frame.Generation);w.Write(frame.Sequence);w.Write(frame.Width);w.Write(frame.Height);w.Write(frame.Jpeg.Length);w.Write(frame.Jpeg);packet=s.ToArray();}
            Demand(1,true);var stage=new FrameSendStage();
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token)){
                try{
                    await sending.WaitAsync(linked.Token).ConfigureAwait(false);
                    try{
                        Demand(1,true);if(authorizationGuard!=null)authorizationGuard();
                        if(!await TrySendDirectFrameAsync(packet,linked.Token,authorizationGuard,stage).ConfigureAwait(false)){
                            // Relay (or fallback after a dropped direct epoch): re-check the same original proof before relay Seal.
                            Demand(1,true);if(authorizationGuard!=null)authorizationGuard();
                            stage.Sealed=true;var sealedPacket=crypto.Seal(1,packet);
                            // Relay post-Seal uncertainty (including proof expiry at the queue tail/pre-body) is terminal, never retried.
                            await transport.SendAsync(descriptor,1,sealedPacket,linked.Token,authorizationGuard).ConfigureAwait(false);
                        }
                    }finally{sending.Release();}
                }
                catch(CaptureProofExpiredException)when(!stage.Sealed){throw;}
                catch(Exception ex){Fail(ex,DiagAdapterOrigin.SendFrame,stage.Sealed?1:0);
                    if(ex is CaptureProofExpiredException)throw new InvalidOperationException("Capture proof expired after Seal; the secure epoch is terminal.",ex);
                    throw;}
            }
        }
        void ReceiveFrame(byte[] packet){
            using(var s=new MemoryStream(packet,false))using(var r=new BinaryReader(s)){
                if(packet.Length<28)throw new InvalidDataException("Truncated secure frame.");
                long gen=r.ReadInt64(),seq=r.ReadInt64();int width=r.ReadInt32(),height=r.ReadInt32(),n=r.ReadInt32();
                if(gen!=descriptor.SharingGeneration||seq<=0||width<1||width>1920||height<1||height>1080||(long)width*height>2073600||n<1||n>524000||s.Length-s.Position!=n)throw new InvalidDataException("Secure frame metadata invalid.");
                byte[] jpeg=r.ReadBytes(n);MediaWire.ValidateJpegStructure(jpeg,width,height);
                lock(sync){if(seq<=lastFrameSequence)return;latest=new FramePacket{Generation=gen,Sequence=seq,Width=width,Height=height,Jpeg=jpeg,StatefulInputVersion=1,MediaOptimizationVersion=1};lastFrameSequence=seq;}
                firstFrame.TrySetResult(true);
            }
        }
        internal async Task<FramePacket> GetFrameAsync(CancellationToken ct,long previousGeneration,long previousSequence){
            await Await(firstFrame.Task,10000,ct).ConfigureAwait(false);Demand(1,false);
            lock(sync){if(failure!=null)throw new IOException("Secure stream failed.",failure);var f=latest;
                return new FramePacket{Generation=f.Generation,Sequence=f.Sequence,Width=f.Width,Height=f.Height,Jpeg=(byte[])f.Jpeg.Clone(),IsDuplicate=f.Generation==previousGeneration&&f.Sequence==previousSequence,StatefulInputVersion=1,MediaOptimizationVersion=1};}
        }
        sealed class PendingInput { internal readonly InputCommand Command; internal readonly long ReceivedAt; internal PendingInput(InputCommand c){Command=new InputCommand{Generation=c.Generation,Sequence=c.Sequence,Kind=c.Kind,X=c.X,Y=c.Y,Button=c.Button,KeyCode=c.KeyCode,Text=c.Text,WheelDelta=c.WheelDelta};ReceivedAt=Stopwatch.GetTimestamp();} }
        internal Task SendInputAsync(InputCommand value,CancellationToken ct){
            MediaWire.ValidateInput(value,true);if(value.Generation!=descriptor.SharingGeneration)throw new InvalidDataException("Wrong secure input generation.");
            return SendAsync(2,new UTF8Encoding(false,true).GetBytes(json.Serialize(new{generation=value.Generation,sequence=value.Sequence,kind=value.Kind,x=value.X,y=value.Y,button=value.Button,keyCode=value.KeyCode,text=value.Text,wheelDelta=value.WheelDelta})),ct);
        }
        void ReceiveInput(byte[] packet){
            if(packet.Length>16384)throw new InvalidDataException("Secure input bound.");
            string raw=new UTF8Encoding(false,true).GetString(packet);ServerProfile.ValidateFlatObjectKeys(raw,json);
            var d=json.Deserialize<Dictionary<string,object>>(raw);
            var batch=new Dictionary<string,object>{{"generation",descriptor.SharingGeneration},{"leaseMs",100},{"events",new object[]{d}}};
            var parsed=MediaWire.ParseInputLease(batch,true);
            if(parsed.Events.Count!=1)throw new InvalidDataException("Invalid secure input batch.");var value=parsed.Events[0];
            object rawGen;if(!d.TryGetValue("generation",out rawGen)||Convert.ToInt64(rawGen)!=descriptor.SharingGeneration||value.Sequence<=lastInputSequence)throw new InvalidDataException("Secure input context/replay.");
            lock(sync){if(inputQueueClosed||failure!=null||disposed!=0||lifetime.IsCancellationRequested)throw new IOException("Secure input queue is closed.");if(value.Kind=="releaseAll")inputs.Clear();if(inputs.Count>=32)throw new InvalidDataException("Secure input backlog.");inputs.Enqueue(new PendingInput(value));lastInputSequence=value.Sequence;}
        }
        internal List<InputCommand> DrainLegacyInputs(){if(descriptor.IsStrictCaptureV1)throw new InvalidOperationException("strict secure input requires immutable authority binding");if((descriptor.PermissionBits&2)==0)return new List<InputCommand>();Demand(2,false);lock(sync){var list=inputs.Select(x=>x.Command).ToList();inputs.Clear();return list;}}
        internal void StopAcceptingInputs(){lock(sync){inputQueueClosed=true;inputs.Clear();}}
        internal int PendingInputBudgetMs(){lock(sync){if(inputs.Count==0)return -1;long age=Stopwatch.GetTimestamp()-inputs.Peek().ReceivedAt;return Math.Max(0,750-(int)(age*1000/Stopwatch.Frequency));}}
        internal void FailPendingInputs(Exception error){Fail(error??new TimeoutException("pending secure input expired"),DiagAdapterOrigin.PendingInputs,-1);}
        internal bool TryBindInputs(InputLeaseAuthority authority,CancellationToken ct,out List<InputCommand> batch){
            batch=new List<InputCommand>();if(authority==null)throw new ArgumentNullException("authority");
            try{
                Demand(2,false);
                if(!Ready||!authority.IsContextValidFor(descriptor,ct))throw new InvalidOperationException("Secure input authority changed.");
                lock(sync){
                    if(failure!=null)throw new IOException("Secure stream failed.",failure);
                    if(!authority.IsContextValidFor(descriptor,ct))throw new InvalidOperationException("Secure input authority changed.");
                    if(inputs.Count>0){long age=Stopwatch.GetTimestamp()-inputs.Peek().ReceivedAt;if(age>=750L*Stopwatch.Frequency/1000){inputs.Clear();throw new TimeoutException("pending secure input exceeded 750ms authority wait");}}
                    if(authority.RequestStart.ElapsedMilliseconds>=authority.LeaseMs)return false;
                    if(authority.Id<=lastBoundAuthorityId)throw new InvalidDataException("input authority replay.");
                    lastBoundAuthorityId=authority.Id;
                    while(inputs.Count>0)batch.Add(inputs.Dequeue().Command);
                    return true;
                }
            }catch(Exception ex){Fail(ex,DiagAdapterOrigin.BindInputs,-1);throw;}
        }
        void Fail(Exception ex){Fail(ex,DiagAdapterOrigin.Unknown,-1);}
        // detail: packet/send channel, or for SendFrame 1=after Seal / 0=before Seal; -1 when not applicable.
        void Fail(Exception ex,DiagAdapterOrigin origin,int detail){
            bool first;
            lock(sync){first=failure==null;if(failure==null)failure=ex;inputQueueClosed=true;inputs.Clear();latest=null;}
            // Logged outside the lock and before any wrapper exception can hide the original reason.
            if(first)DiagnosticLog.Failure(DiagEvent.AdapterFail,diagCorrelation,ex,DiagValue.Of(DiagKey.Origin,origin),DiagValue.Of(DiagKey.Detail,(long)detail),
                DiagValue.Of(DiagKey.Initiator,initiator),DiagValue.Of(DiagKey.Direct,directConnection!=null),DiagValue.Of(DiagKey.Ready,readySignal.Task.Status==TaskStatus.RanToCompletion));
            hello.TrySetException(ex);finished.TrySetException(ex);readySignal.TrySetException(ex);firstFrame.TrySetException(ex);directOffer.TrySetException(ex);
            try{lifetime.Cancel();}catch(ObjectDisposedException){}
        }
        static async Task<T> Await<T>(Task<T> operation,int ms,CancellationToken ct){
            using(var delay=CancellationTokenSource.CreateLinkedTokenSource(ct)){var timeout=Task.Delay(ms,delay.Token);
                if(await Task.WhenAny(operation,timeout).ConfigureAwait(false)!=operation){ct.ThrowIfCancellationRequested();throw new TimeoutException("Secure session response timeout.");}
                delay.Cancel();return await operation.ConfigureAwait(false);}
        }
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;lock(sync){inputQueueClosed=true;inputs.Clear();}try{lifetime.Cancel();}catch{}transport.Dispose();var direct=directConnection;if(direct!=null)DropDirect(direct);crypto.Dispose();
            // Do not dispose CTS/semaphore while an awaited worker may still touch their registrations/Release.
        }
    }
}
