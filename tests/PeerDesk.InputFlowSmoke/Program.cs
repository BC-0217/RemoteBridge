using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PeerDesk.Windows;

// Synthetic contract tests against production parsing/authority/input-guard code.
// No network, live server, physical input, credentials or product UI. Not a WAN/E2E claim.
internal static class Program
{
    static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static int checks;
    static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);checks++;Console.WriteLine("PASS "+name);}
    static void Set(object o,string field,object v){var f=o.GetType().GetField(field,F);if(f==null)throw new Exception("Missing fixture field "+field);f.SetValue(o,v);}
    static object Get(object o,string field){return o.GetType().GetField(field,F).GetValue(o);}
    static object Call(object o,string name,params object[] args)
    {
        try{return o.GetType().GetMethod(name,F).Invoke(o,args);}
        catch(TargetInvocationException e){throw e.InnerException??e;}
    }
    static TaskCompletionSource<T> Tcs<T>(){return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);}
    sealed class Fixture:IDisposable
    {
        internal readonly SessionTransportDescriptor Descriptor=new SessionTransportDescriptor("flow-smoke","initiator","target","epoch",7,3,1,1,1,250,100,1);
        internal readonly SecureSessionAdapter Adapter;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        internal Fixture()
        {
            var crypto=(SecurePeerChannel)FormatterServices.GetUninitializedObject(typeof(SecurePeerChannel));
            Set(crypto,"gate",new object());Set(crypto,"established",true);Set(crypto,"sentFinished",true);Set(crypto,"receivedFinished",true);
            Adapter=(SecureSessionAdapter)FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter));
            Set(Adapter,"crypto",crypto);Set(Adapter,"initiator",false);Set(Adapter,"lifetime",lifetime);Set(Adapter,"sync",new object());Set(Adapter,"descriptor",Descriptor);
            Set(Adapter,"json",new JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8});
            Set(Adapter,"inputs",Activator.CreateInstance(typeof(SecureSessionAdapter).GetField("inputs",F).FieldType));
            Set(Adapter,"hello",Tcs<byte[]>());Set(Adapter,"finished",Tcs<byte[]>());Set(Adapter,"readySignal",Tcs<bool>());Set(Adapter,"firstFrame",Tcs<bool>());Set(Adapter,"directOffer",Tcs<byte[]>());
            Check(Adapter.Ready,"synthetic adapter ready");
        }
        internal int Count {get{return ((ICollection)Get(Adapter,"inputs")).Count;}}
        internal long Last {get{return (long)Get(Adapter,"lastInputSequence");}}
        internal void Receive(InputCommand c){Call(Adapter,"ReceiveInput",Bytes(c));}
        internal InputLeaseAuthority Authority(Stopwatch start=null,Func<bool> current=null)
        {
            var binding=new CaptureProofBinding(Descriptor.SessionId,"host",Descriptor.TargetDeviceId,"fingerprint",2,"https://synthetic.invalid","pin",1,7,"scope");
            return new InputLeaseAuthority(7,100,start??Stopwatch.StartNew(),binding,2,1,Descriptor,CancellationToken.None,current??(()=>true));
        }
        internal List<InputCommand> Bind()
        {
            List<InputCommand> b;Check(Adapter.TryBindInputs(Authority(),CancellationToken.None,out b),"fresh authority binds");return b;
        }
        internal void AgePending(int ms)
        {
            foreach(var p in (IEnumerable)Get(Adapter,"inputs"))
                p.GetType().GetField("ReceivedAt",F).SetValue(p,Stopwatch.GetTimestamp()-ms*Stopwatch.Frequency/1000);
        }
        public void Dispose(){lifetime.Cancel();lifetime.Dispose();} // no transport was constructed
    }
    static InputCommand Command(long seq,string kind="keyDown",int key=8)
    {bool keyboard=kind=="keyDown"||kind=="keyUp"||kind=="key";bool pointer=kind=="move"||kind=="mouseDown"||kind=="mouseUp"||kind=="wheel";return new InputCommand{Generation=7,Sequence=seq,Kind=kind,KeyCode=keyboard?key:0,Text="",X=pointer?12:0,Y=pointer?34:0,Button=0,WheelDelta=kind=="wheel"?120:0};}
    static byte[] Bytes(InputCommand c)
    {return Encoding.UTF8.GetBytes(Json.Serialize(new{generation=c.Generation,sequence=c.Sequence,kind=c.Kind,x=c.X,y=c.Y,button=c.Button,keyCode=c.KeyCode,text=c.Text,wheelDelta=c.WheelDelta}));}
    sealed class Sink:INativeStatefulInput
    {
        internal readonly List<string> Events=new List<string>();
        public bool IsPhysicallyDown(int vk){return false;}
        public bool Key(int vk,bool down){Events.Add((down?"D":"U")+vk);return true;}
        public bool ButtonDown(int b,int x,int y){Events.Add("BD"+b);return true;}
        public bool ButtonUp(int b,int x,int y){Events.Add("BU"+b);return true;}
        public bool ButtonUpInPlace(int b){Events.Add("BU"+b);return true;}
        public bool Wheel(int d,int x,int y){Events.Add("W"+d);return true;}
    }
    static void Await(Task task){if(!task.Wait(2500))throw new TimeoutException("fixture deadline");task.GetAwaiter().GetResult();}
    static void LegacyReceiveAndRepeat()
    {
        using(var f=new Fixture())
        {
            for(int n=1;n<=24;n++)f.Receive(Command(n));
            f.Receive(Command(25,"keyUp"));var b=f.Bind();
            Check(b.Count==25&&b.Take(24).All(x=>x.Kind=="keyDown"&&x.KeyCode==8)&&b[24].Kind=="keyUp","legacy repeated keyDown count preserved with final keyUp");
            Check(b.Select(x=>x.Sequence).SequenceEqual(Enumerable.Range(1,25).Select(x=>(long)x)),"legacy repeat sequence ordered");
            var sink=new Sink();using(var guard=new InputSafety(()=>true,sink))
            {
                guard.Start(@"Local\PeerDesk.InputFlowSmoke."+Guid.NewGuid().ToString("N"));
                Await(guard.RunAsync(()=>{foreach(var c in b){if(c.Kind=="keyDown")guard.KeyDown(c.KeyCode);else guard.KeyUp(c.KeyCode);}return true;}));
                Check(sink.Events.Count(x=>x=="D8")==24&&sink.Events.Count(x=>x=="U8")==1&&guard.HeldCount==0,"real guard fake sink preserves all repeats and releases once");
            }
        }
        using(var f=new Fixture())
        {
            var expected=new[]{"keyDown","keyDown","mouseDown","move","wheel","mouseUp","keyUp","keyUp"};
            for(int i=0;i<expected.Length;i++)f.Receive(Command(i+1,expected[i],i==0||i==7?17:65));
            var b=f.Bind();Check(b.Select(x=>x.Kind).SequenceEqual(expected),"typing and mouse edges retain original receive order");
        }
    }
    static void BoundsReplayAndRelease()
    {
        using(var f=new Fixture())
        {
            for(int n=1;n<=32;n++)f.Receive(Command(n));
            bool rejected=false;try{f.Receive(Command(33));}catch(InvalidDataException){rejected=true;}
            Check(rejected&&f.Count==32&&f.Last==32,"33rd original event rejected without sequence advancement");
        }
        using(var f=new Fixture())
        {
            f.Receive(Command(10));bool rejected=false;try{f.Receive(Command(9));}catch(InvalidDataException){rejected=true;}
            Check(rejected&&f.Count==1&&f.Last==10,"reversed sequence rejected without enqueue");
            f.Receive(Command(11,"releaseAll"));
            var b=f.Bind();Check(b.Count==1&&b[0].Kind=="releaseAll","releaseAll barrier discards unsent receiver backlog");
        }
    }
    static void AuthorityAndExpiry()
    {
        using(var f=new Fixture())
        {
            f.Receive(Command(1));var old=Stopwatch.StartNew();Thread.Sleep(120);List<InputCommand> b;
            Check(!f.Adapter.TryBindInputs(f.Authority(old),CancellationToken.None,out b)&&b.Count==0&&f.Count==1,"expired original request-start authority consumes no pending input");
            var authority=f.Authority();Check(f.Adapter.TryBindInputs(authority,CancellationToken.None,out b)&&b.Count==1,"unbound input may bind to genuinely new fresh authority");
            f.Receive(Command(2));bool rejected=false;try{f.Adapter.TryBindInputs(authority,CancellationToken.None,out b);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&f.Count==0&&!f.Adapter.Ready,"already-consumed authority cannot rebind and fails closed");
        }
        using(var f=new Fixture())
        {
            f.Receive(Command(1));f.AgePending(751);bool rejected=false;
            try{f.Bind();}catch(TimeoutException){rejected=true;}
            Check(rejected&&f.Count==0&&!f.Adapter.Ready,"751ms original receiver age expires without reauthentication");
        }
        using(var f=new Fixture())
        {
            f.Receive(Command(1));List<InputCommand> b;bool rejected=false;
            try{f.Adapter.TryBindInputs(f.Authority(current:()=>false),CancellationToken.None,out b);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&f.Count==0&&!f.Adapter.Ready,"changed context purges pending events");
        }
    }
    static object StaticCall(string name,params object[] args)
    {
        var method=typeof(SecureSessionAdapter).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
        if(method==null)throw new Exception("Missing production batch API "+name);
        try{return method.Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException??e;}
    }
    static byte[] Encode(IList<InputCommand> commands){return (byte[])StaticCall("EncodeInputBatch",commands);}
    static InputCommand[] Decode(byte[] bytes,long generation=7,long previous=0){return (InputCommand[])StaticCall("DecodeInputBatch",bytes,generation,previous);}
    static bool Reject(Action action)
    {
        try{action();return false;}
        catch(Exception e){if(e is InvalidOperationException||e is InvalidDataException||e is ArgumentException||e is DecoderFallbackException||e is OverflowException)return true;throw;}
    }
    static void BatchCodec()
    {
        var events=Enumerable.Range(1,31).Select(n=>Command(n)).Concat(new[]{Command(32,"keyUp")}).ToArray();
        byte[] wire=Encode(events);var roundtrip=Decode(wire);
        Check(roundtrip.Length==32&&roundtrip.Take(31).All(c=>c.Kind=="keyDown"&&c.KeyCode==8)&&roundtrip[31].Kind=="keyUp","batch roundtrip retains 31 repeats and final keyUp");
        Check(roundtrip.Select(c=>c.Sequence).SequenceEqual(events.Select(c=>c.Sequence)),"batch roundtrip exact logical sequence");
        Check(Reject(()=>Encode(new InputCommand[0])),"empty batch rejected");
        Check(Reject(()=>Encode(Enumerable.Range(1,33).Select(n=>Command(n)).ToArray())),"33 event batch rejected");
        var wrong=Command(2);wrong.Generation=8;
        Check(Reject(()=>Encode(new[]{Command(1),wrong})),"mixed generation batch rejected");
        Check(Reject(()=>Encode(new[]{Command(2),Command(1)})),"reversed batch sequence rejected");
        Check(Reject(()=>Encode(new[]{Command(1),Command(1)})),"duplicate batch sequence rejected");
        Check(Reject(()=>Decode(wire,8)),"wrong expected generation rejected");
        Check(Reject(()=>Decode(wire,7,1)),"batch replay against previous sequence rejected");
        Check(Reject(()=>Decode(new byte[16385])),"oversized decoded packet rejected");
        Check(Reject(()=>Decode(new byte[]{0xff,0xfe,0xff})),"invalid encoding rejected");
        Check(Reject(()=>Decode(wire.Take(wire.Length-1).ToArray())),"truncated packet rejected");
        var kinds=new[]{"keyDown","keyDown","mouseDown","move","wheel","mouseUp","keyUp","keyUp"};
        var mixed=kinds.Select((k,i)=>Command(i+1,k,i==0||i==7?17:65)).ToArray();
        Check(Decode(Encode(mixed)).Select(c=>c.Kind).SequenceEqual(kinds),"mixed typing pointer batch order preserved");
    }
    static byte[] Control(byte kind,byte value,long generation=7)
    {var p=new byte[11];p[0]=kind;p[1]=1;BitConverter.GetBytes(generation).CopyTo(p,2);p[10]=value;return p;}
    static void BatchReceiveAtomic()
    {
        using(var f=new Fixture())
        {
            var bytes=Encode(new[]{Command(1),Command(2,"keyUp")});
            Check(Reject(()=>Call(f.Adapter,"ReceiveInput",bytes))&&f.Count==0&&f.Last==0,"unnegotiated receiver rejects batch without mutation");
            Set(f.Adapter,"localAckedCaps",3);Call(f.Adapter,"ReceiveInput",bytes);
            var b=f.Bind();Check(b.Count==2&&b[0].Sequence==1&&b[1].Sequence==2,"negotiated receiver publishes actual decoded batch in order");
        }
        using(var f=new Fixture())
        {
            Set(f.Adapter,"localAckedCaps",3);f.Receive(Command(1));
            var bytes=Encode(new[]{Command(2),Command(3)});
            // Fixed v1 wire: six-byte envelope + 39 bytes/event; corrupt LAST event kind.
            bytes[6+39+16]=255;
            Check(Reject(()=>Call(f.Adapter,"ReceiveInput",bytes))&&f.Count==1&&f.Last==1,"invalid last event cannot partially publish first event or advance replay state");
            var valid=Encode(new[]{Command(2),Command(3)});Call(f.Adapter,"ReceiveInput",valid);
            Check(f.Count==3&&f.Last==3,"valid same sequence remains acceptable after rejected malformed batch");
        }
        using(var f=new Fixture())
        {
            Set(f.Adapter,"localAckedCaps",3);for(int i=1;i<=31;i++)f.Receive(Command(i));
            Check(Reject(()=>Call(f.Adapter,"ReceiveInput",Encode(new[]{Command(32),Command(33)})))&&f.Count==31&&f.Last==31,"aggregate pending cap counts original events and rejects whole batch atomically");
            Call(f.Adapter,"ReceiveInput",Encode(new[]{Command(32,"releaseAll"),Command(33),Command(34,"keyUp")}));
            var b=f.Bind();Check(b.Select(c=>c.Kind).SequenceEqual(new[]{"releaseAll","keyDown","keyUp"}),"batch releaseAll preserves receiver barrier and following edge order");
        }
    }
    static void NegotiationAndFailure()
    {
        using(var f=new Fixture())
        {
            Set(f.Adapter,"initiator",true);
            Check(!f.Adapter.InputBatchingReady,"old peer and delayed ACK remain single mode");
            Check(Reject(()=>Call(f.Adapter,"ReceiveInputControl",Control(91,1)))&&!f.Adapter.InputBatchingReady,"unsolicited capability ACK rejected");
            Set(f.Adapter,"capsOfferSent",1);
            Check(Reject(()=>Call(f.Adapter,"ReceiveInputControl",Control(91,1,8)))&&!f.Adapter.InputBatchingReady,"old generation capability ACK cannot enable batching");
            Call(f.Adapter,"ReceiveInputControl",Control(91,1));
            Check(f.Adapter.InputBatchingReady,"matching authenticated-control ACK enables sender batching");
            Check(Reject(()=>Call(f.Adapter,"ReceiveInputControl",Control(91,1))),"duplicate capability ACK rejected");
        }
        using(var f=new Fixture())
        {
            Check(!(bool)Call(f.Adapter,"ReceiveInputControl",new byte[]{70})&&!(bool)Call(f.Adapter,"ReceiveInputControl",new byte[]{80}),"reserved existing ACK and direct opcodes not claimed");
            Set(f.Adapter,"localAckedCaps",2);
            Call(f.Adapter,"ReceiveInputControl",Control(92,2));
            Call(f.Adapter,"ReceiveInputControl",Control(92,9));
            Check(f.Adapter.PeerInputFailure==PeerInputFailureReason.QueueAge,"first typed peer failure survives later SendFailed notice");
            f.Adapter.FailPendingInputs(new IOException("synthetic transport failure"));
            Check(f.Adapter.PeerInputFailure==PeerInputFailureReason.QueueAge&&f.Adapter.Failure!=null,"peer reason survives local cleanup failure without clearing local failure");
        }
        using(var f=new Fixture())
        {
            Set(f.Adapter,"localAckedCaps",2);
            Call(f.Adapter,"ReceiveInputControl",Control(92,254));
            Check(f.Adapter.PeerInputFailure==PeerInputFailureReason.Unknown,"unknown peer failure code maps to fixed Unknown rather than free text");
        }
    }
    static void LocalReasonFirstFailure()
    {
        using(var f=new Fixture())
        {
            var first=new IOException("synthetic context failure");
            f.Adapter.FailPendingInputs(first,PeerInputFailureReason.ContextMismatch);
            Check(f.Adapter.LocalInputFailure==PeerInputFailureReason.ContextMismatch,"explicit Context reason is not preempted by generic Unknown cleanup");
            f.Adapter.FailPendingInputs(new IOException("later cleanup"));
            Check(f.Adapter.LocalInputFailure==PeerInputFailureReason.ContextMismatch&&Object.ReferenceEquals(f.Adapter.Failure,first),"later default cleanup preserves first explicit reason and exception");
        }
        using(var f=new Fixture())
        {
            var first=new IOException("synthetic unknown failure");
            f.Adapter.FailPendingInputs(first);
            Check(f.Adapter.LocalInputFailure==PeerInputFailureReason.Unknown,"default independent failure records Unknown without claiming native failure");
            f.Adapter.FailPendingInputs(new IOException("later typed"),PeerInputFailureReason.NativeFailure);
            Check(f.Adapter.LocalInputFailure==PeerInputFailureReason.Unknown&&Object.ReferenceEquals(f.Adapter.Failure,first),"later typed failure cannot overwrite earlier Unknown and exception");
        }
    }
    static void GateExpiry()
    {
        foreach(bool batch in new[]{true,false})
        using(var f=new Fixture())
        using(var gate=new SemaphoreSlim(0,1))
        {
            Set(f.Adapter,"initiator",true);Set(f.Adapter,"peerAckedCaps",1);Set(f.Adapter,"sending",gate);
            int callbacks=0;var oldest=Stopwatch.StartNew();
            Action guard=()=>{
                callbacks++;if(oldest.ElapsedMilliseconds>=750)throw new TimeoutException("synthetic original enqueue expired");
                throw new Exception("fixture released gate too early");
            };
            if(!batch)Set(f.Adapter,"peerAckedCaps",0);
            var task=batch?f.Adapter.SendInputsAsync(new[]{Command(1)},CancellationToken.None,guard):f.Adapter.SendInputGuardedAsync(Command(1),CancellationToken.None,guard);
            Check(!task.IsCompleted&&callbacks==0,(batch?"batch":"oldpeer single")+" preSeal guard waits behind actual shared sending gate");
            Thread.Sleep(760);gate.Release();bool expired=false;
            try{Await(task);}catch(AggregateException e){expired=e.Flatten().InnerExceptions.Any(x=>x is TimeoutException);}
            catch(TimeoutException){expired=true;}
            Check(expired&&callbacks==1&&f.Adapter.Ready&&f.Adapter.Failure==null,(batch?"batch":"oldpeer single")+" original age expiry checked after gate and before Seal without transport or epoch consumption");
        }
    }
    [System.STAThread]
    static int Main(string[] args)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            if(args.Contains("--tls-first")){TlsFlow.Run(true);return 0;} if(args.Contains("--tls-flow")){TlsFlow.Run();return 0;}
            LegacyReceiveAndRepeat();BoundsReplayAndRelease();AuthorityAndExpiry();BatchCodec();BatchReceiveAtomic();NegotiationAndFailure();GateExpiry();LocalReasonFirstFailure();
            Console.WriteLine("INPUT_FLOW_SMOKE_PASS checks="+checks+" elapsedMs="+sw.ElapsedMilliseconds+" synthetic=true nativeInput=false encryptedTransport=false");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
