using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeerDesk.Windows;

internal static class Program
{
    sealed class Sink : INativeStatefulInput
    {
        internal int Calls, ButtonDownCalls;
        public bool IsPhysicallyDown(int vk){return false;}
        public bool Key(int vk,bool down){Calls++;return true;}
        public bool ButtonDown(int b,int x,int y){Calls++;ButtonDownCalls++;return true;}
        public bool ButtonUp(int b,int x,int y){Calls++;return true;}
        public bool ButtonUpInPlace(int b){Calls++;return true;}
        public bool Wheel(int d,int x,int y){Calls++;return true;}
    }
    static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;

    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    static void Set(object target, string field, object value)
    {
        target.GetType().GetField(field, F).SetValue(target, value);
    }

    static object Get(object target, string field)
    {
        return target.GetType().GetField(field, F).GetValue(target);
    }

    static SessionTransportDescriptor Descriptor(string sid = "input-smoke")
    {
        return new SessionTransportDescriptor(sid, "initiator", "target", "epoch", 7, 3, 1, 1, 1, 250, 100, 1);
    }

    // Synthetic key-confirmed adapter fixture, matching CaptureProofSmoke; exercises real ReceiveInput/TryBindInputs,
    // but does not claim encrypted transport, authentication handshake, native input, or end-to-end behavior.
    static SecureSessionAdapter Adapter(SessionTransportDescriptor descriptor)
    {
        var crypto = (SecurePeerChannel)FormatterServices.GetUninitializedObject(typeof(SecurePeerChannel));
        Set(crypto, "gate", new object());
        Set(crypto, "established", true);
        Set(crypto, "sentFinished", true);
        Set(crypto, "receivedFinished", true);

        var adapter = (SecureSessionAdapter)FormatterServices.GetUninitializedObject(typeof(SecureSessionAdapter));
        Set(adapter, "crypto", crypto);
        Set(adapter, "initiator", false); // inbound channel-2 sender is the initiator
        Set(adapter, "lifetime", new CancellationTokenSource());
        Set(adapter, "sync", new object());
        Set(adapter, "descriptor", descriptor);
        Set(adapter, "json", new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 });
        // Initialize private Queue<PendingInput> without constructing the adapter's transport stack.
        var inputField = typeof(SecureSessionAdapter).GetField("inputs", F);
        Set(adapter, "inputs", Activator.CreateInstance(inputField.FieldType));
        Set(adapter, "hello", NewTcs<byte[]>());
        Set(adapter, "finished", NewTcs<byte[]>());
        Set(adapter, "readySignal", NewTcs<bool>());
        Set(adapter, "firstFrame", NewTcs<bool>());
        Set(adapter, "directOffer", NewTcs<byte[]>());
        if (!adapter.Ready) throw new Exception("synthetic adapter did not become ready");
        return adapter;
    }

    static TaskCompletionSource<T> NewTcs<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static void Receive(SecureSessionAdapter adapter, long seq, string kind = "move")
    {
        string json = "{\"generation\":7,\"sequence\":" + seq +
            ",\"kind\":\"" + kind + "\",\"x\":12,\"y\":34,\"button\":0,\"keyCode\":0,\"text\":\"\",\"wheelDelta\":0}";
        typeof(SecureSessionAdapter).GetMethod("ReceiveInput", F)
            .Invoke(adapter, new object[] { new UTF8Encoding(false, true).GetBytes(json) });
    }

    static InputLeaseAuthority Authority(SessionTransportDescriptor descriptor, Func<bool> current,
        Stopwatch start = null, CancellationToken ct = default(CancellationToken), long rev = 2, long epoch = 1,
        string bindingSid = null, string bindingDevice = null)
    {
        var binding = new CaptureProofBinding(bindingSid ?? descriptor.SessionId, "host", bindingDevice ?? descriptor.TargetDeviceId, "fingerprint",
            rev, "https://synthetic.invalid", "pin", epoch, descriptor.SharingGeneration, "scope");
        return new InputLeaseAuthority(descriptor.SharingGeneration, 100, start ?? Stopwatch.StartNew(),
            binding, rev, epoch, descriptor, ct, current);
    }

    static int Pending(SecureSessionAdapter adapter)
    {
        return (int)adapter.GetType().GetMethod("PendingInputBudgetMs", F).Invoke(adapter, null) >= 0 ? 1 : 0;
    }

    static bool Bind(SecureSessionAdapter adapter, InputLeaseAuthority authority, out List<InputCommand> batch)
    {
        return adapter.TryBindInputs(authority, CancellationToken.None, out batch);
    }

    static int Main()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var d = Descriptor();
            bool wrongSidRejected = false, wrongTargetRejected = false;
            try { Authority(d, () => true, bindingSid: "other-session"); }
            catch (InvalidOperationException) { wrongSidRejected = true; }
            try { Authority(d, () => true, bindingDevice: "other-device"); }
            catch (InvalidOperationException) { wrongTargetRejected = true; }
            Check(wrongSidRejected, "input authority constructor rejects wrong session id");
            Check(wrongTargetRejected, "input authority constructor rejects wrong target device");

            // A 100ms lease is stale after 150ms, but must leave the queued sequence unbound and available.
            {
                var staleAdapter = Adapter(d);
                Receive(staleAdapter, 41);
                var oldStart = Stopwatch.StartNew();
                Thread.Sleep(155);
                List<InputCommand> batch;
                Check(!Bind(staleAdapter, Authority(d, () => true, oldStart), out batch) && batch.Count == 0 &&
                      Pending(staleAdapter) == 1 && staleAdapter.Ready, "150ms stale authority returns false without dequeue or failure");
                Check(Bind(staleAdapter, Authority(d, () => true), out batch) && batch.Count == 1 &&
                      batch[0].Sequence == 41 && Pending(staleAdapter) == 0, "fresh authority consumes the same unbound sequence once");
            }

            // A previously consumed authority cannot be rebound, even when a later event is queued.
            {
                var a = Adapter(d);
                Receive(a, 42);
                var auth = Authority(d, () => true);
                List<InputCommand> batch;
                Check(Bind(a, auth, out batch) && batch.Count == 1, "first fresh authority binds event");
                Receive(a, 43);
                bool rejected = false;
                try { Bind(a, auth, out batch); }
                catch (System.IO.InvalidDataException) { rejected = true; }
                Check(rejected && Pending(a) == 0 && a.Failure != null && !a.Ready,
                    "duplicate authority rebind is fatal and purges pending queue");
            }

            // A context/credential callback failure cannot consume a pending event.
            {
                var a = Adapter(d);
                Receive(a, 44);
                List<InputCommand> batch;
                bool rejected = false;
                try { Bind(a, Authority(d, () => false), out batch); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && Pending(a) == 0 && a.Failure != null && !a.Ready,
                    "credential/context callback false fails closed and purges queue");
            }

            // A distinct descriptor object is not the adapter's immutable descriptor identity.
            {
                var a = Adapter(d);
                Receive(a, 45);
                var other = Descriptor("other-session");
                List<InputCommand> batch;
                bool rejected = false;
                try { Bind(a, Authority(other, () => true), out batch); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && Pending(a) == 0 && a.Failure != null && !a.Ready,
                    "descriptor identity mismatch fails closed and purges queue");
            }

            // The adapter's independent pending-input TTL remains 750ms; do not shorten it for the test.
            {
                var a = Adapter(d);
                Receive(a, 46);
                Thread.Sleep(760);
                List<InputCommand> batch;
                bool rejected = false;
                try { Bind(a, Authority(d, () => true), out batch); }
                catch (TimeoutException) { rejected = true; }
                Check(rejected && Pending(a) == 0 && a.Failure != null && !a.Ready,
                    "pending event aged past 750ms is fatal and clears entire queue");
            }

            // Freshly bound input must not reach the native sink once its original 100ms authority expires.
            // This invokes production ApplyStatefulAsync with a synthetic desktop scope and recording sink only.
            {
                var a=Adapter(d); Receive(a,47,"mouseDown");
                var authority=Authority(d,()=>true);
                List<InputCommand> batch;
                Check(Bind(a,authority,out batch)&&batch.Count==1&&Pending(a)==0,
                    "fresh authority binds edge before expiry gate");
                var wire=(MediaWire)FormatterServices.GetUninitializedObject(typeof(MediaWire));
                wire.GetType().GetProperty("Secure",F).SetValue(wire,a,null);
                var session=(MediaSharingSession)FormatterServices.GetUninitializedObject(typeof(MediaSharingSession));
                Set(session,"wire",wire); Set(session,"scope",CaptureScope.ForDesktopShare()); Set(session,"allowRemoteInput",true);
                var sink=new Sink(); var guard=new InputSafety(()=>true,sink);
                guard.Start(@"Local\PeerDesk.InputBindingSmoke."+Guid.NewGuid().ToString("N"));
                try {
                    Thread.Sleep(120);
                    var apply=typeof(MediaSharingSession).GetMethod("ApplyStatefulAsync",F);
                    var applied=(Task<bool>)apply.Invoke(session,new object[]{guard,batch[0],authority.RequestStart,authority.LeaseMs,CancellationToken.None,authority});
                    Check(!applied.GetAwaiter().GetResult()&&sink.Calls==0&&Pending(a)==0&&a.Ready,
                        "expired bound edge is not dispatched or reinserted (worker edge-fatal not exercised)");
                    Receive(a,48,"mouseDown");
                    var freshAuthority=Authority(d,()=>true);
                    List<InputCommand> freshBatch;
                    Check(Bind(a,freshAuthority,out freshBatch)&&freshBatch.Count==1&&Pending(a)==0,
                        "fresh positive-control authority binds second edge");
                    var freshApply=(Task<bool>)apply.Invoke(session,new object[]{guard,freshBatch[0],freshAuthority.RequestStart,freshAuthority.LeaseMs,CancellationToken.None,freshAuthority});
                    Check(freshApply.GetAwaiter().GetResult()&&sink.ButtonDownCalls==1&&guard.HeldCount==1,
                        "fresh mouseDown reaches fake native sink and is tracked");
                    guard.RunAsync(()=>{guard.ReleaseAllOnThread();return true;}).GetAwaiter().GetResult();
                    Check(guard.HeldCount==0,"positive-control held input releases cleanly");
                } finally { guard.Dispose(); }
            }

            Console.WriteLine("INPUT_BINDING_SMOKE_PASS checks=" + checks + " elapsedMs=" + sw.ElapsedMilliseconds +
                " synthetic=true nativeInput=false encryptedTransport=false");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}