using System;
using System.Collections;
using System.Collections.Generic;
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
    // Additive sharing response fields. Absent fields (old server) => legacy sequential 200ms loop.
    internal sealed class SharingCapabilities
    {
        public long Generation; public int MinFrameIntervalMs; public bool InputLeasePollSupported;
        public bool Negotiated { get { return InputLeasePollSupported&&MinFrameIntervalMs>0; } }
        public int StatefulInputVersion; public int InputPollIntervalMs; public int MediaOptimizationVersion;
        // Stateful only with strict version 1 + 50ms poll on top of the negotiated lease poll; anything else stays legacy 100ms.
        public bool Stateful { get { return Negotiated&&StatefulInputVersion==1&&InputPollIntervalMs==MediaWire.StatefulInputPollIntervalMs; } }
    }
    internal sealed class InputLeaseBatch { public long Generation; public int LeaseMs; public List<InputCommand> Events; }
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
            if(ov==1&&(!caps.Stateful||caps.MinFrameIntervalMs!=OptimizedFrameIntervalMs))throw new InvalidOperationException("optimized media requires stateful v1/50ms frame interval");
            return caps;
        }
        internal Task<FramePacket> GetFrameAsync(string sessionId) { return GetFrameAsync(sessionId,CancellationToken.None); }
        internal Task<FramePacket> GetFrameAsync(string sessionId,CancellationToken ct) { return GetFrameAsync(sessionId,ct,0,0); }
        internal Task<FramePacket> GetFrameAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence) { return GetFrameCoreAsync(sessionId,ct,previousGeneration,previousSequence,true); }
        internal Task<FramePacket> GetFrameForDisplayAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence) { return GetFrameCoreAsync(sessionId,ct,previousGeneration,previousSequence,false); }
        private async Task<FramePacket> GetFrameCoreAsync(string sessionId,CancellationToken ct,long previousGeneration,long previousSequence,bool decode)
        {
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
            var h=new Dictionary<string,string> {
                ["X-Share-Generation"]=frame.Generation.ToString(),["X-Frame-Sequence"]=frame.Sequence.ToString(),
                ["X-Frame-Width"]=frame.Width.ToString(),["X-Frame-Height"]=frame.Height.ToString()
            };
            await client.SendMediaAsync("PUT",Path(sessionId)+"/frame",frame.Jpeg,h,ct).ConfigureAwait(false);
        }
        internal Task SendInputAsync(string sessionId,InputCommand command) { return SendInputAsync(sessionId,command,CancellationToken.None); }
        internal async Task SendInputAsync(string sessionId,InputCommand command,CancellationToken ct)
        {
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
        internal Task<InputLeaseBatch> PollInputLeaseAsync(string sessionId,CancellationToken ct) { return PollInputLeaseAsync(sessionId,false,ct); }
        internal async Task<InputLeaseBatch> PollInputLeaseAsync(string sessionId,bool allowStateful,CancellationToken ct)
        {
            string raw=await client.SendJsonAsync("GET",Path(sessionId)+"/input-lease",null,ct).ConfigureAwait(false);
            return ParseInputLease(Deserialize<Dictionary<string,object>>(raw),allowStateful);
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
        private int minFrameIntervalMs; private string firstWorkerError,firstWorkerEndReason;
        private volatile bool running;
        // Target opt-in for stateful input (statefulInputVersion 1). Old servers ignore it and the session stays legacy taps.
        internal bool RequestStatefulInput { get; set; }
        private InputSafety guard; private volatile bool stateful; private bool optimizedMedia;
        internal bool StatefulInputActive { get { return stateful; } }
        internal bool IsRunning { get { return running; } }
        internal string LastError { get; private set; }
        internal string EndReason { get; private set; }
        private static bool IsNormalEnd(string reason) { return reason=="user-stopped"||reason=="session-ended"; }
        internal MediaSharingSession(ControlPlaneClient client,string id,CaptureScope captureScope) { host=client;sessionId=id;scope=captureScope;wire=new MediaWire(client); }
        internal string SessionId { get { return sessionId; } }
        internal void CancelLocal(string confirmedEndReason=null) {
            if(EndReason==null&&(confirmedEndReason=="session-ended"||confirmedEndReason=="expired"))EndReason=confirmedEndReason;
            InvalidateFrameAuthorization();
            if(cts!=null)try{cts.Cancel();}catch(ObjectDisposedException){} DisposeGuard();
        }
        internal async Task StartAsync(CancellationToken authorization = default(CancellationToken))
        {
            authorization.ThrowIfCancellationRequested();
            if(IsRunning)throw new InvalidOperationException("sharing already running");
            if(!scope.IsCurrent())throw new InvalidOperationException("capture scope is not live");
            cts=CancellationTokenSource.CreateLinkedTokenSource(authorization);
            try {
                SharingCapabilities caps=await wire.SetSharingWithCapabilitiesAsync(sessionId,true,RequestStatefulInput,cts.Token).ConfigureAwait(false);
                generation=caps.Generation;
                cts.Token.ThrowIfCancellationRequested();
                if(generation<=0)throw new InvalidOperationException("invalid sharing generation");
                stateful=RequestStatefulInput&&caps.Stateful;
                optimizedMedia=stateful&&caps.MediaOptimizationVersion==1;
                lock(frameAuthorizationGate){frameAuthorizationEpoch++;recentFrameAuthorization=null;}
                if(stateful) { var g=new InputSafety(()=>scope.IsCurrent()&&(scope.IsDesktop||scope.ForegroundMatches())); g.Start(); guard=g; } // second owner fails here
                running=true; LastError=null; EndReason=null; staleLeaseMisses=0; staleInputMisses=0; firstWorkerError=null; firstWorkerEndReason=null;
                if(caps.Negotiated) {
                    minFrameIntervalMs=Math.Max(optimizedMedia?MediaWire.OptimizedFrameIntervalMs:MediaWire.MinFrameIntervalFloorMs,caps.MinFrameIntervalMs);
                    // Created here (not inside the runner) so Dispose racing the runner never links to a disposed source.
                    var workers=CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    runner=Task.Run(()=>RunWorkers(workers));
                } else runner=Task.Run(()=>Loop(cts.Token));
            } catch {
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
                    using(var bitmap=scope.Capture()) {
                        FramePacket frame=Encode(bitmap,generation,++sequence);
                        ct.ThrowIfCancellationRequested();
                        if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                        // Capture+encode may outlive the first lease: re-authorize the already-encoded frame with a second fresh lease.
                        var putStart=System.Diagnostics.Stopwatch.StartNew();
                        MediaLease putLease=await FreshLeaseAsync(putStart,ct).ConfigureAwait(false);
                        if(putLease==null) { await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false); continue; } // frame dropped
                        if(frame.Generation!=generation||putLease.Generation!=frame.Generation||!scope.IsCurrent())throw new InvalidOperationException("frame generation or capture scope changed");
                        ct.ThrowIfCancellationRequested();
                        if(putStart.ElapsedMilliseconds>=putLease.LeaseMs){await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false);continue;}
                        await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                        staleLeaseMisses=0;
                    }
                    var events=await wire.PollInputAsync(sessionId,ct).ConfigureAwait(false);
                    foreach(var e in events) {
                        if(e.Generation!=generation)continue;
                        var auth=System.Diagnostics.Stopwatch.StartNew();
                        MediaLease applyLease=await FreshLeaseAsync(auth,ct).ConfigureAwait(false);
                        if(applyLease==null) { await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false); continue; } // input dropped
                        if(!scope.IsDesktop) { if(!NativeInput.ForegroundMatches(scope))continue; }
                        if(ApplyInput(scope,e,auth,applyLease.LeaseMs,ct))staleInputMisses=0;
                        else if(auth.ElapsedMilliseconds>=applyLease.LeaseMs){await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);}
                    }
                    await Task.Delay(200,ct).ConfigureAwait(false);
                }
            } catch(OperationCanceledException) { } catch(Exception ex) { EndReason=(ex as MediaHttpException)?.EndReason; LastError=IsNormalEnd(EndReason)?null:ex.Message; if(!IsNormalEnd(EndReason))try { if(generation>0) await wire.SetSharingAsync(sessionId,false,CancellationToken.None).ConfigureAwait(false); } catch { } }
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
                if(error!=null) { if(firstWorkerEndReason!=null)EndReason=firstWorkerEndReason; LastError=IsNormalEnd(firstWorkerEndReason)?null:error;
                    if(!IsNormalEnd(EndReason))try { if(generation>0) await wire.SetSharingAsync(sessionId,false,CancellationToken.None).ConfigureAwait(false); } catch { } }
            } finally { InvalidateFrameAuthorization(); DisposeGuard(); running=false; workers.Dispose(); }
        }
        private async Task Worker(Func<CancellationToken,Task> body,CancellationTokenSource workers)
        {
            CancellationToken ct=workers.Token;
            try { await body(ct).ConfigureAwait(false); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
            catch(Exception ex) {
                // An OperationCanceledException that our token did not request (e.g. HTTP timeout) is fatal, not a clean stop.
                if(Interlocked.CompareExchange(ref firstWorkerError,ex.Message,null)==null)
                    firstWorkerEndReason=(ex as MediaHttpException)?.EndReason;
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
                if(sinceLastPutAck!=null){long remaining=minFrameIntervalMs-sinceLastPutAck.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);}
                if(sinceLastAttempt!=null){long remaining=minFrameIntervalMs-sinceLastAttempt.ElapsedMilliseconds;if(remaining>0)await Task.Delay((int)remaining,ct).ConfigureAwait(false);}
                await WaitFrameAuthorization(ct).ConfigureAwait(false);
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                ct.ThrowIfCancellationRequested();if(!FreshFrameAuthorization(ct))continue;
                sinceLastAttempt=System.Diagnostics.Stopwatch.StartNew();
                using(var bitmap=scope.Capture()){
                    var frame=Encode(bitmap,generation,++sequence);
                    ct.ThrowIfCancellationRequested();
                    if(!scope.IsCurrent()||frame.Generation!=generation)throw new InvalidOperationException("frame generation or capture scope changed");
                    // No extra GET fallbacks: input20/s + max frame20/s stays inside the unchanged60/s media quota.
                    // If the 100ms proof expired during encode, discard this frame rather than renew its timestamp.
                    if(!FreshFrameAuthorization(ct)){if(++expiredEncodeDrops>=3)throw new TimeoutException("frame authorization expired during3 consecutive captures; sharing stopped");continue;}
                    await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                    expiredEncodeDrops=0;sinceLastPutAck=System.Diagnostics.Stopwatch.StartNew();
                }
            }
        }

        private async Task FrameWorker(CancellationToken ct)
        {
            if(optimizedMedia){await OptimizedFrameWorker(ct).ConfigureAwait(false);return;}
            System.Diagnostics.Stopwatch sinceLastPutAck=null;
            while(!ct.IsCancellationRequested) {
                // Wait before authorizing/capturing, not while holding an already old screenshot.
                if(sinceLastPutAck!=null) {
                    long remaining=minFrameIntervalMs-sinceLastPutAck.ElapsedMilliseconds;
                    if(remaining>0) await Task.Delay((int)remaining,ct).ConfigureAwait(false);
                }
                var leaseStart=System.Diagnostics.Stopwatch.StartNew();
                MediaLease lease=await FreshLeaseAsync(leaseStart,ct).ConfigureAwait(false);
                if(lease==null) { await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false); continue; }
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                ct.ThrowIfCancellationRequested();
                if(leaseStart.ElapsedMilliseconds>=lease.LeaseMs){await StaleLeaseMissAsync("before capture",ct).ConfigureAwait(false);continue;}
                using(var bitmap=scope.Capture()) {
                    FramePacket frame=Encode(bitmap,generation,++sequence);
                    ct.ThrowIfCancellationRequested();
                    if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                    // A separate fresh upload lease is still required after capture/encode.
                    var putStart=System.Diagnostics.Stopwatch.StartNew();
                    MediaLease putLease=await FreshLeaseAsync(putStart,ct).ConfigureAwait(false);
                    if(putLease==null) { await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false); continue; } // frame dropped
                    if(frame.Generation!=generation||putLease.Generation!=frame.Generation||!scope.IsCurrent())throw new InvalidOperationException("frame generation or capture scope changed");
                    ct.ThrowIfCancellationRequested();
                    if(putStart.ElapsedMilliseconds>=putLease.LeaseMs){await StaleLeaseMissAsync("before frame upload",ct).ConfigureAwait(false);continue;}
                    await wire.PutFrameAsync(sessionId,frame,ct).ConfigureAwait(false);
                    sinceLastPutAck=System.Diagnostics.Stopwatch.StartNew();
                    staleLeaseMisses=0;
                }
            }
        }
        private const int InputPollIntervalMs=MediaWire.LegacyInputPollIntervalMs;
        private async Task InputWorker(CancellationToken ct)
        {
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
                        if(ApplyInput(scope,e,auth,batch.LeaseMs,ct)) { staleInputMisses=0; continue; }
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
        private async Task StatefulInputWorker(CancellationToken ct)
        {
            long lastDrainedSequence=0; InputSafety g=guard;
            while(!ct.IsCancellationRequested) {
                if(g.Failed)throw new InvalidOperationException(g.Failure);
                long proofEpoch;lock(frameAuthorizationGate)proofEpoch=frameAuthorizationEpoch;
                long credentialRevision=host.CredentialRevision;
                var auth=System.Diagnostics.Stopwatch.StartNew();
                InputLeaseBatch batch=await wire.PollInputLeaseAsync(sessionId,true,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if(batch.Generation!=generation)throw new InvalidOperationException("sharing generation changed");
                if(g.Failed)throw new InvalidOperationException(g.Failure);
                if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                if(batch.Events.Count>0&&batch.Events[0].Sequence<=lastDrainedSequence)throw new InvalidOperationException("input batch replay");
                // Publish only after the whole batch, cross-batch sequence, guard and scope checks passed.
                if(auth.ElapsedMilliseconds<batch.LeaseMs){g.ObserveServerLease();PublishFrameAuthorization(proofEpoch,batch.Generation,batch.LeaseMs,auth,ct,credentialRevision);}
                if(batch.Events.Count>0) {
                    lastDrainedSequence=batch.Events[batch.Events.Count-1].Sequence;
                    if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                    if(auth.ElapsedMilliseconds>=batch.LeaseMs) {
                        if(g.HeldCount>0||batch.Events.Any(e=>MediaWire.IsStatefulKind(e.Kind)&&e.Kind!="wheel"))await StatefulFatalAsync(g,"stale stateful input batch").ConfigureAwait(false);
                        await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false);
                    } else for(int i=0;i<batch.Events.Count;i++) {
                        var e=batch.Events[i];
                        ct.ThrowIfCancellationRequested();
                        if(e.Generation!=generation)throw new InvalidOperationException("input generation changed");
                        if(!scope.IsCurrent())throw new InvalidOperationException("capture scope ended");
                        if(await ApplyStatefulAsync(g,e,auth,batch.LeaseMs,ct).ConfigureAwait(false)) { staleInputMisses=0; continue; }
                        if(auth.ElapsedMilliseconds>=batch.LeaseMs) {
                            for(int j=i;j<batch.Events.Count;j++)
                                if(g.HeldCount>0||(MediaWire.IsStatefulKind(batch.Events[j].Kind)&&batch.Events[j].Kind!="wheel"))await StatefulFatalAsync(g,"stateful input lease expired mid-batch").ConfigureAwait(false);
                            await StaleLeaseMissAsync("before input",ct).ConfigureAwait(false); break;
                        }
                    }
                }
                long remaining=MediaWire.StatefulInputPollIntervalMs-auth.ElapsedMilliseconds;
                if(remaining>0) await Task.Delay((int)remaining,ct).ConfigureAwait(false);
            }
        }
        private static async Task StatefulFatalAsync(InputSafety g,string reason)
        {
            await g.FailAsync(reason).ConfigureAwait(false);
            throw new InvalidOperationException(reason+"; held input released, input stopped until sharing is restarted");
        }
        // All guards are re-checked on the guard thread right before the native call. Cleanup (releaseAll, up of a held
        // button/key) bypasses the lease; up outside the owned scope releases in place without moving the pointer.
        private Task<bool> ApplyStatefulAsync(InputSafety g,InputCommand c,System.Diagnostics.Stopwatch auth,int lease,CancellationToken ct)
        {
            MediaWire.ValidateInput(c,true);
            return g.RunAsync(()=>{
                if(c.Kind=="releaseAll") { g.ReleaseAllOnThread(); return true; }
                Rectangle b=scope.GetInputBounds();
                int px=b.Left+(int)((long)c.X*Math.Max(0,b.Width-1)/65535), py=b.Top+(int)((long)c.Y*Math.Max(0,b.Height-1)/65535);
                bool fresh=auth.ElapsedMilliseconds<lease&&!ct.IsCancellationRequested&&scope.IsCurrent()&&(scope.IsDesktop||scope.ForegroundMatches());
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
                catch(Exception ex) { if(LastError==null)LastError=ex.Message; }
            running=false;
        }
        public void Dispose() { CancelLocal(); if(cts!=null) { cts.Dispose(); cts=null; } }
        private static readonly Lazy<ImageCodecInfo> JpegEncoder=new Lazy<ImageCodecInfo>(()=>ImageCodecInfo.GetImageEncoders().First(x=>x.MimeType=="image/jpeg"));
        private static FramePacket Encode(Bitmap source,long gen,long seq)
        {
            ImageCodecInfo codec=JpegEncoder.Value;
            for(int quality=70;quality>=30;quality-=20) {
                using(var ms=new MemoryStream()) {
                    using(var ep=new EncoderParameters(1)) { ep.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality); source.Save(ms,codec,ep); }
                    if(ms.Length<=524288) { var data=ms.ToArray(); MediaWire.ValidateJpegStructure(data,source.Width,source.Height); return new FramePacket{Jpeg=data,Width=source.Width,Height=source.Height,Generation=gen,Sequence=seq}; }
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
        internal static void Apply(InputCommand c,int px,int py)
        {
            if(c.Kind=="move") { Send(new[]{Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK)}); return; }
            if(c.Kind=="click") {
                uint down=c.Button==0?LEFTDOWN:c.Button==1?RIGHTDOWN:MIDDLEDOWN, up=c.Button==0?LEFTUP:c.Button==1?RIGHTUP:MIDDLEUP;
                INPUT d=Mouse(px,py,MOUSEMOVE|ABSOLUTE|VIRTUALDESK|down),u=Mouse(px,py,up);
                uint sent=SendInput(2,new[]{d,u},Marshal.SizeOf(typeof(INPUT)));
                if(sent!=2&&sent>0)SendInput(1,new[]{Mouse(px,py,up)},Marshal.SizeOf(typeof(INPUT)));
                if(sent!=2)throw new InvalidOperationException("SendInput click failed");
                return;
            }
            if(c.Kind=="key") { KeyTap((ushort)c.KeyCode,false);return; }
            if(c.Kind=="text") { KeyTap(c.Text[0],true);return; }
        }
        private static void KeyTap(ushort key,bool unicode)
        {
            INPUT d=Key(key,unicode?UNICODE:0),u=Key(key,(unicode?UNICODE:0)|KEYUP);
            uint sent=SendInput(2,new[]{d,u},Marshal.SizeOf(typeof(INPUT)));
            if(sent!=2&&sent>0)SendInput(1,new[]{u},Marshal.SizeOf(typeof(INPUT)));
            if(sent!=2)throw new InvalidOperationException("SendInput key tap failed");
        }
        private static INPUT Key(ushort key,uint flags) { bool unicode=(flags&UNICODE)!=0; return new INPUT{type=INPUT_KEYBOARD,U=new UNION{ki=new KEYBDINPUT{vk=unicode?(ushort)0:key,scan=unicode?key:(ushort)0,flags=flags}}}; }
        private static INPUT Mouse(int x,int y,uint flags)
        {
            Rectangle v=SystemInformation.VirtualScreen; int dx=(int)((long)(x-v.Left)*65535/Math.Max(1,v.Width-1)),dy=(int)((long)(y-v.Top)*65535/Math.Max(1,v.Height-1));
            return new INPUT{type=INPUT_MOUSE,U=new UNION{mi=new MOUSEINPUT{dx=dx,dy=dy,dwFlags=flags}}};
        }
        private static uint Send(INPUT[] inputs) { uint n=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(INPUT))); if(n!=inputs.Length)throw new InvalidOperationException("SendInput failed");return n; }
    }
}
