using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    internal sealed class MediaLease { public long Generation; public int LeaseMs; }
    internal sealed class FramePacket
    {
        public byte[] Jpeg { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Sequence { get; set; }
        public long Generation { get; set; }
        // True when the server returned the same generation+sequence the caller already shows: headers and JPEG
        // structure/bounds were validated, but the GDI decode was skipped. Never means "stopped" (that stays null).
        public bool IsDuplicate { get; set; }
        // 1 only when the target opted into stateful input (X-Stateful-Input-Version: 1); 0 = legacy taps.
        public int StatefulInputVersion { get; set; }
        public int MediaOptimizationVersion { get; set; }
    }
    // Everything a capture proof is bound to; any difference from the live session context invalidates the proof.
    internal sealed class CaptureProofBinding
    {
        public readonly string SessionId,HostId,DeviceId,TokenFingerprint,ServerUrl,PinFingerprint,Scope; public readonly long Revision,Epoch,Generation;
        public CaptureProofBinding(string sessionId,string hostId,string deviceId,string tokenFingerprint,long revision,string serverUrl,string pinFingerprint,long epoch,long generation,string scope)
        {
            SessionId=sessionId;HostId=hostId;DeviceId=deviceId;TokenFingerprint=tokenFingerprint;Revision=revision;ServerUrl=serverUrl;PinFingerprint=pinFingerprint;Epoch=epoch;Generation=generation;Scope=scope;
        }
        public bool SameAs(CaptureProofBinding o)
        {
            return o!=null&&String.Equals(SessionId,o.SessionId,StringComparison.Ordinal)&&String.Equals(HostId,o.HostId,StringComparison.Ordinal)
                &&String.Equals(DeviceId,o.DeviceId,StringComparison.Ordinal)&&String.Equals(TokenFingerprint,o.TokenFingerprint,StringComparison.Ordinal)
                &&Revision==o.Revision&&String.Equals(ServerUrl,o.ServerUrl,StringComparison.Ordinal)&&String.Equals(PinFingerprint,o.PinFingerprint,StringComparison.Ordinal)
                &&Epoch==o.Epoch&&Generation==o.Generation&&String.Equals(Scope,o.Scope,StringComparison.Ordinal);
        }
    }
    // Immutable: expiry is measured from the Stopwatch stamp taken before the lease request, never renewed for the same frame.
    internal sealed class CaptureProof
    {
        public readonly CaptureProofBinding Binding; public readonly long RequestedAt,ExpiresAt;
        private CaptureProof(CaptureProofBinding b,long requestedAt) { Binding=b;RequestedAt=requestedAt;ExpiresAt=requestedAt+MediaWire.CaptureLeaseMs*Stopwatch.Frequency/1000; }
        internal static CaptureProof Create(long generation,CaptureProofBinding binding,long requestedAt)
        {
            if(binding==null||generation!=binding.Generation)throw new InvalidOperationException("capture lease generation mismatch");
            return new CaptureProof(binding,requestedAt);
        }
        public bool Expired { get { return Stopwatch.GetTimestamp()>=ExpiresAt; } }
        // Throws CaptureProofExpiredException for pure expiry (retryable by caller policy); any binding mismatch is fatal.
        public void Check(CaptureProofBinding live)
        {
            if(!Binding.SameAs(live))throw new InvalidOperationException("capture proof binding changed");
            if(Expired)throw new CaptureProofExpiredException();
        }
    }
    internal sealed class CaptureProofExpiredException : InvalidOperationException { public CaptureProofExpiredException():base("capture proof expired") {} }

    // Additive sharing response fields. Absent fields (old server) => legacy sequential 200ms loop.
    internal sealed class SharingCapabilities
    {
        public long Generation; public int MinFrameIntervalMs; public bool InputLeasePollSupported;
        public bool Negotiated { get { return InputLeasePollSupported&&MinFrameIntervalMs>0; } }
        public int StatefulInputVersion; public int InputPollIntervalMs; public int MediaOptimizationVersion;
        public int CaptureLeaseVersion; public int CaptureLeaseMs;
        public bool CaptureLeased { get { return CaptureLeaseVersion==MediaWire.CaptureLeaseVersion1&&CaptureLeaseMs==MediaWire.CaptureLeaseMs&&MinFrameIntervalMs==MediaWire.MinFrameIntervalFloorMs; } }
        // Stateful only with strict version 1 + 50ms poll on top of the negotiated lease poll; anything else stays legacy 100ms.
        public bool Stateful { get { return Negotiated&&StatefulInputVersion==1&&InputPollIntervalMs==MediaWire.StatefulInputPollIntervalMs; } }
    }
    internal sealed class InputLeaseBatch { public long Generation; public int LeaseMs; public List<InputCommand> Events; }
    // Immutable authority for one secure input-lease response; request start is never renewed.
    internal sealed class InputLeaseAuthority {
        static long nextId; internal readonly long Id,Generation,CredentialRevision,Epoch; internal readonly int LeaseMs;
        internal readonly Stopwatch RequestStart; internal readonly CaptureProofBinding Binding; internal readonly SessionTransportDescriptor Descriptor; internal readonly CancellationToken Cancellation; readonly Func<bool> contextCurrent;
        internal InputLeaseAuthority(long generation,int leaseMs,Stopwatch requestStart,CaptureProofBinding binding,long credentialRevision,long epoch,SessionTransportDescriptor descriptor,CancellationToken ct,Func<bool> isCurrent){
            if(generation<=0||leaseMs<1||leaseMs>MediaWire.MaxInputLeaseMs||requestStart==null||binding==null||descriptor==null||generation!=binding.Generation||epoch!=binding.Epoch||credentialRevision!=binding.Revision||binding.SessionId!=descriptor.SessionId||binding.DeviceId!=descriptor.TargetDeviceId||(descriptor.PermissionBits&2)==0||isCurrent==null)throw new InvalidOperationException("invalid input lease authority");
            Id=Interlocked.Increment(ref nextId);Generation=generation;LeaseMs=leaseMs;RequestStart=requestStart;Binding=binding;CredentialRevision=credentialRevision;Epoch=epoch;Descriptor=descriptor;Cancellation=ct;contextCurrent=isCurrent;
        }
        internal bool IsContextValidFor(SessionTransportDescriptor descriptor,CancellationToken ct){
            if(ct.IsCancellationRequested||Cancellation.IsCancellationRequested||!ct.Equals(Cancellation)||descriptor==null||!Object.ReferenceEquals(Descriptor,descriptor)||Generation!=descriptor.SharingGeneration||Binding.Generation!=Generation||Epoch!=Binding.Epoch||(CredentialRevision&1L)!=0)return false;
            try{return contextCurrent();}catch{return false;}
        }
        internal bool IsFresh {get{return RequestStart.ElapsedMilliseconds<LeaseMs;}}
    }
    internal sealed class InputCommand
    {
        public long generation { get; set; }
        public long sequence { get; set; }
        public string kind { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int button { get; set; }
        public int keyCode { get; set; }
        public string text { get; set; }
        public int wheelDelta { get; set; }
        public int WheelDelta { get { return wheelDelta; } set { wheelDelta=value; } }
        public long Generation { get { return generation; } set { generation=value; } }
        public long Sequence { get { return sequence; } set { sequence=value; } }
        public string Kind { get { return kind; } set { kind=value; } }
        public int X { get { return x; } set { x=value; } }
        public int Y { get { return y; } set { y=value; } }
        public int Button { get { return button; } set { button=value; } }
        public int KeyCode { get { return keyCode; } set { keyCode=value; } }
        public string Text { get { return text; } set { text=value; } }
    }

    internal sealed class MediaWire
    {
        private readonly ControlPlaneClient client;
        internal SecureSessionAdapter Secure {get;set;}
        internal int FrameByteBudget {get{return Secure==null?524288:524000;}}
        private static T Deserialize<T>(string raw) { return new JavaScriptSerializer { MaxJsonLength=65536 }.Deserialize<T>(raw); }
        internal MediaWire(ControlPlaneClient c) { client=c; }
        internal const int MinFrameIntervalFloorMs=100, MinFrameIntervalCeilingMs=1000, MaxInputBatch=32, MaxInputLeaseMs=100, StatefulInputPollIntervalMs=50, LegacyInputPollIntervalMs=100, OptimizedFrameIntervalMs=50;
        internal static bool IsStatefulKind(string k) { return k=="mouseDown"||k=="mouseUp"||k=="keyDown"||k=="keyUp"||k=="wheel"||k=="releaseAll"||k=="holdHeartbeat"; }
        internal async Task<long> SetSharingAsync(string sessionId,bool enabled,CancellationToken ct)
        {
            return await SetSharingAsync(sessionId,enabled,ct,null).ConfigureAwait(false);
        }
        internal async Task<long> SetSharingAsync(string sessionId,bool enabled,CancellationToken ct,string stopReason)
        {
            object body=stopReason==null?(object)new{enabled=enabled}:new{enabled=enabled,stopReason=stopReason};
            string r=await client.SendJsonAsync("POST",Path(sessionId)+"/sharing",body,ct).ConfigureAwait(false);
            var d=Deserialize<Dictionary<string,object>>(r);
            return Number(d,"generation");
        }
        internal Task<SharingCapabilities> SetSharingWithCapabilitiesAsync(string sessionId,bool enabled,CancellationToken ct) { return SetSharingWithCapabilitiesAsync(sessionId,enabled,false,ct); }
        // requestStateful adds statefulInputVersion:1 (target opt-in); absent = version 0 (old behavior).
        internal async Task<SharingCapabilities> SetSharingWithCapabilitiesAsync(string sessionId,bool enabled,bool requestStateful,CancellationToken ct)
        {
            object body=requestStateful&&enabled?(object)new { enabled=enabled,statefulInputVersion=1,mediaOptimizationVersion=1 }:new { enabled=enabled };
            string r=await client.SendJsonAsync("POST",Path(sessionId)+"/sharing",body,ct).ConfigureAwait(false);
            return ParseCapabilities(Deserialize<Dictionary<string,object>>(r));
        }
        // New secure candidate: requests captureLeaseVersion 1 and fails closed unless the strict capture caps come back.
        internal async Task<SharingCapabilities> SetSharingWithCaptureLeaseAsync(string sessionId,CancellationToken ct)
        {
            object body=new { enabled=true,statefulInputVersion=1,mediaOptimizationVersion=1,captureLeaseVersion=CaptureLeaseVersion1 };
            string r=await client.SendJsonAsync("POST",Path(sessionId)+"/sharing",body,ct).ConfigureAwait(false);
            return ParseCaptureLeaseCapabilitiesJson(r);
        }
        // Raw body: duplicate keys are rejected before the (last-wins) serializer can hide them.
        internal static SharingCapabilities ParseCaptureLeaseCapabilitiesJson(string r)
        {
            RejectDuplicateKeys(r);
            var caps=ParseCapabilities(Deserialize<Dictionary<string,object>>(r));
            if(!caps.CaptureLeased||!caps.Stateful||caps.MediaOptimizationVersion!=1)throw new InvalidOperationException("secure capture requires captureLeaseVersion 1 capabilities");
            return caps;
        }
        internal const int CaptureLeaseVersion1=1, CaptureLeaseMs=250;
        // Target-only. Returns an immutable proof anchored to a Stopwatch timestamp taken before the request was sent.
        internal async Task<CaptureProof> GetCaptureLeaseAsync(string sessionId,CaptureProofBinding binding,CancellationToken ct)
        {
            if(binding==null)throw new ArgumentNullException("binding");
            long requestedAt=Stopwatch.GetTimestamp();
            string raw=await client.SendJsonAsync("GET",Path(sessionId)+"/capture-lease",null,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return CaptureProof.Create(ParseCaptureLeaseJson(raw,binding.Generation),binding,requestedAt);
        }
        // Raw body: duplicate keys are rejected before the (last-wins) serializer can hide them.
        internal static long ParseCaptureLeaseJson(string raw,long expectedGeneration)
        {
            RejectDuplicateKeys(raw);
            return ParseCaptureLease(Deserialize<Dictionary<string,object>>(raw),expectedGeneration);
        }
        // Exactly {generation, captureLeaseVersion:1, leaseMs:250}; any extra/missing/non-integer field is fatal.
        internal static long ParseCaptureLease(Dictionary<string,object> d,long expectedGeneration)
        {
            if(d==null||d.Count!=3)throw new InvalidOperationException("invalid capture lease response");
            long gen=StrictField(d,"generation"),version=StrictField(d,"captureLeaseVersion"),ms=StrictField(d,"leaseMs");
            if(gen<=0||gen!=expectedGeneration)throw new InvalidOperationException("capture lease generation mismatch");
            if(version!=CaptureLeaseVersion1||ms!=CaptureLeaseMs)throw new InvalidOperationException("invalid capture lease capability");
            return gen;
        }
        // Strict structural scan: one JSON value, object keys unique per object at any depth, no trailing data.
        internal static void RejectDuplicateKeys(string json)
        {
            if(json==null)throw new InvalidOperationException("invalid JSON");
            int p=0; SkipWs(json,ref p); ScanValue(json,ref p,0); SkipWs(json,ref p);
            if(p!=json.Length)throw new InvalidOperationException("trailing JSON data");
        }
        private static void SkipWs(string s,ref int p){while(p<s.Length&&(s[p]==' '||s[p]=='\t'||s[p]=='\r'||s[p]=='\n'))p++;}
        private static void ScanValue(string s,ref int p,int depth)
        {
            if(depth>32||p>=s.Length)throw new InvalidOperationException("invalid JSON");
            char c=s[p];
            if(c=='{') {
                p++; var keys=new HashSet<string>(StringComparer.Ordinal); SkipWs(s,ref p);
                if(p<s.Length&&s[p]=='}'){p++;return;}
                while(true) {
                    SkipWs(s,ref p); if(p>=s.Length||s[p]!='"')throw new InvalidOperationException("invalid JSON key");
                    if(!keys.Add(ScanString(s,ref p)))throw new InvalidOperationException("duplicate JSON property");
                    SkipWs(s,ref p); if(p>=s.Length||s[p]!=':')throw new InvalidOperationException("invalid JSON");
                    p++; SkipWs(s,ref p); ScanValue(s,ref p,depth+1); SkipWs(s,ref p);
                    if(p<s.Length&&s[p]==','){p++;continue;}
                    if(p<s.Length&&s[p]=='}'){p++;return;}
                    throw new InvalidOperationException("invalid JSON");
                }
            }
            if(c=='[') {
                p++; SkipWs(s,ref p); if(p<s.Length&&s[p]==']'){p++;return;}
                while(true) {
                    SkipWs(s,ref p); ScanValue(s,ref p,depth+1); SkipWs(s,ref p);
                    if(p<s.Length&&s[p]==','){p++;continue;}
                    if(p<s.Length&&s[p]==']'){p++;return;}
                    throw new InvalidOperationException("invalid JSON");
                }
            }
            if(c=='"'){ScanString(s,ref p);return;}
            int start=p; while(p<s.Length&&(Char.IsLetterOrDigit(s[p])||s[p]=='-'||s[p]=='+'||s[p]=='.'))p++;
            if(p==start)throw new InvalidOperationException("invalid JSON");
        }
        private static string ScanString(string s,ref int p)
        {
            var b=new StringBuilder(); p++;
            while(p<s.Length) {
                char c=s[p++];
                if(c=='"')return b.ToString();
                if(c=='\\') {
                    if(p>=s.Length)break; char e=s[p++];
                    if(e=='u'){if(p+4>s.Length)break;b.Append((char)Convert.ToInt32(s.Substring(p,4),16));p+=4;}
                    else b.Append(e=='n'?'\n':e=='t'?'\t':e=='r'?'\r':e=='b'?'\b':e=='f'?'\f':e);
                } else b.Append(c);
            }
            throw new InvalidOperationException("unterminated JSON string");
        }
        // Present-but-malformed fields are rejected. Legacy max10fps; explicit optimized/stateful v1 max20fps.
        internal static SharingCapabilities ParseCapabilities(Dictionary<string,object> d)
        {
            if(d==null)throw new InvalidOperationException("invalid sharing capabilities");
            long gen=StrictField(d,"generation");if(gen<=0)throw new InvalidOperationException("invalid sharing generation");
            var caps=new SharingCapabilities { Generation=gen };
            object optimization; long ov=0;
            if(d.TryGetValue("mediaOptimizationVersion",out optimization)&&(!StrictInteger(optimization,out ov)||(ov!=0&&ov!=1)))throw new InvalidOperationException("invalid mediaOptimizationVersion");
            caps.MediaOptimizationVersion=(int)ov;
            object v; bool hasInterval=d.TryGetValue("minFrameIntervalMs",out v);
            if(hasInterval) { long ms; if(!StrictInteger(v,out ms)||ms<(ov==1?OptimizedFrameIntervalMs:MinFrameIntervalFloorMs)||ms>MinFrameIntervalCeilingMs)throw new InvalidOperationException("invalid minFrameIntervalMs capability"); caps.MinFrameIntervalMs=(int)ms; }
            object s;
            if(d.TryGetValue("inputLeasePollSupported",out s)) { if(!(s is bool))throw new InvalidOperationException("invalid inputLeasePollSupported capability"); caps.InputLeasePollSupported=(bool)s; }
            if(caps.InputLeasePollSupported&&!hasInterval)throw new InvalidOperationException("inputLeasePollSupported requires minFrameIntervalMs");
            object sv,pi; long n;
            if(d.TryGetValue("statefulInputVersion",out sv)) { if(!StrictInteger(sv,out n)||(n!=0&&n!=1))throw new InvalidOperationException("invalid statefulInputVersion capability"); caps.StatefulInputVersion=(int)n; }
            if(d.TryGetValue("inputPollIntervalMs",out pi)) {
                if(!StrictInteger(pi,out n))throw new InvalidOperationException("invalid inputPollIntervalMs capability");
                if(caps.StatefulInputVersion==1?n!=StatefulInputPollIntervalMs:n!=LegacyInputPollIntervalMs)throw new InvalidOperationException("inputPollIntervalMs does not match statefulInputVersion");
                caps.InputPollIntervalMs=(int)n;
            }
            if(caps.StatefulInputVersion==1&&(caps.InputPollIntervalMs!=StatefulInputPollIntervalMs||!caps.Negotiated))throw new InvalidOperationException("statefulInputVersion 1 requires inputPollIntervalMs 50 and input lease poll");
            object cv,cm; long clv=0;
            if(d.TryGetValue("captureLeaseVersion",out cv)&&(!StrictInteger(cv,out clv)||(clv!=0&&clv!=CaptureLeaseVersion1)))throw new InvalidOperationException("invalid captureLeaseVersion");
            caps.CaptureLeaseVersion=(int)clv;
            bool hasCaptureMs=d.TryGetValue("captureLeaseMs",out cm);
            if(clv==CaptureLeaseVersion1) {
                long cms; if(!hasCaptureMs||!StrictInteger(cm,out cms)||cms!=CaptureLeaseMs)throw new InvalidOperationException("captureLeaseVersion 1 requires captureLeaseMs 250");
                caps.CaptureLeaseMs=(int)cms;
                if(ov!=1||!caps.Stateful||caps.MinFrameIntervalMs!=MinFrameIntervalFloorMs)throw new InvalidOperationException("captureLeaseVersion 1 requires optimized stateful v1/100ms frame interval");
            } else {
                if(hasCaptureMs)throw new InvalidOperationException("captureLeaseMs without captureLeaseVersion 1");
                if(ov==1&&(!caps.Stateful||caps.MinFrameIntervalMs!=OptimizedFrameIntervalMs))throw new InvalidOperationException("optimized media requires stateful v1/50ms frame interval");
            }
            return caps;
        }
        internal Task<FramePacket> GetFrameAsync(string sessionId) { return GetFrameAsync(sessionId,CancellationToken.None); }
        internal Task<FramePacket> GetFrameAsync(string sessionId,CancellationToken ct) { return GetFrameAsync(sessionId,ct,0,0); }
        internal Task<FramePacket> GetFrameAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence) { return GetFrameCoreAsync(sessionId,ct,previousGeneration,previousSequence,true); }
        internal Task<FramePacket> GetFrameForDisplayAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence) { return GetFrameCoreAsync(sessionId,ct,previousGeneration,previousSequence,false); }
        private async Task<FramePacket> GetFrameCoreAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence,bool decode)
        {
            if(Secure!=null)return await Secure.GetFrameAsync(ct,previousGeneration,previousSequence).ConfigureAwait(false);
            var r=await client.SendMediaAsync("GET",Path(sessionId)+"/frame",null,null,ct).ConfigureAwait(false);
            if(r.StatusCode==204)return null;
            if(!String.Equals(r.ContentType,"image/jpeg",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("frame content type must be image/jpeg");
            long generation=HeaderLong(r,"X-Share-Generation"), seq=HeaderLong(r,"X-Frame-Sequence");
            long widthValue=HeaderLong(r,"X-Frame-Width"),heightValue=HeaderLong(r,"X-Frame-Height");
            if(generation<=0||seq<=0||widthValue<=0||widthValue>1920||heightValue<=0||heightValue>1080||widthValue*heightValue>2073600)throw new InvalidOperationException("frame header dimensions/generation invalid");
            int width=(int)widthValue,height=(int)heightValue;
            string sv; int stateful=0;
            if(r.Headers.TryGetValue("X-Stateful-Input-Version",out sv)) { if(sv!="1")throw new InvalidOperationException("invalid X-Stateful-Input-Version"); stateful=1; }
            string opt; int optimizationVersion=0;
            if(r.Headers.TryGetValue("X-Media-Optimization-Version",out opt)){if(opt!="1"||stateful!=1)throw new InvalidOperationException("invalid optimized media frame capability");optimizationVersion=1;}
            if(previousGeneration>0&&generation==previousGeneration&&seq==previousSequence) {
                ValidateJpegStructure(r.Body,width,height);
                return new FramePacket { Jpeg=r.Body,Width=width,Height=height,Sequence=seq,Generation=generation,IsDuplicate=true,StatefulInputVersion=stateful,MediaOptimizationVersion=optimizationVersion };
            }
            if(decode)ValidateJpeg(r.Body,width,height);else ValidateJpegStructure(r.Body,width,height);
            return new FramePacket { Jpeg=r.Body,Width=width,Height=height,Sequence=seq,Generation=generation,StatefulInputVersion=stateful,MediaOptimizationVersion=optimizationVersion };
        }
        internal async Task PutFrameAsync(string sessionId,FramePacket frame,CancellationToken ct)
        {
            if(Secure!=null){await Secure.SendFrameAsync(frame,ct).ConfigureAwait(false);return;}
            var h=new Dictionary<string,string> {
                ["X-Share-Generation"]=frame.Generation.ToString(),["X-Frame-Sequence"]=frame.Sequence.ToString(),
                ["X-Frame-Width"]=frame.Width.ToString(),["X-Frame-Height"]=frame.Height.ToString()
            };
            await client.SendMediaAsync("PUT",Path(sessionId)+"/frame",frame.Jpeg,h,ct).ConfigureAwait(false);
        }
        // capture-v1: the guard is the dedicated proof check; the secure adapter runs it before seal and before the actual write.
        internal async Task PutFrameAsync(string sessionId,FramePacket frame,CancellationToken ct,Action authorizationGuard)
        {
            if(authorizationGuard==null)throw new ArgumentNullException("authorizationGuard");
            if(Secure==null)throw new InvalidOperationException("capture-v1 frames require a secure session");
            await Secure.SendFrameAsync(frame,ct,authorizationGuard).ConfigureAwait(false);
        }
        internal Task SendInputAsync(string sessionId,InputCommand command) { return SendInputAsync(sessionId,command,CancellationToken.None); }
        internal async Task SendInputAsync(string sessionId,InputCommand command,CancellationToken ct)
        {
            if(Secure!=null){await Secure.SendInputAsync(command,ct).ConfigureAwait(false);return;}
            // wheelDelta is only on the wire for wheel, so legacy request bodies stay byte-identical.
            object body=command.Kind=="wheel"
                ?(object)new { generation=command.Generation, sequence=command.Sequence, kind=command.Kind, x=command.X, y=command.Y, button=command.Button, keyCode=command.KeyCode, text=command.Text, wheelDelta=command.WheelDelta }
                :new { generation=command.Generation, sequence=command.Sequence, kind=command.Kind, x=command.X, y=command.Y, button=command.Button, keyCode=command.KeyCode, text=command.Text };
            await client.SendJsonAsync("POST",Path(sessionId)+"/input",body,ct).ConfigureAwait(false);
        }
        internal async Task<List<InputCommand>> PollInputAsync(string sessionId,CancellationToken ct)
        {
            string raw=await client.SendJsonAsync("GET",Path(sessionId)+"/input",null,ct).ConfigureAwait(false);
            var d=Deserialize<Dictionary<string,object>>(raw);
            if(d==null)throw new InvalidOperationException("invalid input response");
            long gen=Number(d,"generation"); object items;
            if(!d.TryGetValue("events",out items)||(items is string)||(items is IDictionary)||(!(items is ArrayList)&&!(items is object[])))throw new InvalidOperationException("invalid input event list");
            IEnumerable rows=(IEnumerable)items; var output=new List<InputCommand>();
            foreach(object row in rows) {
                var x=row as Dictionary<string,object>; if(x==null)throw new InvalidOperationException("invalid input event");
                var c=new InputCommand { Generation=gen,Sequence=Number(x,"sequence"),Kind=StringField(x,"kind"),X=(int)Number(x,"x"),Y=(int)Number(x,"y"),Button=(int)OptionalNumber(x,"button"),KeyCode=(int)OptionalNumber(x,"keyCode"),Text=OptionalString(x,"text") };
                ValidateInput(c); output.Add(c);
            }
            return output;
        }
        internal Task<InputLeaseBatch> PollInputLeaseAsync(string sessionId,CancellationToken ct) { return PollInputLeaseAsync(sessionId,false,ct,false); }
        internal Task<InputLeaseBatch> PollInputLeaseAsync(string sessionId,bool allowStateful,CancellationToken ct) { return PollInputLeaseAsync(sessionId,allowStateful,ct,false); }
        internal async Task<InputLeaseBatch> PollInputLeaseAsync(string sessionId,bool allowStateful,CancellationToken ct,bool deferSecureBinding)
        {
            string raw=await client.SendJsonAsync("GET",Path(sessionId)+"/input-lease",null,ct).ConfigureAwait(false);
            var batch=ParseInputLease(Deserialize<Dictionary<string,object>>(raw),allowStateful);
            if(Secure!=null){
                if(!Secure.Ready){var failure=Secure.Failure;if(failure!=null)throw failure;throw new InvalidOperationException("Secure session authority changed.");}
                if(batch.Generation!=Secure.Descriptor.SharingGeneration)throw new InvalidOperationException("Secure session authority changed.");
                if(deferSecureBinding)batch.Events=new List<InputCommand>();
                else if(Secure.Descriptor.IsStrictCaptureV1)throw new InvalidOperationException("strict secure input requires deferred authority binding");
                else batch.Events=Secure.DrainLegacyInputs();
            }
            return batch;
        }
        // Strict: every row is parsed and validated before the batch is returned, so nothing is applied from a bad batch.
        internal static InputLeaseBatch ParseInputLease(Dictionary<string,object> d) { return ParseInputLease(d,false); }
        internal static InputLeaseBatch ParseInputLease(Dictionary<string,object> d,bool allowStateful)
        {
            if(d==null)throw new InvalidOperationException("invalid input lease response");
            long gen=StrictField(d,"generation"), ms=StrictField(d,"leaseMs");
            if(gen<=0)throw new InvalidOperationException("invalid input lease generation");
            if(ms<1||ms>MaxInputLeaseMs)throw new InvalidOperationException("invalid input lease duration");
            object items;
            if(!d.TryGetValue("events",out items)||(!(items is ArrayList)&&!(items is object[])))throw new InvalidOperationException("invalid input event list");
            var rows=(IList)items; if(rows.Count>MaxInputBatch)throw new InvalidOperationException("input batch exceeds "+MaxInputBatch);
            var output=new List<InputCommand>(rows.Count);long previousSequence=0;
            foreach(object row in rows) {
                var x=row as Dictionary<string,object>; if(x==null)throw new InvalidOperationException("invalid input event");
                object kind; if(!x.TryGetValue("kind",out kind)||!(kind is string))throw new InvalidOperationException("invalid input kind");
                var c=new InputCommand { Generation=gen,Sequence=StrictField(x,"sequence"),Kind=(string)kind,X=StrictInt(x,"x",false),Y=StrictInt(x,"y",false),Button=StrictInt(x,"button",true),KeyCode=StrictInt(x,"keyCode",true),Text=OptionalStrictString(x,"text"),WheelDelta=StrictInt(x,"wheelDelta",true) };
                if(StrictField(x,"generation")!=gen||c.Sequence<=previousSequence)throw new InvalidOperationException("input batch generation or sequence invalid");
                ValidateInput(c,allowStateful);previousSequence=c.Sequence; output.Add(c);
            }
            return new InputLeaseBatch { Generation=gen,LeaseMs=(int)ms,Events=output };
        }
        private static bool StrictInteger(object v,out long n) { if(v is int){n=(int)v;return true;} if(v is long){n=(long)v;return true;} n=0;return false; }
        private static long StrictField(Dictionary<string,object> d,string k) { object v; long n; if(!d.TryGetValue(k,out v)||!StrictInteger(v,out n))throw new InvalidOperationException("missing or non-integer field "+k); return n; }
        private static int StrictInt(Dictionary<string,object> d,string k,bool optional)
        {
            object v; long n;
            if(!d.TryGetValue(k,out v)||v==null) { if(optional)return 0; throw new InvalidOperationException("missing field "+k); }
            if(!StrictInteger(v,out n)||n<Int32.MinValue||n>Int32.MaxValue)throw new InvalidOperationException("non-integer field "+k);
            return (int)n;
        }
        private static string OptionalStrictString(Dictionary<string,object> d,string k) { object v; if(!d.TryGetValue(k,out v)||v==null)return ""; if(!(v is string))throw new InvalidOperationException("non-string field "+k); return (string)v; }
        internal async Task<MediaLease> GetLeaseAsync(string sessionId,CancellationToken ct)
        {
            string raw=await client.SendJsonAsync("GET",Path(sessionId)+"/lease",null,ct).ConfigureAwait(false);
            var d=Deserialize<Dictionary<string,object>>(raw); int ms=(int)Number(d,"leaseMs");
            if(ms<=0||ms>100)throw new InvalidOperationException("invalid lease duration");
            return new MediaLease { Generation=Number(d,"generation"), LeaseMs=ms };
        }
        internal static void ValidateInput(InputCommand c) { ValidateInput(c,false); }
        // Stateful kinds are rejected unless this stream negotiated statefulInputVersion 1.
        internal static void ValidateInput(InputCommand c,bool allowStateful)
        {
            if(IsStatefulKind(c.Kind)&&!allowStateful)throw new InvalidOperationException("stateful input kind not negotiated");
            if(IsStatefulKind(c.Kind)) {
                bool noPoint=c.X==0&&c.Y==0, noText=String.IsNullOrEmpty(c.Text);
                bool fields=c.Kind=="mouseDown"||c.Kind=="mouseUp" ? c.KeyCode==0&&noText :
                    c.Kind=="keyDown"||c.Kind=="keyUp" ? noPoint&&c.Button==0&&noText :
                    c.Kind=="wheel" ? c.Button==0&&c.KeyCode==0&&noText :
                    noPoint&&c.Button==0&&c.KeyCode==0&&noText;
                if(!fields)throw new InvalidOperationException("invalid unused stateful input fields");
            }
            if(c.Kind!="wheel"&&c.WheelDelta!=0)throw new InvalidOperationException("wheelDelta only valid for wheel");
            if(c.Generation<=0||c.Sequence<=0||c.X<0||c.X>65535||c.Y<0||c.Y>65535)throw new InvalidOperationException("invalid input coordinates/generation");
            switch(c.Kind) {
                case "move": break;
                case "click": if(c.Button<0||c.Button>2)throw new InvalidOperationException("invalid button"); break;
                case "key": if(c.KeyCode<1||c.KeyCode>254)throw new InvalidOperationException("invalid key"); break;
                case "text": if(String.IsNullOrEmpty(c.Text)||c.Text.Length!=1||Char.IsSurrogate(c.Text[0])||Char.IsControl(c.Text[0]))throw new InvalidOperationException("invalid text tap"); break;
                case "mouseDown": case "mouseUp": if(c.Button<0||c.Button>2)throw new InvalidOperationException("invalid button"); break;
                case "keyDown": case "keyUp": if(c.KeyCode<1||c.KeyCode>254||InputSafety.IsMouseVk(c.KeyCode))throw new InvalidOperationException("invalid key"); break;
                case "wheel": if(c.WheelDelta==0||c.WheelDelta<-1200||c.WheelDelta>1200)throw new InvalidOperationException("invalid wheel delta"); break;
                case "releaseAll": case "holdHeartbeat": break;
                default: throw new InvalidOperationException("unsupported input kind");
            }
        }
        private static string Path(string id) { return "/api/v1/sessions/"+Uri.EscapeDataString(id); }
        private static string StringField(Dictionary<string,object> d,string k) { return Convert.ToString(d[k]); }
        private static string OptionalString(Dictionary<string,object> d,string k) { object v; return d.TryGetValue(k,out v)&&v!=null?Convert.ToString(v):""; }
        private static long OptionalNumber(Dictionary<string,object> d,string k) { object v; return d.TryGetValue(k,out v)&&v!=null?Convert.ToInt64(v):0; }
        private static long Number(Dictionary<string,object> d,string k) { object v; if(d==null||!d.TryGetValue(k,out v))throw new InvalidOperationException("missing field "+k); return Convert.ToInt64(v); }
        private static long HeaderLong(MediaHttpResponse r,string k) { string v; long n; if(!r.Headers.TryGetValue(k,out v)||!Int64.TryParse(v,out n)||n<0)throw new InvalidOperationException("invalid frame header "+k);return n; }
        internal static void ValidateJpeg(byte[] bytes,int width,int height)
        {
            ValidateJpegStructure(bytes,width,height);
            using(var ms=new MemoryStream(bytes,false)) using(var image=Image.FromStream(ms,false,true)) {
                if(image.Width!=width||image.Height!=height)throw new InvalidOperationException("decoded JPEG size mismatch");
            }
        }
        // Bounds, SOI/EOI markers and SOF dimensions only (no GDI decode).
        internal static void ValidateJpegStructure(byte[] bytes,int width,int height)
        {
            if(bytes==null||bytes.Length<4||bytes.Length>524288||bytes[0]!=0xff||bytes[1]!=0xd8||bytes[bytes.Length-2]!=0xff||bytes[bytes.Length-1]!=0xd9||width<=0||height<=0||(long)width*height>2073600||width>1920||height>1080)throw new InvalidOperationException("frame bounds or JPEG markers invalid");
            int p=2; bool found=false;
            while(p+3<bytes.Length) {
                if(bytes[p++]!=0xff)continue;
                while(p<bytes.Length&&bytes[p]==0xff)p++;
                if(p>=bytes.Length)break;
                int marker=bytes[p++]; if(marker==0xd9||marker==0xda)break;
                if(marker==0x01||(marker>=0xd0&&marker<=0xd7))continue;
                if(p+1>=bytes.Length)break;
                int len=(bytes[p]<<8)|bytes[p+1]; if(len<2||p+len>bytes.Length)break;
                if((marker>=0xc0&&marker<=0xc3)||(marker>=0xc5&&marker<=0xc7)||(marker>=0xc9&&marker<=0xcb)||(marker>=0xcd&&marker<=0xcf)) {
                    if(len<7)break; int h=(bytes[p+3]<<8)|bytes[p+4],w=(bytes[p+5]<<8)|bytes[p+6];
                    if(w!=width||h!=height)throw new InvalidOperationException("JPEG dimensions do not match headers"); found=true; break;
                }
                p+=len;
            }
            if(!found)throw new InvalidOperationException("JPEG SOF not found");
        }
    }

    internal sealed class MediaSharingSession : IDisposable
    {
        private readonly ControlPlaneClient host; private readonly string sessionId; private readonly CaptureScope scope;
        private readonly MediaWire wire; private CancellationTokenSource cts; private Task runner; private long generation,sequence;
        private SharingCapabilities preparedCaps;private long preparedCredentialRevision;private CancellationToken preparedAuthorization;
        internal async Task<SharingCapabilities> PrepareSecureAsync(CancellationToken authorization){
            authorization.ThrowIfCancellationRequested();if(running||preparedCaps!=null)throw new InvalidOperationException("Sharing already prepared.");
            long revision=host.CredentialRevision;if((revision&1L)!=0)throw new InvalidOperationException("Credentials are changing.");
            var caps=await wire.SetSharingWithCaptureLeaseAsync(sessionId,authorization).ConfigureAwait(false);
            if(revision!=host.CredentialRevision)throw new InvalidOperationException("Credentials changed during secure preparation.");
            generation=caps.Generation;if(!caps.Stateful||caps.MediaOptimizationVersion!=1||!caps.CaptureLeased)throw new InvalidOperationException("Secure sharing requires stateful media v1 with capture-v1.");
            preparedCaps=caps;preparedCredentialRevision=host.CredentialRevision;preparedAuthorization=authorization;return caps;
        }
        internal void AttachSecure(SecureSessionAdapter value){
            if(running||value==null||!value.Ready||value.Descriptor.SharingGeneration!=generation)throw new InvalidOperationException("Secure sharing is not confirmed for this generation.");
            if(preparedCaps!=null&&preparedCaps.CaptureLeased&&(!value.Descriptor.IsStrictCaptureV1||value.Descriptor.SessionId!=sessionId||value.Descriptor.TargetDeviceId!=host.DeviceId))throw new InvalidOperationException("Secure descriptor does not match the prepared capture-v1 session.");
            wire.Secure=value;diagCorrelation=value.DiagCorrelation;}
        // Local diagnostic correlation (random, shared with the attached secure adapter); never derived from session/device ids.
        private long diagCorrelation; internal long DiagCorrelation { get { return diagCorrelation; } }
        private readonly System.Diagnostics.Stopwatch diagMetricSample=new System.Diagnostics.Stopwatch(); private int diagExpiriesSinceSample; private long diagLastExpiryLogMs=-1;
        private int minFrameIntervalMs; private string firstWorkerError,firstWorkerEndReason;
        private readonly object workerErrorGate=new object();
        private Exception firstCandidateError,firstIndependentFatalError; private string firstTypedNormalReason;
        private volatile bool running;
        private readonly AdaptiveQualityPolicy qualityPolicy=new AdaptiveQualityPolicy();
        internal QualityMode QualityMode { get { return qualityPolicy.Mode; } set { qualityPolicy.SetMode(value); } }
        internal QualitySnapshot CurrentQuality { get { return qualityPolicy.Snapshot(); } }
        internal double LastCaptureEncodeMs {get;private set;}
        internal double LastRawCaptureMs {get;private set;}
        internal double LastEncodeOnlyMs {get;private set;}
        internal double CaptureStartProofAgeMs {get;private set;}
        internal double CaptureEndProofAgeMs {get;private set;}
        internal int LastInputLeaseResponseMs {get;private set;}
        private double LatestFrameProofAge(){lock(frameAuthorizationGate){return recentFrameAuthorization==null?-1:recentFrameAuthorization.RequestStart.Elapsed.TotalMilliseconds;}}
        private int FrameCadence { get { return Math.Max(minFrameIntervalMs,qualityPolicy.Snapshot().IntervalMs); } }
        private void ObserveFrame(double encodeMs,System.Diagnostics.Stopwatch upload,FramePacket frame) {
            qualityPolicy.Observe(Math.Min(60000,encodeMs),Math.Min(60000,upload.Elapsed.TotalMilliseconds),frame.Jpeg.Length,0);
        }
        // Target opt-in for stateful input (statefulInputVersion 1). Old servers ignore it and the session stays legacy taps.
        internal bool RequestStatefulInput { get; set; }
        private bool allowRemoteInput=true;
        internal bool AllowRemoteInput {get{return allowRemoteInput;}set{if(running)throw new InvalidOperationException("Change input permission only before sharing starts.");allowRemoteInput=value;}}
        internal void FailSecurePreparation(Exception error){LastError=error.Message;}
        private InputSafety guard; private volatile bool stateful; private bool optimizedMedia,captureLeased;
        // Immutable per-session identities for the capture proof binding (client and scope are readonly fields).
        private readonly string clientInstanceId=Guid.NewGuid().ToString("N"),scopeInstanceId=Guid.NewGuid().ToString("N");
        // Frozen close-probe context: a private snapshot of the live binding/client/root taken once at StartAsync, never
        // renewed for this session. Used only by ProbeFrozenEndReasonAsync; null means no frozen probe is possible.
        private CaptureProofBinding frozenCloseBinding; private ControlPlaneClient frozenCloseClient; private CancellationTokenSource frozenCloseRoot;
        internal bool CaptureLeaseActive { get { return captureLeased; } }
        internal bool StatefulInputActive { get { return stateful; } }
        internal bool IsRunning { get { return running; } }
        internal string LastError { get; private set; }
        internal string EndReason { get; private set; }
        private static bool IsNormalEnd(string reason) { return reason=="user-stopped"||reason=="session-ended"; }
        internal MediaSharingSession(ControlPlaneClient client,string id,CaptureScope captureScope) { host=client;sessionId=id;scope=captureScope;wire=new MediaWire(client); }
        internal string SessionId { get { return sessionId; } }
        // Explicit local cancel intent for the current cts; workers may treat OCE/ODE as clean only after it and cts.Cancel.
        private volatile CancellationTokenSource localCancelSource;
        internal void CancelLocal(string confirmedEndReason=null) {
            var source=cts; localCancelSource=source;
            if(EndReason==null&&(confirmedEndReason=="session-ended"||confirmedEndReason=="expired"))EndReason=confirmedEndReason;
            DiagnosticLog.Event(DiagEvent.SharingCancel,diagCorrelation,DiagValue.Of(DiagKey.Generation,generation),DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(confirmedEndReason)),DiagValue.Of(DiagKey.Value,source!=null),DiagValue.Of(DiagKey.HadError,LastError!=null));
            InvalidateFrameAuthorization();
            if(source!=null)try{source.Cancel();}catch(ObjectDisposedException){}
            if(wire.Secure!=null)wire.Secure.Dispose();
            DisposeGuard();
        }
        internal async Task StartAsync(CancellationToken authorization = default(CancellationToken))
        {
            authorization.ThrowIfCancellationRequested();
            if(IsRunning)throw new InvalidOperationException("sharing already running");
            if(!scope.IsCurrent())throw new InvalidOperationException("capture scope is not live");
            cts=CancellationTokenSource.CreateLinkedTokenSource(authorization); localCancelSource=null;
            if(diagCorrelation==0)diagCorrelation=DiagnosticLog.NewCorrelation(); DiagStartStage diagStage=DiagStartStage.Capabilities;
            try {
                SharingCapabilities caps;bool securePrepared=preparedCaps!=null;
                if(preparedCaps!=null){if(preparedCredentialRevision!=host.CredentialRevision||!preparedAuthorization.Equals(authorization)||wire.Secure==null||!wire.Secure.Ready)throw new InvalidOperationException("Prepared secure authority changed.");caps=preparedCaps;preparedCaps=null;}
                else caps=await wire.SetSharingWithCapabilitiesAsync(sessionId,true,RequestStatefulInput,cts.Token).ConfigureAwait(false);
                generation=caps.Generation; diagStage=DiagStartStage.Validate;
                qualityPolicy.SetMode(qualityPolicy.Mode); // a new sharing epoch never reuses stale quality samples
                cts.Token.ThrowIfCancellationRequested();
                if(generation<=0)throw new InvalidOperationException("invalid sharing generation");
                stateful=RequestStatefulInput&&caps.Stateful;
                optimizedMedia=stateful&&caps.MediaOptimizationVersion==1;
                captureLeased=securePrepared&&optimizedMedia&&caps.CaptureLeased;
                if(securePrepared&&!captureLeased)throw new InvalidOperationException("Secure sharing requires capture-v1 stateful input.");
                lock(frameAuthorizationGate){frameAuthorizationEpoch++;recentFrameAuthorization=null;}
                diagStage=DiagStartStage.FrozenProbe;
                if(captureLeased) {
                    long beforeRevision=host.CredentialRevision;
                    CaptureProofBinding binding=LiveCaptureBinding();
                    string frozenToken=host.Token,frozenTemp=host.TemporaryPassword,frozenExpiry=host.PasswordExpiresAt;
                    long afterRevision=host.CredentialRevision;
                    if(beforeRevision!=afterRevision||(afterRevision&1L)!=0||afterRevision!=binding.Revision)
                        throw new InvalidOperationException("credentials changed while freezing close probe binding");
                    string frozenProfileJson=new JavaScriptSerializer{MaxJsonLength=16384}.Serialize(new {
                        Version=1,Name="probe",RendezvousUrl=binding.ServerUrl,RelayUrl=binding.ServerUrl,CertificateSha256=binding.PinFingerprint
                    });
                    var frozenClient=new ControlPlaneClient(ServerProfile.Parse(frozenProfileJson));
                    frozenClient.RestoreCredentials(binding.DeviceId,frozenToken,frozenTemp,frozenExpiry);
                    frozenCloseBinding=binding;frozenCloseClient=frozenClient;frozenCloseRoot=cts;
                } else { frozenCloseBinding=null;frozenCloseClient=null;frozenCloseRoot=null; }
                diagStage=DiagStartStage.Guard;
                if(stateful) { var g=new InputSafety(()=>scope.IsCurrent()&&(scope.IsDesktop||scope.ForegroundMatches())); g.DiagCorrelation=diagCorrelation; g.Start(); guard=g; } // second owner fails here
                running=true; LastError=null; EndReason=null; staleLeaseMisses=0; staleInputMisses=0; firstWorkerError=null; firstWorkerEndReason=null;
                firstCandidateError=null; firstIndependentFatalError=null; firstTypedNormalReason=null;
                diagStage=DiagStartStage.Runner; diagMetricSample.Reset(); diagExpiriesSinceSample=0; diagLastExpiryLogMs=-1;
                DiagnosticLog.Event(DiagEvent.SharingStart,diagCorrelation,DiagValue.Of(DiagKey.Generation,generation),DiagValue.Of(DiagKey.Negotiated,caps.Negotiated),
                    DiagValue.Of(DiagKey.Stateful,stateful),DiagValue.Of(DiagKey.Optimized,optimizedMedia),DiagValue.Of(DiagKey.CaptureLeased,captureLeased),DiagValue.Of(DiagKey.Secure,securePrepared));
                if(caps.Negotiated) {
                    minFrameIntervalMs=Math.Max(optimizedMedia?MediaWire.OptimizedFrameIntervalMs:MediaWire.MinFrameIntervalFloorMs,caps.MinFrameIntervalMs);
                    // Created here (not inside the runner) so Dispose racing the runner never links to a disposed source.
                    var workers=CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    runner=Task.Run(()=>RunWorkers(workers));
                } else runner=Task.Run(()=>Loop(cts.Token));
            } catch(Exception ex) {
                DiagFailure(DiagEvent.SharingStartFailure,ex,DiagValue.Of(DiagKey.Stage,diagStage));
                DisposeGuard();
                try { await wire.SetSharingAsync(sessionId,false,CancellationToken.None).ConfigureAwait(false); } catch { }
                running=false; throw;
            }
        }
        // Lease policy: generation/scope/cancellation/HTTP(401/403/409)/TLS failures are fatal at once and never restart sharing.
        // A lease that is merely stale by elapsed time (request start -> now >= leaseMs) only drops that frame/input, backs off
        // briefly, and becomes fatal after MaxStaleLeaseMisses in a row. Only a fresh, authorized PUT/apply resets the counter.
        private const int MaxStaleLeaseMisses=3, StaleLeaseBackoffMs=50;
        private int staleLeaseMisses,staleInputMisses;
        private async Task StaleLeaseMissAsync(string stage,CancellationToken ct)
        {
            int misses=stage=="before input"?++staleInputMisses:++staleLeaseMisses;
            if(misses>=MaxStaleLeaseMisses)throw new TimeoutException("lease expired "+misses+" consecutive times ("+stage+"); sharing stopped");
            await Task.Delay(StaleLeaseBackoffMs,ct).ConfigureAwait(false);
        }
        private async Task<MediaLease> FreshLeaseAsync(System.Diagnostics.Stopwatch requestStart,CancellationToken ct)
        {
            MediaLease lease=await wire.GetLeaseAsync(sessionId,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(lease.Generation!=generation)throw new InvalidOperationException("sharing generation changed");
            return requestStart.ElapsedMilliseconds<lease.LeaseMs?lease:null; // null = purely stale by elapsed time
        }
        private async Task Loop(CancellationToken ct)
        {
            try {
                while(!ct.IsCancellationRequested) {
                    var leaseStart=System.Diagnostics.Stopwatch.StartNew();
                    MediaLease lease=await FreshLeaseAsync(leaseStart,ct).ConfigureAwait(false);
                    if(lease==null) { await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false); continue; }
                    if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                    ct.ThrowIfCancellationRequested();
                    if(leaseStart.ElapsedMilliseconds>=lease.LeaseMs){await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false);continue;}
                    var selectedQuality=qualityPolicy.Snapshot();
                    var encodeWatch=System.Diagnostics.Stopwatch.StartNew();
                    using(var bitmap=scope.Capture(selectedQuality.MaxWidth,selectedQuality.MaxHeight)) {
                        FramePacket frame=EncodeBounded(bitmap,generation,++sequence,selectedQuality.JpegQuality,wire.FrameByteBudget);
                        double encodeMs=encodeWatch.Elapsed.TotalMilliseconds;
                        ct.ThrowIfCancellationRequested();
                        if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                        // Capture+encode may outlive the first lease: re-authorize the already-encoded frame with a second fresh lease.
                        var putStart=System.Diagnostics.Stopwatch.StartNew();
                        MediaLease putLease=await FreshLeaseAsync(putStart,ct).ConfigureAwait(false);
                        if(putLease==null) { await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false); continue; } // frame dropped
                        if(frame.Generation!=generation||putLease.Generation!=frame.Generation||!scope.IsCurrent())throw new InvalidOperationException("frame generation or capture scope changed");
                        ct.ThrowIfCancellationRequested();
                        if(putStart.ElapsedMilliseconds>=putLease.LeaseMs){await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false);continue;}
                        var uploadWatch=System.Diagnostics.Stopwatch.StartNew();
                        await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                        ObserveFrame(encodeMs,uploadWatch,frame);
                        staleLeaseMisses=0;
                    }
                    var events=await wire.PollInputAsync(sessionId,ct).ConfigureAwait(false);
                    foreach(var e in events) {
                        if(e.Generation!=generation)continue;
                        var auth=System.Diagnostics.Stopwatch.StartNew();
                        MediaLease applyLease=await FreshLeaseAsync(auth,ct).ConfigureAwait(false);
                        if(applyLease==null) { await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false); continue; } // input dropped
                        if(!scope.IsDesktop) { if(!NativeInput.ForegroundMatches(scope))continue; }
                        if(!allowRemoteInput)continue;
                        if(ApplyInputCore(scope,e,auth,applyLease.LeaseMs,ct,diagCorrelation))staleInputMisses=0;
                        else if(auth.ElapsedMilliseconds>=applyLease.LeaseMs){await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);}
                    }
                    await Task.Delay(200,ct).ConfigureAwait(false);
                }
            } catch(OperationCanceledException) { DiagnosticLog.Event(DiagEvent.SharingLegacyLoopEnd,diagCorrelation,DiagValue.Of(DiagKey.Worker,DiagWorker.LegacyLoop),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.Canceled)); }
            catch(Exception ex) { DiagnosticLog.Failure(DiagEvent.SharingLegacyLoopEnd,diagCorrelation,ex,DiagValue.Of(DiagKey.Worker,DiagWorker.LegacyLoop),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.Error)); EndReason=(ex as MediaHttpException)?.EndReason; LastError=IsNormalEnd(EndReason)?null:ex.Message; if(!IsNormalEnd(EndReason))try { if(generation>0) await wire.SetSharingAsync(sessionId,false,CancellationToken.None).ConfigureAwait(false); } catch { } }
            finally { running=false; }
        }
        // Negotiated mode: one frame worker and one input worker share a linked CTS. The first fatal error is recorded and
        // cancels the other worker; both are awaited before running=false and a single sharing=false cleanup. No restart.
        private async Task RunWorkers(CancellationTokenSource workers)
        {
            try {
                Task frames=Worker(FrameWorker,workers), input=Worker(InputWorker,workers);
                try { await Task.WhenAll(frames,input).ConfigureAwait(false); } catch { }
                string error=Volatile.Read(ref firstWorkerError);
                DisposeGuard(); // release held input before telling the server sharing is off
                if(error!=null)await SettleWorkerErrorAsync(error).ConfigureAwait(false);
            } finally {
                DiagnosticLog.Event(DiagEvent.SharingCleanup,diagCorrelation,DiagValue.Of(DiagKey.Stage,DiagCleanupStage.WorkersDone),DiagValue.Of(DiagKey.HadError,Volatile.Read(ref firstWorkerError)!=null),DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(EndReason)));
                InvalidateFrameAuthorization(); DisposeGuard(); running=false; workers.Dispose();
            }
        }
        // Diagnostics only; never throws, so the caller's original cleanup and rethrow run unchanged.
        private void DiagFailure(DiagEvent ev,Exception ex,params DiagValue[] fields) { try { DiagnosticLog.Failure(ev,diagCorrelation,ex,fields); } catch { } }
        // A peer close can surface first as untyped secure/opaque fallout (e.g. opaque HTTP 410) that beats the typed 409 on
        // the HTTP lease poll. Only a server-typed reason classifies the stop: the worker's own typed 409, a normal end already
        // confirmed through CancelLocal, or (secure capture-v1 only) one capture-lease probe. Anything untyped stays an error.
        private async Task SettleWorkerErrorAsync(string error)
        {
            Exception fatal, candidate; string typedNormal;
            lock(workerErrorGate) { fatal=firstIndependentFatalError; candidate=firstCandidateError; typedNormal=firstTypedNormalReason; }
            string normal=null; bool probed=false;
            if(fatal==null) {
                if(candidate!=null) {
                    if(captureLeased&&wire.Secure!=null)
                    { probed=true; normal=await ProbeFrozenEndReasonAsync().ConfigureAwait(false); }
                } else if(IsNormalEnd(typedNormal)) normal=typedNormal;
            }
            DiagSettle settle=fatal!=null?DiagSettle.IndependentFatal:candidate!=null?(probed?(IsNormalEnd(normal)?DiagSettle.CandidateProbeNormal:DiagSettle.CandidateProbeUnresolved):DiagSettle.CandidateNoProbe):IsNormalEnd(typedNormal)?DiagSettle.TypedNormal:DiagSettle.Untyped;
            if(fatal==null&&IsNormalEnd(normal)) {
                EndReason=normal; LastError=null;
                DiagnosticLog.Event(DiagEvent.SharingNormalEnd,diagCorrelation,DiagValue.Of(DiagKey.Reason,settle),DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(normal)));
                return;
            }
            DiagnosticLog.Failure(DiagEvent.SharingFatal,diagCorrelation,fatal??candidate,DiagValue.Of(DiagKey.Reason,settle),DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(fatal is MediaHttpException?((MediaHttpException)fatal).EndReason:null)));
            var fatalMedia=fatal as MediaHttpException;
            EndReason=fatalMedia==null?null:fatalMedia.EndReason;
            LastError=fatal!=null?fatal.Message:candidate!=null?candidate.Message:error;
            try { if(generation>0) await wire.SetSharingAsync(sessionId,false,CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        private async Task Worker(Func<CancellationToken,Task> body,CancellationTokenSource workers)
        {
            CancellationToken ct=workers.Token; CancellationTokenSource root=cts;
            // Diagnostics only: the worker kind is derived from the body so the existing signature stays unchanged.
            string diagBody=body.Method.Name; DiagWorker kind=diagBody=="FrameWorker"?DiagWorker.Frame:diagBody=="InputWorker"?DiagWorker.Input:DiagWorker.Unknown;
            try { await body(ct).ConfigureAwait(false); DiagnosticLog.Event(DiagEvent.SharingWorkerEnd,diagCorrelation,DiagValue.Of(DiagKey.Worker,kind),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.Completed)); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { DiagnosticLog.Event(DiagEvent.SharingWorkerEnd,diagCorrelation,DiagValue.Of(DiagKey.Worker,kind),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.Canceled)); }
            // Disposal fallout (secure channel/guard) is clean only after this session's recorded local cancel and its cts.Cancel.
            catch(ObjectDisposedException) when(root!=null&&ReferenceEquals(localCancelSource,root)&&root.IsCancellationRequested&&ct.IsCancellationRequested) { DiagnosticLog.Event(DiagEvent.SharingWorkerEnd,diagCorrelation,DiagValue.Of(DiagKey.Worker,kind),DiagValue.Of(DiagKey.Outcome,DiagWorkerOutcome.DisposedAfterLocalCancel)); }
            catch(Exception ex) {
                // An OperationCanceledException that our token did not request (e.g. HTTP timeout) is fatal, not a clean stop.
                bool diagFirst=Interlocked.CompareExchange(ref firstWorkerError,ex.Message,null)==null;
                if(diagFirst)
                    firstWorkerEndReason=(ex as MediaHttpException)?.EndReason;
                DiagWorkerOutcome diagOutcome; bool diagClassFirst;
                lock(workerErrorGate) {
                    var opaque=ex as OpaquePacketHttpException; var media=ex as MediaHttpException;
                    if(opaque!=null&&(opaque.StatusCode==404||opaque.StatusCode==409)) {
                        diagOutcome=DiagWorkerOutcome.Candidate; diagClassFirst=firstCandidateError==null;
                        if(firstCandidateError==null)firstCandidateError=ex;
                    } else if(media!=null&&media.StatusCode==409&&IsNormalEnd(media.EndReason)) {
                        diagOutcome=DiagWorkerOutcome.TypedNormal; diagClassFirst=firstTypedNormalReason==null;
                        if(firstTypedNormalReason==null)firstTypedNormalReason=media.EndReason;
                    } else {
                        diagOutcome=DiagWorkerOutcome.Fatal; diagClassFirst=firstIndependentFatalError==null;
                        if(firstIndependentFatalError==null)firstIndependentFatalError=ex;
                    }
                }
                // Recorded before cancelling the sibling worker so the first observed worker/kind is never hidden by fallout.
                if(diagFirst||diagClassFirst)DiagnosticLog.Failure(diagFirst?DiagEvent.SharingWorkerFirstError:DiagEvent.SharingWorkerClassFirst,diagCorrelation,ex,
                    DiagValue.Of(DiagKey.Worker,kind),DiagValue.Of(DiagKey.Outcome,diagOutcome),DiagValue.Of(DiagKey.AlreadyCanceled,ct.IsCancellationRequested));
                try { workers.Cancel(); } catch(ObjectDisposedException) { }
            }
        }

        // Per-session immutable proof. The stopwatch is the original input-lease REQUEST START and is never restarted.
        private sealed class FrameAuthorization {
            internal readonly MediaSharingSession Owner; internal readonly ControlPlaneClient Client;
            internal readonly string Sid,DeviceId,Url,Pin,Credential; internal readonly long Epoch,Generation,CredentialRevision;
            internal readonly int LeaseMs; internal readonly System.Diagnostics.Stopwatch RequestStart;
            internal FrameAuthorization(MediaSharingSession owner,long epoch,long gen,int lease,System.Diagnostics.Stopwatch start,long credentialRevision){
                Owner=owner;Client=owner.host;Sid=owner.sessionId;DeviceId=Client.DeviceId;Credential=Client.Token;Url=Client.ServerUrl;Pin=Client.CertificateSha256;
                Epoch=epoch;Generation=gen;CredentialRevision=credentialRevision;LeaseMs=lease;RequestStart=start;
            }
        }
        private readonly object frameAuthorizationGate=new object();
        private long frameAuthorizationEpoch; private FrameAuthorization recentFrameAuthorization;
        private void InvalidateFrameAuthorization(){lock(frameAuthorizationGate){frameAuthorizationEpoch++;recentFrameAuthorization=null;}}
        private void PublishFrameAuthorization(long epoch,long gen,int lease,System.Diagnostics.Stopwatch start,CancellationToken ct,long credentialRevision){
            if(!optimizedMedia)return;
            lock(frameAuthorizationGate){
                if(ct.IsCancellationRequested||epoch!=frameAuthorizationEpoch||gen!=generation||(credentialRevision&1L)!=0||credentialRevision!=host.CredentialRevision||lease<=0||lease>100||start.ElapsedMilliseconds>=lease)return;
                recentFrameAuthorization=new FrameAuthorization(this,epoch,gen,lease,start,credentialRevision);
            }
        }
        private bool FreshFrameAuthorization(CancellationToken ct){
            lock(frameAuthorizationGate){
                var a=recentFrameAuthorization;
                return !ct.IsCancellationRequested&&a!=null&&ReferenceEquals(a.Owner,this)&&ReferenceEquals(a.Client,host)
                    &&a.Epoch==frameAuthorizationEpoch&&a.Generation==generation&&a.Sid==sessionId&&a.DeviceId==host.DeviceId
                    &&(a.CredentialRevision&1L)==0&&a.CredentialRevision==host.CredentialRevision&&a.Credential==host.Token&&a.Url==host.ServerUrl&&a.Pin==host.CertificateSha256&&a.RequestStart.ElapsedMilliseconds<a.LeaseMs;
            }
        }
        private async Task WaitFrameAuthorization(CancellationToken ct){
            var wait=System.Diagnostics.Stopwatch.StartNew();
            while(!FreshFrameAuthorization(ct)){
                ct.ThrowIfCancellationRequested();
                if(wait.ElapsedMilliseconds>=1000)throw new TimeoutException("fresh server frame authorization unavailable; sharing stopped");
                await Task.Delay(10,ct).ConfigureAwait(false);
            }
        }
        private async Task OptimizedFrameWorker(CancellationToken ct){
            System.Diagnostics.Stopwatch sinceLastPutAck=null,sinceLastAttempt=null;
            int expiredEncodeDrops=0;
            while(!ct.IsCancellationRequested){
                if(sinceLastPutAck!=null){long remaining=FrameCadence-sinceLastPutAck.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);}
                if(sinceLastAttempt!=null){long remaining=FrameCadence-sinceLastAttempt.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);}
                await WaitFrameAuthorization(ct).ConfigureAwait(false);
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                ct.ThrowIfCancellationRequested();if(!FreshFrameAuthorization(ct))continue;
                sinceLastAttempt=System.Diagnostics.Stopwatch.StartNew();
                CaptureStartProofAgeMs=LatestFrameProofAge();long captureCredentialRevision=host.CredentialRevision;
                var selectedQuality=qualityPolicy.Snapshot();
                var encodeWatch=System.Diagnostics.Stopwatch.StartNew();
                using(var bitmap=scope.Capture(selectedQuality.MaxWidth,selectedQuality.MaxHeight)){
                    double rawCaptureMs=encodeWatch.Elapsed.TotalMilliseconds;LastRawCaptureMs=rawCaptureMs;
                    var frame=EncodeBounded(bitmap,generation,++sequence,selectedQuality.JpegQuality,wire.FrameByteBudget);
                    double encodeMs=encodeWatch.Elapsed.TotalMilliseconds;LastCaptureEncodeMs=encodeMs;LastEncodeOnlyMs=encodeMs-rawCaptureMs;CaptureEndProofAgeMs=LatestFrameProofAge();
                    ct.ThrowIfCancellationRequested();
                    if(!scope.IsCurrent()||frame.Generation!=generation)throw new InvalidOperationException("frame generation or capture scope changed");
                    // No extra GET fallbacks: input20/s + max frame20/s stays inside the unchanged60/s media quota.
                    // If the 100ms proof expired during encode, discard this frame rather than renew its timestamp.
                    if(!FreshFrameAuthorization(ct)){if(captureCredentialRevision!=host.CredentialRevision)throw new InvalidOperationException("credentials changed during capture");qualityPolicy.ObserveExpiredFrame();if(++expiredEncodeDrops>=3)throw new TimeoutException("frame authorization expired during3 consecutive captures; sharing stopped");continue;}
                    var uploadWatch=System.Diagnostics.Stopwatch.StartNew();
                    await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                    ObserveFrame(encodeMs,uploadWatch,frame);
                    expiredEncodeDrops=0;sinceLastPutAck=System.Diagnostics.Stopwatch.StartNew();
                }
            }
        }

        // Live context snapshot; an odd (changing) credential revision or missing credential is fatal, never retried.
        internal CaptureProofBinding LiveCaptureBinding()
        {
            long revision=host.CredentialRevision; string token=host.Token,deviceId=host.DeviceId,serverUrl=host.ServerUrl,pin=host.CertificateSha256;
            if((revision&1L)!=0||String.IsNullOrEmpty(token)||String.IsNullOrEmpty(deviceId)||revision!=host.CredentialRevision)throw new InvalidOperationException("credentials changing during capture");
            long epoch;lock(frameAuthorizationGate)epoch=frameAuthorizationEpoch;
            return new CaptureProofBinding(sessionId,clientInstanceId,deviceId,TokenFingerprint(token),revision,serverUrl,pin,epoch,generation,scopeInstanceId);
        }
        private static string TokenFingerprint(string token){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).Replace("-","");}
        // Context/auth/cancel/scope failures are fatal; only pure proof expiry is reported as CaptureProofExpiredException.
        internal void CheckCaptureProof(CaptureProof proof,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!running||!captureLeased)throw new InvalidOperationException("capture-v1 sharing is not active");
            if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
            proof.Check(LiveCaptureBinding());
        }
        internal const int MaxCaptureProofExpiryDrops=3;
        private int captureProofExpiryDrops;
        // Pure pre-seal expiry: drop this frame; the third consecutive drop is fatal. Only a successful send resets.
        internal void ObserveCaptureProofExpiry()
        {
            qualityPolicy.ObserveExpiredFrame();
            if(++captureProofExpiryDrops>=MaxCaptureProofExpiryDrops)throw new TimeoutException("capture proof expired before seal "+captureProofExpiryDrops+" consecutive times; sharing stopped");
        }
        // Diagnostic wrapper for the distinct pure-expiry sites; rate-limited (first per 5s, then every consecutive repeat).
        private void ObserveCaptureProofExpiry(DiagExpiryStage stage,CaptureProof proof,FrameDiag fd)
        {
            diagExpiriesSinceSample++;
            long nowMs=System.Diagnostics.Stopwatch.GetTimestamp()*1000/System.Diagnostics.Stopwatch.Frequency;
            if(captureProofExpiryDrops>=1||diagLastExpiryLogMs<0||nowMs-diagLastExpiryLogMs>=5000) {
                diagLastExpiryLogMs=nowMs;
                DiagnosticLog.Event(DiagEvent.CaptureProofExpired,diagCorrelation,DiagValue.Of(DiagKey.Stage,stage),DiagValue.Of(DiagKey.Count,(long)(captureProofExpiryDrops+1)),
                    DiagValue.Of(DiagKey.ProofAgeMs,(long)ProofAgeMs(proof)),DiagValue.Of(DiagKey.ResponseMs,fd.LeaseMs),DiagValue.Of(DiagKey.CaptureMs,fd.CaptureMs),DiagValue.Of(DiagKey.EncodeMs,fd.EncodeMs),DiagValue.Of(DiagKey.IntervalMs,(long)FrameCadence));
            }
            ObserveCaptureProofExpiry();
        }
        // Per-frame diagnostic snapshot; reset at the start of every attempt so no value can leak from a prior frame.
        private sealed class FrameDiag {
            internal DiagFramePhase Phase; internal long LeaseMs,StartProofAgeMs,EndProofAgeMs,CaptureMs,EncodeMs,UploadMs;
            internal void Reset(){Phase=DiagFramePhase.None;LeaseMs=-1;StartProofAgeMs=-1;EndProofAgeMs=-1;CaptureMs=-1;EncodeMs=-1;UploadMs=-1;}
            internal DiagValue[] Fields(bool valid){return new[]{DiagValue.Of(DiagKey.FrameValid,valid),DiagValue.Of(DiagKey.Phase,Phase),DiagValue.Of(DiagKey.ResponseMs,LeaseMs),
                DiagValue.Of(DiagKey.StartProofAgeMs,StartProofAgeMs),DiagValue.Of(DiagKey.EndProofAgeMs,EndProofAgeMs),DiagValue.Of(DiagKey.CaptureMs,CaptureMs),DiagValue.Of(DiagKey.EncodeMs,EncodeMs),DiagValue.Of(DiagKey.UploadMs,UploadMs)};}
        }
        // Diagnostics only; never throws. The caller rethrows the original exception unchanged.
        private void DiagCaptureFailure(Exception ex,FrameDiag fd,CancellationToken ct)
        {
            try {
                if(ex is OperationCanceledException&&ct.IsCancellationRequested)return;
                if(!(ex is CaptureProofExpiredException))DiagnosticLog.Failure(DiagEvent.CaptureFailure,diagCorrelation,ex,fd.Fields(false));
            } catch { }
        }
        // capture-v1 (secure): one dedicated proof per frame, requested fresh (250ms from request start) and held by exactly
        // one bitmap. The proof is never renewed or re-requested for an already captured/encoded bitmap.
        private async Task CaptureLeaseFrameWorker(CancellationToken ct)
        {
            System.Diagnostics.Stopwatch sinceLastAttempt=null; captureProofExpiryDrops=0; var fd=new FrameDiag();
            while(!ct.IsCancellationRequested){
                fd.Reset();
                try {
                if(sinceLastAttempt!=null){long remaining=FrameCadence-sinceLastAttempt.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);}
                sinceLastAttempt=System.Diagnostics.Stopwatch.StartNew();
                ct.ThrowIfCancellationRequested();
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                fd.Phase=DiagFramePhase.LeaseRequest;
                CaptureProofBinding before=LiveCaptureBinding();
                CaptureProof proof=await wire.GetCaptureLeaseAsync(sessionId,before,ct).ConfigureAwait(false);
                fd.LeaseMs=sinceLastAttempt.ElapsedMilliseconds; fd.Phase=DiagFramePhase.ProofBeforeCapture;
                try{CheckCaptureProof(proof,ct);}catch(CaptureProofExpiredException){CaptureStartProofAgeMs=-1;CaptureEndProofAgeMs=-1;ObserveCaptureProofExpiry(DiagExpiryStage.AfterLease,proof,fd);continue;}
                CaptureStartProofAgeMs=ProofAgeMs(proof); fd.StartProofAgeMs=(long)CaptureStartProofAgeMs; fd.Phase=DiagFramePhase.Capture;
                var selectedQuality=qualityPolicy.Snapshot();
                var encodeWatch=System.Diagnostics.Stopwatch.StartNew();
                using(var bitmap=scope.Capture(selectedQuality.MaxWidth,selectedQuality.MaxHeight)){
                    double rawCaptureMs=encodeWatch.Elapsed.TotalMilliseconds;LastRawCaptureMs=rawCaptureMs; fd.CaptureMs=(long)rawCaptureMs; fd.Phase=DiagFramePhase.ProofAfterCapture;
                    // Metrics only: this frame never reached the end-of-encode measurement, so its end proof age is invalid (-1).
                    try{CheckCaptureProof(proof,ct);}catch(CaptureProofExpiredException){CaptureEndProofAgeMs=-1;ObserveCaptureProofExpiry(DiagExpiryStage.AfterCapture,proof,fd);continue;}
                    fd.Phase=DiagFramePhase.Encode;
                    var frame=EncodeBounded(bitmap,generation,++sequence,selectedQuality.JpegQuality,wire.FrameByteBudget);
                    double encodeMs=encodeWatch.Elapsed.TotalMilliseconds;LastCaptureEncodeMs=encodeMs;LastEncodeOnlyMs=encodeMs-rawCaptureMs;CaptureEndProofAgeMs=ProofAgeMs(proof);
                    fd.EncodeMs=(long)(encodeMs-rawCaptureMs); fd.EndProofAgeMs=(long)CaptureEndProofAgeMs; fd.Phase=DiagFramePhase.ProofAfterEncode;
                    if(frame.Generation!=proof.Binding.Generation)throw new InvalidOperationException("frame generation changed");
                    try{CheckCaptureProof(proof,ct);}catch(CaptureProofExpiredException){ObserveCaptureProofExpiry(DiagExpiryStage.AfterEncode,proof,fd);continue;}
                    var uploadWatch=System.Diagnostics.Stopwatch.StartNew(); fd.Phase=DiagFramePhase.Upload;
                    // B2 contract: the adapter invokes the guard pre-seal and before the actual write. Only a pre-seal pure expiry
                    // surfaces as CaptureProofExpiredException; any post-seal failure is surfaced as a different (terminal) error.
                    try{await wire.PutFrameAsync(sessionId,frame,ct,()=>CheckCaptureProof(proof,ct)).ConfigureAwait(false);}
                    catch(CaptureProofExpiredException){ObserveCaptureProofExpiry(DiagExpiryStage.PreSealSend,proof,fd);continue;}
                    ObserveFrame(encodeMs,uploadWatch,frame);
                    captureProofExpiryDrops=0;
                    fd.UploadMs=uploadWatch.ElapsedMilliseconds; fd.Phase=DiagFramePhase.Done;
                    if(!diagMetricSample.IsRunning||diagMetricSample.ElapsedMilliseconds>=5000) {
                        diagMetricSample.Restart(); var q=qualityPolicy.Snapshot(); var fields=new List<DiagValue>(fd.Fields(true));
                        fields.Add(DiagValue.Of(DiagKey.Bytes,(long)frame.Jpeg.Length));fields.Add(DiagValue.Of(DiagKey.Width,(long)frame.Width));fields.Add(DiagValue.Of(DiagKey.Height,(long)frame.Height));
                        fields.Add(DiagValue.Of(DiagKey.Quality,(long)q.JpegQuality));fields.Add(DiagValue.Of(DiagKey.IntervalMs,(long)FrameCadence));fields.Add(DiagValue.Of(DiagKey.Expiries,(long)diagExpiriesSinceSample));
                        diagExpiriesSinceSample=0; DiagnosticLog.Metric(DiagEvent.CaptureMetrics,diagCorrelation,fields.ToArray());
                    }
                }
                } catch(Exception ex) { DiagCaptureFailure(ex,fd,ct); throw; }
            }
        }
        private static double ProofAgeMs(CaptureProof proof){return (System.Diagnostics.Stopwatch.GetTimestamp()-proof.RequestedAt)*1000.0/System.Diagnostics.Stopwatch.Frequency;}

        // Frozen close-probe (StageA only): side-effect-free, at most one GET, no authority publish/mutation/restart.
        // Returns a server-typed normal-end reason only if the frozen binding still matches the live binding before
        // and after the single request; everything else (mismatch, non-409, untyped, timeout, transport error) is null.
        private async Task<string> ProbeFrozenEndReasonAsync()
        {
            CaptureProofBinding binding=frozenCloseBinding; ControlPlaneClient client=frozenCloseClient; CancellationTokenSource root=frozenCloseRoot;
            if(binding==null||client==null||root==null||!ReferenceEquals(root,cts)||root.IsCancellationRequested)return null;
            CaptureProofBinding live;
            try { live=LiveCaptureBinding(); } catch { return null; }
            if(!binding.SameAs(live))return null;
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(root.Token)) {
                linked.CancelAfter(1000);
                using(var timer=new System.Threading.Timer(_=>{
                    if(root.IsCancellationRequested){try{linked.Cancel();}catch(ObjectDisposedException){}return;}
                    CaptureProofBinding now;
                    try { now=LiveCaptureBinding(); } catch { try{linked.Cancel();}catch(ObjectDisposedException){} return; }
                    if(!binding.SameAs(now)){try{linked.Cancel();}catch(ObjectDisposedException){}}
                },null,50,50)) {
                    try {
                        await client.SendJsonAsync("GET","/api/v1/sessions/"+Uri.EscapeDataString(sessionId)+"/capture-lease",null,linked.Token).ConfigureAwait(false);
                    } catch(MediaHttpException ex) when(ex.StatusCode==409&&IsNormalEnd(ex.EndReason)) {
                        if(linked.IsCancellationRequested||!ReferenceEquals(root,cts)||root.IsCancellationRequested)return null;
                        CaptureProofBinding after;
                        try { after=LiveCaptureBinding(); } catch { return null; }
                        if(!binding.SameAs(after))return null;
                        return ex.EndReason;
                    } catch { }
                }
            }
            return null;
        }

        private async Task FrameWorker(CancellationToken ct)
        {
            if(captureLeased){await CaptureLeaseFrameWorker(ct).ConfigureAwait(false);return;}
            if(optimizedMedia){await OptimizedFrameWorker(ct).ConfigureAwait(false);return;}
            System.Diagnostics.Stopwatch sinceLastPutAck=null;
            while(!ct.IsCancellationRequested) {
                // Wait before authorizing/capturing, not while holding an already old screenshot.
                if(sinceLastPutAck!=null) {
                    long remaining=FrameCadence-sinceLastPutAck.ElapsedMilliseconds;
                    if(remaining>0) await Task.Delay((int)remaining,ct).ConfigureAwait(false);
                }
                var leaseStart=System.Diagnostics.Stopwatch.StartNew();
                MediaLease lease=await FreshLeaseAsync(leaseStart,ct).ConfigureAwait(false);
                if(lease==null) { await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false); continue; }
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                ct.ThrowIfCancellationRequested();
                if(leaseStart.ElapsedMilliseconds>=lease.LeaseMs){await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false);continue;}
                var selectedQuality=qualityPolicy.Snapshot();
                var encodeWatch=System.Diagnostics.Stopwatch.StartNew();
                using(var bitmap=scope.Capture(selectedQuality.MaxWidth,selectedQuality.MaxHeight)) {
                    FramePacket frame=EncodeBounded(bitmap,generation,++sequence,selectedQuality.JpegQuality,wire.FrameByteBudget);
                    double encodeMs=encodeWatch.Elapsed.TotalMilliseconds;LastCaptureEncodeMs=encodeMs;CaptureEndProofAgeMs=LatestFrameProofAge();
                    ct.ThrowIfCancellationRequested();
                    if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                    // A separate fresh upload lease is still required after capture/encode.
                    var putStart=System.Diagnostics.Stopwatch.StartNew();
                    MediaLease putLease=await FreshLeaseAsync(putStart,ct).ConfigureAwait(false);
                    if(putLease==null) { await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false); continue; } // frame dropped
                    if(frame.Generation!=generation||putLease.Generation!=frame.Generation||!scope.IsCurrent())throw new InvalidOperationException("frame generation or capture scope changed");
                    ct.ThrowIfCancellationRequested();
                    if(putStart.ElapsedMilliseconds>=putLease.LeaseMs){await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false);continue;}
                    var uploadWatch=System.Diagnostics.Stopwatch.StartNew();
                    await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                    ObserveFrame(encodeMs,uploadWatch,frame);
                    sinceLastPutAck=System.Diagnostics.Stopwatch.StartNew();
                    staleLeaseMisses=0;
                }
            }
        }
        private const int InputPollIntervalMs=MediaWire.LegacyInputPollIntervalMs;
        private async Task InputWorker(CancellationToken ct)
        {
            if(captureLeased&&wire.Secure!=null){await SecureStatefulInputWorker(ct).ConfigureAwait(false);return;}
            if(stateful) { await StatefulInputWorker(ct).ConfigureAwait(false); return; }
            long lastDrainedSequence=0;
            while(!ct.IsCancellationRequested) {
                // The poll request start is the lease start for every event of the batch.
                var auth=System.Diagnostics.Stopwatch.StartNew();
                InputLeaseBatch batch=await wire.PollInputLeaseAsync(sessionId,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if(batch.Generation!=generation)throw new InvalidOperationException("sharing generation changed");
                if(batch.Events.Count>0) {
                    if(batch.Events[0].Sequence<=lastDrainedSequence)throw new InvalidOperationException("input batch replay");
                    lastDrainedSequence=batch.Events[batch.Events.Count-1].Sequence; // Drained once, including dropped/stale events.
                    if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                    if(auth.ElapsedMilliseconds>=batch.LeaseMs) await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false); // whole batch dropped
                    else foreach(var e in batch.Events) {
                        ct.ThrowIfCancellationRequested();
                        if(e.Generation!=generation)throw new InvalidOperationException("input generation changed");
                        if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                        if(!scope.IsDesktop) { if(!NativeInput.ForegroundMatches(scope))continue; }
                        if(!allowRemoteInput)continue;
                        if(ApplyInputCore(scope,e,auth,batch.LeaseMs,ct,diagCorrelation)) { staleInputMisses=0; continue; }
                        if(auth.ElapsedMilliseconds>=batch.LeaseMs) { await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false); break; } // rest dropped
                    }
                }
                // An empty stale lease has nothing to apply: it neither counts as a miss nor resets the counter.
                long remaining=InputPollIntervalMs-auth.ElapsedMilliseconds;
                if(remaining>0) await Task.Delay((int)remaining,ct).ConfigureAwait(false);
            }
        }
        // Stateful stream (version 1, 50ms poll). The whole batch was strictly parsed before anything is applied. Every
        // actuation still needs the 100ms request-start lease; a stale batch or a mid-batch lease expiry that carries edges
        // (or meets held input) releases everything and fails the input stream permanently: no late down can revive a hold.
        private bool InputAuthorityCurrent(CaptureProofBinding binding,long epoch,long credentialRevision,long gen,SecureSessionAdapter adapter,CancellationToken ct)
        {
            return InputAuthorityState(binding,epoch,credentialRevision,gen,adapter,ct)==DiagInputContext.Current;
        }
        // Same clauses, order and short-circuit as before; the result names the first clause that failed (evaluated once).
        private DiagInputContext InputAuthorityState(CaptureProofBinding binding,long epoch,long credentialRevision,long gen,SecureSessionAdapter adapter,CancellationToken ct)
        {
            if(ct.IsCancellationRequested)return DiagInputContext.Canceled;
            if(!running)return DiagInputContext.NotRunning;
            if(!captureLeased)return DiagInputContext.NotLeased;
            if(generation!=gen)return DiagInputContext.Generation;
            if(host.CredentialRevision!=credentialRevision)return DiagInputContext.CredentialRevision;
            if((credentialRevision&1L)!=0)return DiagInputContext.CredentialOdd;
            if(wire.Secure!=adapter)return DiagInputContext.Adapter;
            if(!scope.IsCurrent())return DiagInputContext.Scope;
            try{if(!binding.SameAs(LiveCaptureBinding()))return DiagInputContext.Binding;return binding.Epoch==epoch?DiagInputContext.Current:DiagInputContext.Epoch;}catch{return DiagInputContext.BindingThrew;}
        }
        // One concise event per fixed input-worker failure site, recorded before the original throw/fatal. Only values the
        // site actually evaluated are passed; -1 / NotEvaluated mean the site never computed them. Enqueue only, no IO.
        private void DiagInputSite(DiagInputCause cause,bool secure,InputSafety g,CancellationToken ct,long pollAgeMs=-1,int leaseMs=-1,
            DiagInputContext context=DiagInputContext.NotEvaluated,DiagTri scopeCurrent=DiagTri.NotEvaluated,DiagTri generationMatch=DiagTri.NotEvaluated,
            int index=-1,int count=-1,long proofAgeMs=-1,int pendingBudget=-1,bool bindInputs=false,Exception ex=null)
        {
            try {
                var f=new[]{DiagValue.Of(DiagKey.Cause,cause),DiagValue.Of(DiagKey.Secure,secure),DiagValue.Of(DiagKey.Generation,generation),
                    DiagValue.Of(DiagKey.PollAgeMs,pollAgeMs),DiagValue.Of(DiagKey.LeaseMs,(long)leaseMs),DiagValue.Of(DiagKey.Context,context),
                    DiagValue.Of(DiagKey.ScopeCurrent,scopeCurrent),DiagValue.Of(DiagKey.GenerationMatch,generationMatch),DiagValue.Of(DiagKey.Index,(long)index),
                    DiagValue.Of(DiagKey.Count,(long)count),DiagValue.Of(DiagKey.ProofAgeMs,proofAgeMs),DiagValue.Of(DiagKey.PendingBudgetMs,(long)pendingBudget),
                    DiagValue.Of(DiagKey.BindInputs,bindInputs),DiagValue.Of(DiagKey.HeldCount,(long)(g==null?-1:g.HeldCount)),DiagValue.Of(DiagKey.AlreadyCanceled,ct.IsCancellationRequested)};
                if(ex!=null)DiagnosticLog.Failure(DiagEvent.InputWorkerSite,diagCorrelation,ex,f); else DiagnosticLog.Event(DiagEvent.InputWorkerSite,diagCorrelation,f);
            } catch { }
        }
        // Exception filter (always false): records an apply failure on the throwing path without catching it.
        private bool DiagApplyFailed(Exception ex,bool secure,InputSafety g,CancellationToken ct,long pollAgeMs,int leaseMs,int index,int count)
        {
            if(!(ex is OperationCanceledException&&ct.IsCancellationRequested))
                DiagInputSite(DiagInputCause.ApplyFailed,secure,g,ct,pollAgeMs,leaseMs,index:index,count:count,ex:ex);
            return false;
        }
        private async Task StatefulInputWorker(CancellationToken ct)
        {
            if(captureLeased&&wire.Secure!=null){await SecureStatefulInputWorker(ct).ConfigureAwait(false);return;}
            await LegacyStatefulInputWorker(ct).ConfigureAwait(false);
        }
        private async Task LegacyStatefulInputWorker(CancellationToken ct)
        {
            long lastDrainedSequence=0; InputSafety g=guard;
            while(!ct.IsCancellationRequested) {
                if(g.Failed){DiagInputSite(DiagInputCause.GuardPreFailed,false,g,ct);throw new InvalidOperationException(g.Failure);}
                long proofEpoch;lock(frameAuthorizationGate)proofEpoch=frameAuthorizationEpoch;
                long credentialRevision=host.CredentialRevision;var auth=System.Diagnostics.Stopwatch.StartNew();
                InputLeaseBatch batch=await wire.PollInputLeaseAsync(sessionId,true,ct).ConfigureAwait(false);
                LastInputLeaseResponseMs=(int)Math.Min(10000,auth.ElapsedMilliseconds);ct.ThrowIfCancellationRequested();
                if(batch.Generation!=generation){DiagInputSite(DiagInputCause.GenerationAfterPoll,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.False);throw new InvalidOperationException("sharing generation changed");}
                if(g.Failed){DiagInputSite(DiagInputCause.GuardFailedAfterPoll,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.True);throw new InvalidOperationException(g.Failure);}
                if(!scope.IsCurrent()){DiagInputSite(DiagInputCause.ScopeAfterPoll,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,scopeCurrent:DiagTri.False,generationMatch:DiagTri.True);throw new InvalidOperationException("capture scope ended");}
                if(batch.Events.Count>0&&batch.Events[0].Sequence<=lastDrainedSequence){DiagInputSite(DiagInputCause.ReplayBatch,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,count:batch.Events.Count);throw new InvalidOperationException("input batch replay");}
                if(auth.ElapsedMilliseconds<batch.LeaseMs){g.ObserveServerLease();PublishFrameAuthorization(proofEpoch,batch.Generation,batch.LeaseMs,auth,ct,credentialRevision);}
                if(batch.Events.Count>0){lastDrainedSequence=batch.Events[batch.Events.Count-1].Sequence;
                    if(!scope.IsCurrent()){DiagInputSite(DiagInputCause.ScopeBeforeApply,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,scopeCurrent:DiagTri.False,count:batch.Events.Count);throw new InvalidOperationException("capture scope ended");}
                    long staleAge;
                    if((staleAge=auth.ElapsedMilliseconds)>=batch.LeaseMs){if(g.HeldCount>0||batch.Events.Any(e=>MediaWire.IsStatefulKind(e.Kind)&&e.Kind!="wheel")){DiagInputSite(DiagInputCause.StaleBatch,false,g,ct,staleAge,batch.LeaseMs,count:batch.Events.Count);await StatefulFatalAsync(g,"stale stateful input batch").ConfigureAwait(false);}await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);}
                    else for(int i=0;i<batch.Events.Count;i++){var e=batch.Events[i];ct.ThrowIfCancellationRequested();
                        if(e.Generation!=generation){DiagInputSite(DiagInputCause.EventGeneration,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.False,index:i,count:batch.Events.Count);throw new InvalidOperationException("input generation changed");}
                        if(!scope.IsCurrent()){DiagInputSite(DiagInputCause.EventScope,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,scopeCurrent:DiagTri.False,generationMatch:DiagTri.True,index:i,count:batch.Events.Count);throw new InvalidOperationException("capture scope ended");}
                        bool applied;try{applied=await ApplyStatefulAsync(g,e,auth,batch.LeaseMs,ct,null).ConfigureAwait(false);}catch(Exception ex)when(DiagApplyFailed(ex,false,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,i,batch.Events.Count)){throw;}
                        if(applied){staleInputMisses=0;continue;}
                        long midAge;
                        if((midAge=auth.ElapsedMilliseconds)>=batch.LeaseMs){for(int j=i;j<batch.Events.Count;j++)if(g.HeldCount>0||(MediaWire.IsStatefulKind(batch.Events[j].Kind)&&batch.Events[j].Kind!="wheel")){DiagInputSite(DiagInputCause.LeaseMidBatch,false,g,ct,midAge,batch.LeaseMs,index:j,count:batch.Events.Count);await StatefulFatalAsync(g,"stateful input lease expired mid-batch").ConfigureAwait(false);}await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);break;}}
                }
                long remaining=MediaWire.StatefulInputPollIntervalMs-auth.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);
            }
        }
        private async Task SecureStatefulInputWorker(CancellationToken ct)
        {
            long lastDrainedSequence=0; InputSafety g=guard; SecureSessionAdapter adapter=wire.Secure;
            if(adapter==null){DiagInputSite(DiagInputCause.AdapterUnavailable,true,g,ct);throw new InvalidOperationException("secure input authority unavailable");}
            bool bindInputs=stateful&&allowRemoteInput&&(adapter.Descriptor.PermissionBits&2)!=0;
            if(!bindInputs)adapter.StopAcceptingInputs();
            try {
            CaptureProofBinding binding=LiveCaptureBinding();long proofEpoch=binding.Epoch,credentialRevision=binding.Revision;
            var proofUnavailable=System.Diagnostics.Stopwatch.StartNew();
            while(!ct.IsCancellationRequested) {
                if(g!=null&&g.Failed){DiagInputSite(DiagInputCause.GuardPreFailed,true,g,ct,proofAgeMs:proofUnavailable.ElapsedMilliseconds,bindInputs:bindInputs);throw new InvalidOperationException(g.Failure);}
                DiagInputContext preContext=InputAuthorityState(binding,proofEpoch,credentialRevision,generation,adapter,ct);
                if(preContext!=DiagInputContext.Current){DiagInputSite(DiagInputCause.ContextBeforePoll,true,g,ct,context:preContext,proofAgeMs:proofUnavailable.ElapsedMilliseconds,bindInputs:bindInputs);var changed=new InvalidOperationException("secure input identity context changed");adapter.FailPendingInputs(changed);throw changed;}
                long proofRemaining=750-proofUnavailable.ElapsedMilliseconds;int pendingBudget=bindInputs?adapter.PendingInputBudgetMs():-1;
                int requestBudget=(int)Math.Min(proofRemaining,pendingBudget>=0?pendingBudget:Int32.MaxValue);
                using(var pollCts=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    if(requestBudget<=0){DiagInputTimeout(DiagInputTimeoutSite.BudgetExhausted,proofUnavailable.ElapsedMilliseconds,-1,requestBudget,proofRemaining,pendingBudget,bindInputs);var expired=new TimeoutException("fresh secure input authority unavailable within 750ms");adapter.FailPendingInputs(expired);if(g!=null)await StatefulFatalAsync(g,"secure input authority timed out").ConfigureAwait(false);throw expired;}
                    pollCts.CancelAfter(requestBudget);
                    var auth=System.Diagnostics.Stopwatch.StartNew();
                    InputLeaseBatch batch;
                    try{batch=await wire.PollInputLeaseAsync(sessionId,true,pollCts.Token,true).ConfigureAwait(false);}
                    catch(OperationCanceledException)when(!ct.IsCancellationRequested){
                        DiagInputTimeout(DiagInputTimeoutSite.PollCanceled,proofUnavailable.ElapsedMilliseconds,auth.ElapsedMilliseconds,requestBudget,proofRemaining,pendingBudget,bindInputs);
                        var expired=new TimeoutException("fresh secure input authority unavailable within 750ms");adapter.FailPendingInputs(expired);if(g!=null)await StatefulFatalAsync(g,"secure input authority timed out").ConfigureAwait(false);throw expired;
                    }
                    LastInputLeaseResponseMs=(int)Math.Min(10000,auth.ElapsedMilliseconds);
                    ct.ThrowIfCancellationRequested();
                    if(batch.Generation!=generation){DiagInputSite(DiagInputCause.GenerationAfterPoll,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.False,proofAgeMs:proofUnavailable.ElapsedMilliseconds,pendingBudget:pendingBudget,bindInputs:bindInputs);throw new InvalidOperationException("sharing generation changed");}
                    if(g!=null&&g.Failed){DiagInputSite(DiagInputCause.GuardFailedAfterPoll,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.True,proofAgeMs:proofUnavailable.ElapsedMilliseconds,pendingBudget:pendingBudget,bindInputs:bindInputs);throw new InvalidOperationException(g.Failure);}
                    DiagInputContext postContext=InputAuthorityState(binding,proofEpoch,credentialRevision,batch.Generation,adapter,ct);
                    if(postContext!=DiagInputContext.Current){DiagInputSite(DiagInputCause.ContextAfterPoll,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,postContext,generationMatch:DiagTri.True,proofAgeMs:proofUnavailable.ElapsedMilliseconds,pendingBudget:pendingBudget,bindInputs:bindInputs);var changed=new InvalidOperationException("secure input identity context changed");adapter.FailPendingInputs(changed);throw changed;}
                    if(!scope.IsCurrent()){DiagInputSite(DiagInputCause.ScopeAfterPoll,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,postContext,DiagTri.False,DiagTri.True,proofAgeMs:proofUnavailable.ElapsedMilliseconds,pendingBudget:pendingBudget,bindInputs:bindInputs);throw new InvalidOperationException("capture scope ended");}
                    if(auth.ElapsedMilliseconds<batch.LeaseMs){if(g!=null)g.ObserveServerLease();PublishFrameAuthorization(proofEpoch,batch.Generation,batch.LeaseMs,auth,ct,credentialRevision);proofUnavailable.Restart();}
                    if(bindInputs) {
                    var authority=new InputLeaseAuthority(batch.Generation,batch.LeaseMs,auth,binding,credentialRevision,proofEpoch,adapter.Descriptor,ct,
                        ()=>InputAuthorityCurrent(binding,proofEpoch,credentialRevision,batch.Generation,adapter,ct));
                    List<InputCommand> bound;bool fresh=adapter.TryBindInputs(authority,ct,out bound);batch.Events=bound;
                    if(batch.Events.Count>0&&batch.Events[0].Sequence<=lastDrainedSequence){DiagInputSite(DiagInputCause.ReplayBatch,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,count:batch.Events.Count,bindInputs:bindInputs);throw new InvalidOperationException("input batch replay");}
                    if(batch.Events.Count>0) {
                        lastDrainedSequence=batch.Events[batch.Events.Count-1].Sequence;
                        for(int i=0;i<batch.Events.Count;i++) {
                            var e=batch.Events[i];ct.ThrowIfCancellationRequested();
                            if(e.Generation!=generation){DiagInputSite(DiagInputCause.EventGeneration,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,generationMatch:DiagTri.False,index:i,count:batch.Events.Count,bindInputs:bindInputs);throw new InvalidOperationException("input generation changed");}
                            if(!scope.IsCurrent()){DiagInputSite(DiagInputCause.EventScope,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,scopeCurrent:DiagTri.False,generationMatch:DiagTri.True,index:i,count:batch.Events.Count,bindInputs:bindInputs);throw new InvalidOperationException("capture scope ended");}
                            bool applied;try{applied=await ApplyStatefulAsync(g,e,auth,batch.LeaseMs,ct,authority).ConfigureAwait(false);}catch(Exception ex)when(DiagApplyFailed(ex,true,g,ct,auth.ElapsedMilliseconds,batch.LeaseMs,i,batch.Events.Count)){throw;}
                            if(applied){staleInputMisses=0;continue;}
                            long midAge;
                            if((midAge=auth.ElapsedMilliseconds)>=batch.LeaseMs) {
                                for(int j=i;j<batch.Events.Count;j++)
                                    if(g.HeldCount>0||(MediaWire.IsStatefulKind(batch.Events[j].Kind)&&batch.Events[j].Kind!="wheel")) {
                                        DiagInputSite(DiagInputCause.LeaseMidBatch,true,g,ct,midAge,batch.LeaseMs,index:j,count:batch.Events.Count,bindInputs:bindInputs);
                                        await StatefulFatalAsync(g,"stateful input lease expired mid-batch").ConfigureAwait(false);
                                    }
                                await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);break;
                            }
                        }
                    }
                    // A stale REST lease never drains or rebinds pending input; it remains subject to the original 750ms age limit.
                    long bindAge;
                    if(!fresh&&(bindAge=auth.ElapsedMilliseconds)<batch.LeaseMs){DiagInputSite(DiagInputCause.BindFailed,true,g,ct,bindAge,batch.LeaseMs,count:batch.Events.Count,proofAgeMs:proofUnavailable.ElapsedMilliseconds,pendingBudget:pendingBudget,bindInputs:bindInputs);throw new InvalidOperationException("input authority binding failed");}
                    }
                    long remaining=MediaWire.StatefulInputPollIntervalMs-auth.ElapsedMilliseconds;
                    if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);
                }
            }
            } finally { adapter.StopAcceptingInputs(); }
        }
        // Both 750ms sites share one message; record which budget actually ran out (numbers only, no input contents).
        private void DiagInputTimeout(DiagInputTimeoutSite site,long elapsedMs,long pollMs,int requestBudget,long proofRemaining,int pendingBudget,bool bindInputs)
        {
            DiagnosticLog.Event(DiagEvent.InputBudgetTimeout,diagCorrelation,DiagValue.Of(DiagKey.Site,site),DiagValue.Of(DiagKey.ElapsedMs,elapsedMs),DiagValue.Of(DiagKey.ResponseMs,pollMs),
                DiagValue.Of(DiagKey.RequestBudgetMs,(long)requestBudget),DiagValue.Of(DiagKey.ProofRemainingMs,proofRemaining),DiagValue.Of(DiagKey.PendingBudgetMs,(long)pendingBudget),
                DiagValue.Of(DiagKey.BindInputs,bindInputs),DiagValue.Of(DiagKey.LastResponseMs,(long)LastInputLeaseResponseMs));
        }
        private static async Task StatefulFatalAsync(InputSafety g,string reason)
        {
            await g.FailAsync(reason).ConfigureAwait(false);
            throw new InvalidOperationException(reason+"; held input released, input stopped until sharing is restarted");
        }
        // All guards are re-checked on the guard thread right before the native call. Cleanup (releaseAll, up of a held
        // button/key) bypasses the lease; up outside the owned scope releases in place without moving the pointer.
        private Task<bool> ApplyStatefulAsync(InputSafety g,InputCommand c,System.Diagnostics.Stopwatch auth,int lease,CancellationToken ct,InputLeaseAuthority authority)
        {
            if(!allowRemoteInput)return Task.FromResult(false);
            MediaWire.ValidateInput(c,true);
            return g.RunAsync(()=>{
                if(authority!=null&&ct.IsCancellationRequested){g.NoteBodyCause(DiagBodyCause.CanceledBeforeNative);throw new OperationCanceledException(ct);}
                if(authority!=null&&!authority.IsContextValidFor(wire.Secure==null?null:wire.Secure.Descriptor,ct)){
                    g.NoteBodyCause(DiagBodyCause.ContextBeforeNative);var changed=new InvalidOperationException("secure input identity changed before native application");if(wire.Secure!=null)wire.Secure.FailPendingInputs(changed);throw changed;
                }
                if(c.Kind=="releaseAll") { g.ReleaseAllOnThread(); return true; }
                Rectangle b=scope.GetInputBounds();
                int px=b.Left+(int)((long)c.X*Math.Max(0,b.Width-1)/65535), py=b.Top+(int)((long)c.Y*Math.Max(0,b.Height-1)/65535);
                bool fresh=auth.ElapsedMilliseconds<lease&&(authority==null||authority.IsFresh)&&!ct.IsCancellationRequested&&scope.IsCurrent()&&(scope.IsDesktop||scope.ForegroundMatches());
                if(c.Kind=="keyUp")return g.KeyUp(c.KeyCode);
                if(c.Kind=="mouseUp")return g.ButtonUp(c.Button,px,py,fresh&&scope.OwnsPoint(px,py)&&scope.OwnsMouseTarget(px,py));
                if(!fresh)return false;
                if(c.Kind=="holdHeartbeat") { g.Heartbeat(); return true; }
                if(c.Kind=="keyDown")return g.KeyDown(c.KeyCode);
                if(c.Kind=="key"||c.Kind=="text") { g.Legacy(c,px,py); return true; }
                if(!scope.OwnsPoint(px,py)||!scope.OwnsMouseTarget(px,py))return false;
                if(c.Kind=="mouseDown")return g.ButtonDown(c.Button,px,py);
                if(c.Kind=="wheel")return g.Wheel(c.WheelDelta,px,py);
                g.Legacy(c,px,py); return true; // move/click
            });
        }
        private void DisposeGuard() { var g=Interlocked.Exchange(ref guard,null); if(g!=null)g.Dispose(); }
        internal async Task StopAsync()
        {
            CancelLocal();
            try { if(runner!=null) await runner.ConfigureAwait(false); } catch { }
            bool explicitUserStop=LastError==null&&EndReason==null;
            if(explicitUserStop)EndReason="user-stopped";
            // Cleanup is idempotent: do not relabel an error as a user stop or turn a confirmed remote end into a new 409.
            if(generation>0&&(explicitUserStop||EndReason==null))
                try { await wire.SetSharingAsync(sessionId,false,CancellationToken.None,explicitUserStop?"user":"error").ConfigureAwait(false); }
                catch(Exception ex) { DiagnosticLog.Failure(DiagEvent.SharingCleanup,diagCorrelation,ex,DiagValue.Of(DiagKey.Stage,DiagCleanupStage.StopSetSharing)); if(LastError==null)LastError=ex.Message; }
            DiagnosticLog.Event(DiagEvent.SharingStop,diagCorrelation,DiagValue.Of(DiagKey.Generation,generation),DiagValue.Of(DiagKey.ExplicitUserStop,explicitUserStop),
                DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(EndReason)),DiagValue.Of(DiagKey.HadError,LastError!=null));
            running=false;
        }
        public void Dispose() { CancelLocal(); if(cts!=null) { cts.Dispose(); cts=null; } }
        private static readonly Lazy<ImageCodecInfo> JpegEncoder=new Lazy<ImageCodecInfo>(()=>ImageCodecInfo.GetImageEncoders().First(x=>x.MimeType=="image/jpeg"));
        private static FramePacket Encode(Bitmap source,long gen,long seq) { return EncodeForQuality(source,gen,seq,70); }
        private static FramePacket EncodeForQuality(Bitmap source,long gen,long seq,int initialQuality) { return EncodeBounded(source,gen,seq,initialQuality,524288); }
        private static FramePacket EncodeBounded(Bitmap source,long gen,long seq,int initialQuality,int frameByteBudget)
        {
            if(initialQuality<30||initialQuality>85)throw new ArgumentOutOfRangeException("initialQuality");
            ImageCodecInfo codec=JpegEncoder.Value;
            for(int quality=initialQuality;quality>=30;quality=quality>30?Math.Max(30,quality-20):0) {
                using(var ms=new MemoryStream()) {
                    using(var ep=new EncoderParameters(1)) { ep.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality); source.Save(ms,codec,ep); }
                    if(ms.Length<=frameByteBudget) { var data=ms.ToArray(); MediaWire.ValidateJpegStructure(data,source.Width,source.Height); return new FramePacket{Jpeg=data,Width=source.Width,Height=source.Height,Generation=gen,Sequence=seq}; }
                }
            }
            throw new InvalidOperationException("frame exceeds 512KB");
        }
        // Returns true only when every guard passed within the fresh lease and the input was handed to NativeInput.Apply
        // (this does not claim that SendInput itself succeeded, e.g. under UIPI).
        private static bool ApplyInput(CaptureScope scope,InputCommand c,System.Diagnostics.Stopwatch auth,int lease,CancellationToken ct)
        {
            MediaWire.ValidateInput(c); Rectangle b=scope.GetInputBounds();
            int px=b.Left+(int)((long)c.X*Math.Max(0,b.Width-1)/65535), py=b.Top+(int)((long)c.Y*Math.Max(0,b.Height-1)/65535);
            if(!scope.OwnsPoint(px,py))return false;
            if(auth.ElapsedMilliseconds>=lease||ct.IsCancellationRequested||!scope.IsCurrent()||(!scope.IsDesktop&&!scope.ForegroundMatches()))return false;
            if((c.Kind=="move"||c.Kind=="click")&&!scope.OwnsMouseTarget(px,py))return false;
            NativeInput.Apply(c,px,py);
            return true;
        }
        // Same guards as ApplyInput; additionally records the consumed SendInput failure under the session correlation.
        private static bool ApplyInputCore(CaptureScope scope,InputCommand c,System.Diagnostics.Stopwatch auth,int lease,CancellationToken ct,long diagCorrelation)
        {
            MediaWire.ValidateInput(c); Rectangle b=scope.GetInputBounds();
            int px=b.Left+(int)((long)c.X*Math.Max(0,b.Width-1)/65535), py=b.Top+(int)((long)c.Y*Math.Max(0,b.Height-1)/65535);
            if(!scope.OwnsPoint(px,py))return false;
            if(auth.ElapsedMilliseconds>=lease||ct.IsCancellationRequested||!scope.IsCurrent()||(!scope.IsDesktop&&!scope.ForegroundMatches()))return false;
            if((c.Kind=="move"||c.Kind=="click")&&!scope.OwnsMouseTarget(px,py))return false;
            // The filter consumes the SendInput result on this same thread before unwinding; it never catches.
            try { NativeInput.Apply(c,px,py); } catch(Exception) when(NativeInput.DiagConsumeFailure(diagCorrelation)) { throw; }
            return true;
        }
    }

    internal static class NativeInput
    {
        private const uint INPUT_MOUSE=0,INPUT_KEYBOARD=1, MOUSEMOVE=0x0001, LEFTDOWN=0x0002,LEFTUP=0x0004,RIGHTDOWN=0x0008,RIGHTUP=0x0010,MIDDLEDOWN=0x0020,MIDDLEUP=0x0040,ABSOLUTE=0x8000,VIRTUALDESK=0x4000,KEYUP=0x0002,UNICODE=0x0004;
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION U; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
        [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint n,INPUT[] inputs,int cb);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
        internal static bool ForegroundMatches(CaptureScope scope) { return scope.ForegroundMatches(); }
        // Diagnostics: failure of the current operation on THIS thread only (reset at every Apply start, consumed on the same
        // thread before any async hop). TestSendOverride is a per-thread test seam; null in production (real SendInput).
        [ThreadStatic] private static NativeInputFailure lastFailure;
        [ThreadStatic] internal static NativeSendOverride TestSendOverride;
        internal static NativeInputFailure TakeFailure() { var f=lastFailure; lastFailure=null; return f; }
        // Exception-filter helper (always false): emit the same-thread snapshot, if any, without catching.
        internal static bool DiagConsumeFailure(long correlation)
        {
            try { var f=TakeFailure(); if(f!=null)DiagnosticLog.Event(DiagEvent.InputNativeFailure,correlation,f.Fields()); } catch { }
            return false;
        }
        private static uint Invoke(INPUT[] inputs,out int error)
        {
            var fake=TestSendOverride; if(fake!=null)return fake(inputs.Length,out error);
            uint n=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(INPUT)));
            error=Marshal.GetLastWin32Error(); // same thread, next statement; not proof of success/UIPI, may be stale
            return n;
        }
        internal static void Apply(InputCommand c,int px,int py)
        {
            lastFailure=null;
            if(c.Kind=="move") { Send(new[]{Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK)}); return; }
            if(c.Kind=="click") {
                uint down=c.Button==0?LEFTDOWN:c.Button==1?RIGHTDOWN:MIDDLEDOWN, up=c.Button==0?LEFTUP:c.Button==1?RIGHTUP:MIDDLEUP;
                INPUT d=Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK|down),u=Mouse(px,py,up);
                int error; uint sent=Invoke(new[]{d,u},out error);
                // Original partial result is fixed in locals before the cleanup call; cleanup is recorded separately.
                NativeInputFailure failed=sent!=2?new NativeInputFailure(DiagNativeOp.Click,2,sent,error,true):null;
                if(sent!=2&&sent>0) { int cleanupError; uint cleanupSent=Invoke(new[]{Mouse(px,py,up)},out cleanupError); failed=new NativeInputFailure(DiagNativeOp.Click,2,sent,error,true,true,1,cleanupSent,cleanupError); }
                if(sent!=2) { lastFailure=failed; throw new InvalidOperationException("SendInput click failed"); }
                return;
            }
            if(c.Kind=="key") { KeyTap((ushort)c.KeyCode,false);return; }
            if(c.Kind=="text") { KeyTap(c.Text[0],true);return; }
        }
        private static void KeyTap(ushort key,bool unicode)
        {
            INPUT d=Key(key,unicode?UNICODE:0),u=Key(key,(unicode?UNICODE:0)|KEYUP);
            DiagNativeOp op=unicode?DiagNativeOp.TextTap:DiagNativeOp.KeyTap;
            int error; uint sent=Invoke(new[]{d,u},out error);
            NativeInputFailure failed=sent!=2?new NativeInputFailure(op,2,sent,error,true):null;
            if(sent!=2&&sent>0) { int cleanupError; uint cleanupSent=Invoke(new[]{u},out cleanupError); failed=new NativeInputFailure(op,2,sent,error,true,true,1,cleanupSent,cleanupError); }
            if(sent!=2) { lastFailure=failed; throw new InvalidOperationException("SendInput key tap failed"); }
        }
        private static INPUT Key(ushort key,uint flags) { bool unicode=(flags&UNICODE)!=0; return new INPUT{type=INPUT_KEYBOARD,U=new UNION{ki=new KEYBDINPUT{vk=unicode?(ushort)0:key,scan=unicode?key:(ushort)0,flags=flags}}}; }
        private static INPUT Mouse(int x,int y,uint flags)
        {
            Rectangle v=SystemInformation.VirtualScreen; int dx=(int)((long)(x-v.Left)*65535/Math.Max(1,v.Width-1)),dy=(int)((long)(y-v.Top)*65535/Math.Max(1,v.Height-1));
            return new INPUT{type=INPUT_MOUSE,U=new UNION{mi=new MOUSEINPUT{dx=dx,dy=dy,dwFlags=flags}}};
        }
        private static uint Send(INPUT[] inputs)
        {
            int error; uint n=Invoke(inputs,out error);
            if(n!=inputs.Length) { lastFailure=new NativeInputFailure(DiagNativeOp.Move,inputs.Length,n,error,true); throw new InvalidOperationException("SendInput failed"); }
            return n;
        }
    }
}
