using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace PeerDesk.Windows {
    // Fixed wire values (control 92/93 reason byte). Never carries free text.
    internal enum PeerInputFailureReason : byte { None=0, QueueOverflow=1, QueueAge=2, AuthorityExpired=3, ContextMismatch=4, NativeFailure=5, InputSafetyFailure=6, ProtocolFailure=7, StatefulUnsupported=8, SendFailed=9, Unknown=255 }
    // Negotiated input flow: capability offer 90 / ACK 91, encrypted channel-2 batches, typed failure notice 92 / ACK 93.
    // All control messages travel on authenticated control channel 0 and are bound to this session's sharing generation
    // (the session itself is bound by the key context). Old peers ignore unknown control kinds, so they stay on single sends.
    internal sealed partial class SecureSessionAdapter {
        const byte InputCapsOffer=90, InputCapsAck=91, InputFailureNotice=92, InputFailureAck=93, InputControlVersion=1;
        const int CapBatch=1, CapFailureNotice=2, LocalInputCaps=CapBatch|CapFailureNotice, InputControlLength=11;
        internal const int MaxInputBatchEvents=32, MaxInputBatchBytes=16384;
        const byte BatchMagic=0xB1, BatchVersion=1;const int BatchHeaderBytes=6, BatchEventBytes=39;
        static readonly string[] BatchKinds={null,"move","click","key","text","mouseDown","mouseUp","keyDown","keyUp","wheel","releaseAll","holdHeartbeat"};
        // Field initializers are avoided so constructor-bypassing fixtures see safe zero defaults.
        int capsOfferSent,capsOfferReceived,capsAckReceived,peerAckedCaps,localAckedCaps,peerInputFailure,localInputFailure,packetNoticeReason,batchSamples;
        TaskCompletionSource<bool> failureAck;
        internal bool InputBatchingReady {get{return initiator&&crypto!=null&&lifetime!=null&&Ready&&(Volatile.Read(ref peerAckedCaps)&CapBatch)!=0&&(descriptor.PermissionBits&2)!=0;}}
        internal bool InputFailureNoticeReady {get{return (Volatile.Read(ref peerAckedCaps)&CapFailureNotice)!=0;}}
        internal PeerInputFailureReason PeerInputFailure {get{return (PeerInputFailureReason)Volatile.Read(ref peerInputFailure);}}
        internal PeerInputFailureReason LocalInputFailure {get{return (PeerInputFailureReason)Volatile.Read(ref localInputFailure);}}
        byte[] InputControl(byte kind,int value){
            var p=new byte[InputControlLength];p[0]=kind;p[1]=InputControlVersion;WriteInt64(p,2,descriptor.SharingGeneration);p[10]=(byte)value;return p;
        }
        // Called once after key confirmation; never blocks the handshake and never waits for an old peer.
        void AnnounceInputCapabilities(){
            if(Interlocked.Exchange(ref capsOfferSent,1)!=0)return;
            SendControlBestEffort(InputControl(InputCapsOffer,LocalInputCaps));
        }
        void SendControlBestEffort(byte[] payload){
            Task t;try{t=SendAsync(0,payload,lifetime.Token);}catch(Exception){return;}
            t.ContinueWith(x=>{var ignored=x.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        bool ReceiveInputControl(byte[] plain){
            if(plain.Length==0||plain[0]<InputCapsOffer||plain[0]>InputFailureAck)return false;
            if(plain.Length!=InputControlLength||plain[1]!=InputControlVersion||ReadInt64(plain,2)!=descriptor.SharingGeneration)throw new InvalidDataException("Input control message invalid.");
            int value=plain[10];
            switch(plain[0]){
                case InputCapsOffer:{
                    if(Interlocked.Exchange(ref capsOfferReceived,1)!=0)throw new InvalidDataException("Duplicate input capability offer.");
                    // Only the input receiver (target) accepts batches; both sides may accept failure notices.
                    int accepted=value&(initiator?CapFailureNotice:LocalInputCaps);
                    Volatile.Write(ref localAckedCaps,accepted);
                    SendControlBestEffort(InputControl(InputCapsAck,accepted));
                    return true;}
                case InputCapsAck:
                    if(Volatile.Read(ref capsOfferSent)==0||(value&~LocalInputCaps)!=0||(!initiator&&(value&CapBatch)!=0))throw new InvalidDataException("Unexpected input capability ACK.");
                    if(Interlocked.Exchange(ref capsAckReceived,1)!=0)throw new InvalidDataException("Duplicate input capability ACK.");
                    Volatile.Write(ref peerAckedCaps,value);
                    DiagnosticLog.Event(DiagEvent.InputCapability,diagCorrelation,DiagValue.Of(DiagKey.Capabilities,(long)value),DiagValue.Of(DiagKey.Initiator,initiator));
                    return true;
                case InputFailureNotice:{
                    if((Volatile.Read(ref localAckedCaps)&CapFailureNotice)==0||value==0)throw new InvalidDataException("Unexpected input failure notice.");
                    var reason=MapPeerReason(value);
                    if(Interlocked.CompareExchange(ref peerInputFailure,(int)reason,0)!=0)return true; // first peer failure is immutable
                    DiagnosticLog.Event(DiagEvent.InputFailureNotice,diagCorrelation,DiagValue.Of(DiagKey.Outcome,DiagInputNotice.Received),DiagValue.Of(DiagKey.Reason,reason));
                    SendControlBestEffort(InputControl(InputFailureAck,(int)reason));
                    return true;}
                default:
                    if(Volatile.Read(ref localInputFailure)==0||!InputFailureNoticeReady||value!=Volatile.Read(ref localInputFailure))throw new InvalidDataException("Unexpected input failure ACK.");
                    FailureAckSource().TrySetResult(true);
                    return true;
            }
        }
        static PeerInputFailureReason MapPeerReason(int value){
            switch(value){
                case 1:case 2:case 3:case 4:case 5:case 6:case 7:case 8:case 9:return (PeerInputFailureReason)value;
                default:return PeerInputFailureReason.Unknown;
            }
        }
        TaskCompletionSource<bool> FailureAckSource(){
            var s=Volatile.Read(ref failureAck);if(s!=null)return s;
            Interlocked.CompareExchange(ref failureAck,new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),null);
            return failureAck;
        }
        // Best-effort typed notice. The first local reason is immutable and only one notice is attempted. It never retries a
        // sealed packet and never waits longer than timeoutMs in total; true only when the peer ACKed within that bound.
        // It bypasses Ready so a target may notify right after its own local Fail; a post-Seal error fails the epoch.
        // Outside Ready only this fixed control payload may be sealed, and only after key confirmation, the session context,
        // the descriptor's current generation and input permission are re-validated. The receive side keeps its Ready/Demand
        // boundary, so after a local Fail the peer's ACK cannot be accepted: that outcome is logged as sent without ACK.
        internal async Task<bool> NotifyInputFailureAsync(PeerInputFailureReason reason,int timeoutMs){
            if(reason==PeerInputFailureReason.None)reason=PeerInputFailureReason.Unknown;
            if(Interlocked.CompareExchange(ref localInputFailure,(int)reason,0)!=0)return false;
            if(!InputFailureNoticeReady||crypto==null||!crypto.KeyConfirmed||disposed!=0||!NoticeContextValid()){DiagNotice(DiagInputNotice.NotNegotiated,reason,false);return false;}
            timeoutMs=Math.Max(1,Math.Min(timeoutMs,1000));var watch=Stopwatch.StartNew();var ack=FailureAckSource().Task;
            byte[] payload=InputControl(InputFailureNotice,(int)reason);bool entered=false,sealedOnce=false;
            try{
                using(var cts=new CancellationTokenSource(timeoutMs)){
                    try{
                        entered=await sending.WaitAsync(timeoutMs).ConfigureAwait(false);
                        if(!entered){DiagNotice(DiagInputNotice.GateBusy,reason,false);return false;}
                        if(disposed!=0||!crypto.KeyConfirmed||!NoticeContextValid()||ReadInt64(payload,2)!=descriptor.SharingGeneration)return false;
                        sealedOnce=true;byte[] packet=crypto.Seal(5,payload);
                        await transport.SendAsync(descriptor,0,packet,cts.Token).ConfigureAwait(false);
                    }finally{if(entered)sending.Release();}
                }
            }catch(Exception ex){if(sealedOnce)Fail(ex,DiagAdapterOrigin.Send,0);DiagNotice(DiagInputNotice.SendFailed,reason,false);return false;}
            if(!Ready){DiagNotice(DiagInputNotice.SentAckUnavailable,reason,false);return false;}
            int remaining=timeoutMs-(int)watch.ElapsedMilliseconds;
            bool acked=remaining>0&&await Task.WhenAny(ack,Task.Delay(remaining)).ConfigureAwait(false)==ack;
            DiagNotice(acked?DiagInputNotice.AckReceived:DiagInputNotice.Sent,reason,acked);
            return acked;
        }
        bool NoticeContextValid(){return descriptor!=null&&!String.IsNullOrEmpty(descriptor.SessionId)&&(descriptor.PermissionBits&2)!=0;}
        void DiagNotice(DiagInputNotice outcome,PeerInputFailureReason reason,bool acked){
            DiagnosticLog.Event(DiagEvent.InputFailureNotice,diagCorrelation,DiagValue.Of(DiagKey.Outcome,outcome),DiagValue.Of(DiagKey.Reason,reason),DiagValue.Of(DiagKey.Acked,acked));
        }
        // Target typed path: local Fail first (immutable failure, queue cleared), then a bounded fire-and-forget notice.
        internal void FailPendingInputs(Exception error,PeerInputFailureReason reason){
            FailPendingInputsCore(error);StartFailureNotice(reason);
        }
        void StartFailureNotice(PeerInputFailureReason reason){
            if(reason==PeerInputFailureReason.None||crypto==null)return;
            Task<bool> t;try{t=NotifyInputFailureAsync(reason,250);}catch(Exception){return;}
            t.ContinueWith(x=>{var ignored=x.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        // Viewer batch send. beforeSeal runs after the shared sending gate and immediately before Seal; its exception
        // propagates without failing the adapter (nothing sealed). After Seal any failure is terminal and never retried.
        internal Task SendInputsAsync(IList<InputCommand> values,CancellationToken ct,Action beforeSeal){return SendInputsAgedAsync(values,ct,beforeSeal,null);}
        // oldestAgeMs (optional) reports the caller's original oldest queue age for the sampled diagnostic only (numeric).
        internal async Task SendInputsAgedAsync(IList<InputCommand> values,CancellationToken ct,Action beforeSeal,Func<long> oldestAgeMs){
            if(!InputBatchingReady)throw new InvalidOperationException("Secure input batching is not negotiated.");
            byte[] plain=EncodeInputBatch(values);
            if(values[0].Generation!=descriptor.SharingGeneration)throw new InvalidDataException("Wrong secure input generation.");
            long started=Stopwatch.GetTimestamp();
            long gate=await SendGuardedInputAsync(plain,ct,beforeSeal).ConfigureAwait(false);
            if((Interlocked.Increment(ref batchSamples)&63)==1){long end=Stopwatch.GetTimestamp();
                DiagnosticLog.Event(DiagEvent.InputBatchSample,diagCorrelation,DiagValue.Of(DiagKey.BatchCount,(long)values.Count),
                    DiagValue.Of(DiagKey.GateWaitMs,(gate-started)*1000/Stopwatch.Frequency),DiagValue.Of(DiagKey.RttMs,(end-gate)*1000/Stopwatch.Frequency),
                    DiagValue.Of(DiagKey.AgeMs,oldestAgeMs!=null?oldestAgeMs():-1));}
        }
        // Old-peer single fallback with the same guarded boundary as batches (validated and serialized before the gate).
        internal async Task SendInputGuardedAsync(InputCommand value,CancellationToken ct,Action beforeSeal){
            MediaWire.ValidateInput(value,true);if(value.Generation!=descriptor.SharingGeneration)throw new InvalidDataException("Wrong secure input generation.");
            await SendGuardedInputAsync(SerializeInput(value),ct,beforeSeal).ConfigureAwait(false);
        }
        // Returns the gate-entry timestamp. Only a beforeSeal exception skips Fail; nothing is sealed or retried for it.
        async Task<long> SendGuardedInputAsync(byte[] plain,CancellationToken ct,Action beforeSeal){
            Demand(2,true);bool guardFailed=false;long gate;
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token)){
                await sending.WaitAsync(linked.Token).ConfigureAwait(false);
                try{
                    gate=Stopwatch.GetTimestamp();Demand(2,true);
                    if(beforeSeal!=null){guardFailed=true;beforeSeal();guardFailed=false;}
                    var packet=crypto.Seal(2,plain);
                    await transport.SendAsync(descriptor,2,packet,linked.Token).ConfigureAwait(false);
                }
                catch(Exception ex){if(!guardFailed)Fail(ex,DiagAdapterOrigin.Send,2);throw;}
                finally{sending.Release();}
            }
            return gate;
        }
        void ReceiveInputBatch(byte[] packet){
            if((Volatile.Read(ref localAckedCaps)&CapBatch)==0)throw new InvalidDataException("Secure input batch not negotiated.");
            long previous;lock(sync)previous=lastInputSequence;
            // Pure decode/validation first: a malformed batch never mutates the queue or replay state.
            var values=DecodeInputBatch(packet,descriptor.SharingGeneration,previous);
            lock(sync){
                if(inputQueueClosed||failure!=null||disposed!=0||lifetime.IsCancellationRequested)throw new IOException("Secure input queue is closed.");
                if(lastInputSequence!=previous)throw new InvalidDataException("Secure input context/replay.");
                int count=inputs.Count;
                foreach(var v in values){if(v.Kind=="releaseAll")count=0;if(count>=32){Volatile.Write(ref packetNoticeReason,(int)PeerInputFailureReason.QueueOverflow);throw new InvalidDataException("Secure input backlog.");}count++;}
                foreach(var v in values){if(v.Kind=="releaseAll")inputs.Clear();inputs.Enqueue(new PendingInput(v));}
                lastInputSequence=values[values.Length-1].Sequence;
            }
        }
        // Envelope: magic 0xB1 (never a JSON start byte), version 1, int32 count, then count fixed 39-byte events:
        // int64 generation, int64 sequence, byte kind, int32 x, int32 y, int32 button, int32 keyCode, int32 wheelDelta, uint16 text.
        internal static byte[] EncodeInputBatch(IList<InputCommand> values){
            if(values==null||values.Count<1||values.Count>MaxInputBatchEvents)throw new InvalidDataException("Secure input batch bound.");
            var p=new byte[BatchHeaderBytes+values.Count*BatchEventBytes];
            if(p.Length>MaxInputBatchBytes)throw new InvalidDataException("Secure input batch bound.");
            p[0]=BatchMagic;p[1]=BatchVersion;WriteInt32(p,2,values.Count);
            long generation=0,previous=0;int o=BatchHeaderBytes;
            for(int i=0;i<values.Count;i++){
                var c=values[i];if(c==null)throw new InvalidDataException("Secure input batch event missing.");
                MediaWire.ValidateInput(c,true);
                if(i==0)generation=c.Generation;else if(c.Generation!=generation)throw new InvalidDataException("Secure input batch generation mixed.");
                if(c.Sequence<=previous)throw new InvalidDataException("Secure input batch order.");previous=c.Sequence;
                int code=KindCode(c.Kind);bool text=c.Kind=="text";
                if(!text&&!String.IsNullOrEmpty(c.Text))throw new InvalidDataException("Secure input batch text field.");
                WriteInt64(p,o,c.Generation);WriteInt64(p,o+8,c.Sequence);p[o+16]=(byte)code;
                WriteInt32(p,o+17,c.X);WriteInt32(p,o+21,c.Y);WriteInt32(p,o+25,c.Button);WriteInt32(p,o+29,c.KeyCode);WriteInt32(p,o+33,c.WheelDelta);
                int ch=text?c.Text[0]:0;p[o+37]=(byte)ch;p[o+38]=(byte)(ch>>8);
                o+=BatchEventBytes;
            }
            return p;
        }
        // Pure strict validation; no adapter state is read or written.
        internal static InputCommand[] DecodeInputBatch(byte[] packet,long expectedGeneration,long previousSequence){
            if(packet==null||packet.Length<BatchHeaderBytes+BatchEventBytes||packet.Length>MaxInputBatchBytes||packet[0]!=BatchMagic||packet[1]!=BatchVersion)throw new InvalidDataException("Secure input batch envelope.");
            int count=ReadInt32(packet,2);
            if(count<1||count>MaxInputBatchEvents||packet.Length!=BatchHeaderBytes+count*BatchEventBytes)throw new InvalidDataException("Secure input batch bound.");
            var result=new InputCommand[count];long previous=previousSequence;int o=BatchHeaderBytes;
            for(int i=0;i<count;i++){
                int code=packet[o+16];if(code<1||code>=BatchKinds.Length)throw new InvalidDataException("Secure input batch kind.");
                string kind=BatchKinds[code];int ch=packet[o+37]|(packet[o+38]<<8);
                if((kind=="text")!=(ch!=0))throw new InvalidDataException("Secure input batch text field.");
                var c=new InputCommand{Generation=ReadInt64(packet,o),Sequence=ReadInt64(packet,o+8),Kind=kind,X=ReadInt32(packet,o+17),Y=ReadInt32(packet,o+21),
                    Button=ReadInt32(packet,o+25),KeyCode=ReadInt32(packet,o+29),WheelDelta=ReadInt32(packet,o+33),Text=ch!=0?new string((char)ch,1):""};
                if(c.Generation!=expectedGeneration||c.Sequence<=previous)throw new InvalidDataException("Secure input context/replay.");
                MediaWire.ValidateInput(c,true);
                previous=c.Sequence;result[i]=c;o+=BatchEventBytes;
            }
            return result;
        }
        static int KindCode(string kind){
            for(int i=1;i<BatchKinds.Length;i++)if(BatchKinds[i]==kind)return i;
            throw new InvalidDataException("Secure input batch kind.");
        }
        static void WriteInt32(byte[] p,int o,int v){p[o]=(byte)v;p[o+1]=(byte)(v>>8);p[o+2]=(byte)(v>>16);p[o+3]=(byte)(v>>24);}
        static void WriteInt64(byte[] p,int o,long v){WriteInt32(p,o,(int)v);WriteInt32(p,o+4,(int)(v>>32));}
        static int ReadInt32(byte[] p,int o){return p[o]|(p[o+1]<<8)|(p[o+2]<<16)|(p[o+3]<<24);}
        static long ReadInt64(byte[] p,int o){return (uint)ReadInt32(p,o)|((long)ReadInt32(p,o+4)<<32);}
    }
}
