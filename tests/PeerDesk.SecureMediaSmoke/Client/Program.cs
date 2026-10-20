using System;using System.Collections.Generic;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Text;using System.Threading;using System.Threading.Tasks;using System.Web.Script.Serialization;using System.Windows.Forms;using PeerDesk.Windows;
internal static class Program {
 sealed class BindingGateNative : INativeStatefulInput { internal int Calls; public bool IsPhysicallyDown(int vk){return false;} public bool Key(int vk,bool down){Calls++;return true;} public bool ButtonDown(int b,int x,int y){Calls++;return true;} public bool ButtonUp(int b,int x,int y){Calls++;return true;} public bool ButtonUpInPlace(int b){Calls++;return true;} public bool Wheel(int d,int x,int y){Calls++;return true;} }
 static void Set(object o,string f,object v){o.GetType().GetField(f,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(o,v);}
 static int n;static void Check(bool b,string s){if(!b)throw new Exception(s);Console.WriteLine("PASS "+s);n++;}
 static string ReadStdinLine(out Encoding used){
  using(var r=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:true)){var line=r.ReadLine();used=r.CurrentEncoding;return line;}
 }
 // Synthetic local gate: no form, UI, or network; reports only first code point, encoding, and empty-object validity.
 static int RunStdinOnly(){
  try{
   Encoding enc;var line=ReadStdinLine(out enc);
   if(line==null){Console.Error.WriteLine("STDIN_ONLY_FAILED no_line");return 1;}
   string first=line.Length==0?"none":"U+"+Char.ConvertToUtf32(line,0).ToString("X4");
   bool empty=false;try{var o=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);empty=o!=null&&o.Count==0;}catch(Exception e){Console.Error.WriteLine("STDIN_ONLY_PARSE_FAILED "+e.GetType().Name);}
   Console.WriteLine("STDIN_ONLY_FIRST_CODEPOINT="+first+" INPUT_ENCODING="+enc.WebName+" VALID_EMPTY_OBJECT="+empty);
   return empty?0:1;
  }catch(Exception e){Console.Error.WriteLine("STDIN_ONLY_FAILED "+e.GetType().Name);return 1;}
 }
 [STAThread]static int Main(){Console.OutputEncoding=new UTF8Encoding(false);Console.SetError(new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false)){AutoFlush=true});if(Environment.GetEnvironmentVariable("PEERDESK_TEST_STDIN_ONLY")=="1")return RunStdinOnly();int result=0;using(var pump=new Form{ShowInTaskbar=false,WindowState=FormWindowState.Minimized}){pump.Shown+=async(s,e)=>{try{await Run();Console.WriteLine("SECURE_MEDIA_INTEGRATION_PASS count="+n);}catch(Exception error){Console.Error.WriteLine(error);result=1;}finally{pump.Close();}};Application.Run(pump);}return result;}
 static async Task Run(){
 var d=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(ReadStdinLine(out _));string url=d["url"],pin=d["pin"],sid=d["sid"];
 var profile=ServerProfile.Parse("{\"Version\":1,\"Name\":\"synthetic\",\"RendezvousUrl\":\""+url+"\",\"RelayUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\"}");
 var a=new ControlPlaneClient(profile);a.RestoreCredentials(d["aId"],d["aToken"],"Temporary","2099-01-01T00:00:00Z");
 var b=new ControlPlaneClient(profile);b.RestoreCredentials(d["bId"],d["bToken"],"Temporary","2099-01-01T00:00:00Z");
    if(Environment.GetEnvironmentVariable("PEERDESK_SECURE_AWS_CAPTURE_ONLY")=="1"){await UiGlue.Run(a,b,sid,Check);return;}
 using(var scopeForm=new Form())
 using(var sharing=new MediaSharingSession(b,sid,CaptureScope.ForOwnedWindow(scopeForm.Handle,System.Diagnostics.Process.GetCurrentProcess().Id)))
 using(var ai=new SecurePeerIdentity())using(var bi=new SecurePeerIdentity())
 using(var at=new SessionTransportClient(a,sid))using(var bt=new SessionTransportClient(b,sid)){
 var caps=await sharing.PrepareSecureAsync(CancellationToken.None);Check(!sharing.IsRunning&&caps.Generation>0,"prepare activates authority without starting capture");
 bool viewOnlyTimeout=Environment.GetEnvironmentVariable("PEERDESK_TEST_VIEWONLY_TIMEOUT")=="1";var bd=await bt.ConfigureAsync(viewOnlyTimeout?1:15,1,1,CancellationToken.None);var ad=await at.GetDescriptorAsync(CancellationToken.None);
 Check(bd.IsStrictCaptureV1&&ad.IsStrictCaptureV1&&bd.CaptureLeaseVersion==1&&bd.CaptureLeaseMs==250&&bd.MinFrameIntervalMs==100&&bd.TransportKeepaliveVersion==1&&(!viewOnlyTimeout||bd.PermissionBits==1),"both descriptors negotiate strict capture-v1 fields");
 using(var ax=new SecureSessionAdapter(a,at,ad,ai,CancellationToken.None))using(var bx=new SecureSessionAdapter(b,bt,bd,bi,CancellationToken.None)){
 Check(SecureSessionAdapter.Context(a,ad).Length>32&&Convert.ToBase64String(SecureSessionAdapter.Context(a,ad))==Convert.ToBase64String(SecureSessionAdapter.Context(b,bd)),"both endpoints canonical context identical");
 await Task.WhenAll(ax.AuthenticateAsync((l,r)=>Task.FromResult(bi.Fingerprint)),bx.AuthenticateAsync((l,r)=>Task.FromResult(ai.Fingerprint)));
 Check(ax.Ready&&bx.Ready,"actual production pinned TLS relay and encrypted Finished");
 if(Environment.GetEnvironmentVariable("PEERDESK_TEST_BOUND_EDGEFATAL")=="1"){
  var gateReceiver=new MediaWire(a){Secure=ax};sharing.AttachSecure(bx);sharing.RequestStatefulInput=true;
  Set(sharing,"scope",CaptureScope.ForDesktopShare());Set(sharing,"running",true);Set(sharing,"captureLeased",true);Set(sharing,"stateful",true);
  var root=new CancellationTokenSource();Set(sharing,"cts",root);
  var native=new BindingGateNative();var guard=new InputSafety(()=>true,native);guard.Start(@"Local\PeerDesk.BoundEdgeFatal."+Guid.NewGuid().ToString("N"));Set(sharing,"guard",guard);
  var entered=new ManualResetEventSlim(false);var release=new ManualResetEventSlim(false);Task<bool> blocker=null;Task worker=null;
  try{
   blocker=guard.RunAsync(()=>{entered.Set();release.Wait(900);return true;});
   Check(entered.Wait(1000),"fake guard action entered barrier");
   await gateReceiver.SendInputAsync(sid,new InputCommand{Generation=bd.SharingGeneration,Sequence=91,Kind="mouseDown",X=123,Y=456,Button=0},CancellationToken.None);
   var received=System.Diagnostics.Stopwatch.StartNew();while(bx.PendingInputBudgetMs()<0&&received.ElapsedMilliseconds<1500)await Task.Delay(5);
   Check(bx.PendingInputBudgetMs()>=0,"actual TLS mouseDown queued before worker");
   var method=typeof(MediaSharingSession).GetMethod("SecureStatefulInputWorker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   worker=(Task)method.Invoke(sharing,new object[]{root.Token});
   var bound=System.Diagnostics.Stopwatch.StartNew();while(bx.PendingInputBudgetMs()>=0&&bound.ElapsedMilliseconds<1000)await Task.Delay(5);
   Check(bx.PendingInputBudgetMs()<0,"actual worker binds/dequeues edge while fake guard is blocked");
   Check(!blocker.IsCompleted&&!worker.IsCompleted,"fake guard and worker remain blocked after worker dequeue");
   await Task.Delay(120);
   Check(!blocker.IsCompleted,"fake guard remains blocked through original 100ms expiry");
   release.Set();
   bool fatal=false;string fault=null;
   try{await worker;}catch(InvalidOperationException ex){fatal=ex.Message.IndexOf("stateful input lease expired mid-batch",StringComparison.Ordinal)>=0;fault=ex.Message;}
   Check(fatal,"actual worker faults at expired bound edge with expected fatal reason: "+(fault??"<none>"));
   Check(native.Calls==0&&guard.HeldCount==0&&bx.PendingInputBudgetMs()<0,"expired worker edge dispatches nothing and leaves no held/pending input");
   bool closed=false;try{await gateReceiver.SendInputAsync(sid,new InputCommand{Generation=bd.SharingGeneration,Sequence=92,Kind="mouseDown",X=123,Y=456,Button=0},CancellationToken.None);}catch{ }
   var closeWait=System.Diagnostics.Stopwatch.StartNew();while(bx.Ready&&closeWait.ElapsedMilliseconds<1000)await Task.Delay(5);
   closed=bx.Failure is IOException&&bx.Failure.Message.IndexOf("input queue is closed",StringComparison.Ordinal)>=0;
   Check(closed,"worker terminal cleanup rejects subsequent encrypted input with closed-queue failure");
   Console.WriteLine("BOUND_EDGEFATAL_ACTUAL_TLS_PASS fatal="+fatal+" nativeCalls="+native.Calls+" held="+guard.HeldCount+" pending="+bx.PendingInputBudgetMs());
   return;
  }finally{release.Set();if(worker!=null&&!worker.IsCompleted){root.Cancel();try{await worker;}catch{}}if(blocker!=null)try{await blocker;}catch{}guard.Dispose();root.Dispose();entered.Dispose();release.Dispose();}
 }
 if(Environment.GetEnvironmentVariable("PEERDESK_TEST_VIEWONLY_TIMEOUT")=="1"){
  Check((bd.PermissionBits&2)==0,"view-only descriptor has no input permission");
  sharing.AllowRemoteInput=false;sharing.RequestStatefulInput=true;sharing.AttachSecure(bx);await sharing.StartAsync(CancellationToken.None);
  var firstLease=System.Diagnostics.Stopwatch.StartNew();while(sharing.LastInputLeaseResponseMs==0&&firstLease.ElapsedMilliseconds<3000)await Task.Delay(10);
  var viewer=new MediaWire(a){Secure=ax};var frameTask=viewer.GetFrameForDisplayAsync(sid,CancellationToken.None,bd.SharingGeneration,0);if(await Task.WhenAny(frameTask,Task.Delay(2000))!=frameTask)throw new TimeoutException("readonly first frame receive timeout");var firstFrame=await frameTask;
  Check(sharing.IsRunning&&sharing.LastError==null&&sharing.LastInputLeaseResponseMs>0&&firstFrame!=null&&firstFrame.Generation==bd.SharingGeneration&&firstFrame.Sequence>0,"readonly secure sharing receives a valid frame sequence and input-lease; seq="+(firstFrame==null?-1:firstFrame.Sequence)+" gen="+(firstFrame==null?-1:firstFrame.Generation)+" running="+sharing.IsRunning+" error="+(sharing.LastError??"<null>")+" leaseResponseMs="+sharing.LastInputLeaseResponseMs);
  string lastFastTicksText=await b.SendJsonAsync("POST","/__test/input-lease-delay",new {},CancellationToken.None);long lastFastTicks=long.Parse(lastFastTicksText,System.Globalization.CultureInfo.InvariantCulture);
  Check(lastFastTicks>0&&sharing.IsRunning&&sharing.LastError==null,"test-only host enables delayed input-lease and returns last fast REST response timestamp");
  var waitForStop=System.Diagnostics.Stopwatch.StartNew();while(sharing.IsRunning&&waitForStop.ElapsedMilliseconds<4000)await Task.Delay(10);
  long sinceFreshMs=(System.Diagnostics.Stopwatch.GetTimestamp()-lastFastTicks)*1000L/System.Diagnostics.Stopwatch.Frequency;
  string stoppedError=sharing.LastError;int requestMs=sharing.LastInputLeaseResponseMs;int pendingMs=bx.PendingInputBudgetMs();
  bool expectedTimeout=stoppedError!=null&&(stoppedError.IndexOf("secure input authority timed out",StringComparison.Ordinal)>=0||stoppedError.IndexOf("fresh secure input authority unavailable within 750ms",StringComparison.Ordinal)>=0);
  Check(!sharing.IsRunning&&expectedTimeout&&pendingMs<0,"delayed REST request stops readonly worker with explicit timeout; error="+(stoppedError??"<null>")+" pendingMs="+pendingMs+" running="+sharing.IsRunning+" elapsedSinceFastResponseMs="+sinceFreshMs);
  Check(sinceFreshMs>=700&&sinceFreshMs<1600,"timeout elapsed from exact last fast REST response timestamp is bounded near original 750ms budget (lastCompletedResponseMs="+requestMs+")");
  Console.WriteLine("VIEWONLY_TIMEOUT_ACTUAL_TLS_PASS elapsedSinceFastResponseMs="+sinceFreshMs+" lastCompletedResponseMs="+requestMs+" pending="+pendingMs+" nativeInput=false foregroundIntervention=false");
  return;
 }
 if(Environment.GetEnvironmentVariable("PEERDESK_TEST_INPUT_BINDING_GATE")=="1"){
  sharing.AttachSecure(bx);var gateReceiver=new MediaWire(a){Secure=ax};var gateSender=new MediaWire(b){Secure=bx};
  await gateReceiver.SendInputAsync(sid,new InputCommand{Generation=bd.SharingGeneration,Sequence=1,Kind="mouseDown",X=123,Y=456,Button=0},CancellationToken.None);
  var arrival=System.Diagnostics.Stopwatch.StartNew();while(bx.PendingInputBudgetMs()<0&&arrival.ElapsedMilliseconds<1000)await Task.Delay(5);
  Check(bx.PendingInputBudgetMs()>=0,"actual TLS encrypted input reached secure adapter pending queue");
  CaptureProofBinding binding=sharing.LiveCaptureBinding();
  Func<bool> current=()=>{try{return binding.SameAs(sharing.LiveCaptureBinding())&&bx.Ready&&Object.ReferenceEquals(bx.Descriptor,bd);}catch{return false;}};
  var staleStart=System.Diagnostics.Stopwatch.StartNew();
  var stale=await gateSender.PollInputLeaseAsync(sid,true,CancellationToken.None,true);
  Check(stale.Generation==bd.SharingGeneration&&stale.LeaseMs==100&&stale.Events.Count==0&&staleStart.ElapsedMilliseconds>=100,
      "first test-only delayed REST authority is stale and secure payload is deferred");
  var staleAuthority=new InputLeaseAuthority(stale.Generation,stale.LeaseMs,staleStart,binding,binding.Revision,binding.Epoch,bx.Descriptor,CancellationToken.None,current);
  List<InputCommand> pending;bool staleBound=bx.TryBindInputs(staleAuthority,CancellationToken.None,out pending);
  Check(!staleBound&&pending.Count==0&&bx.PendingInputBudgetMs()>=0&&bx.Ready,
      "150ms REST authority returns false; encrypted input stays queued and adapter ready");
  var freshStart=System.Diagnostics.Stopwatch.StartNew();
  var fresh=await gateSender.PollInputLeaseAsync(sid,true,CancellationToken.None,true);
  Check(fresh.Generation==bd.SharingGeneration&&fresh.LeaseMs==100&&fresh.Events.Count==0&&freshStart.ElapsedMilliseconds<100,
      "next actual REST authority is fresh while payload remains deferred");
  var freshAuthority=new InputLeaseAuthority(fresh.Generation,fresh.LeaseMs,freshStart,binding,binding.Revision,binding.Epoch,bx.Descriptor,CancellationToken.None,current);
  bool consumed=bx.TryBindInputs(freshAuthority,CancellationToken.None,out pending);
  Check(consumed&&pending.Count==1&&pending[0].Sequence==1&&bx.PendingInputBudgetMs()<0,
      "same encrypted sequence binds exactly once under fresh REST authority");
  Console.WriteLine("INPUT_BINDING_ACTUAL_TLS_PASS elapsedMs="+arrival.ElapsedMilliseconds+" nativeInput=false");
  return;
 }
    await Task.WhenAll(ax.NegotiateDirectScreenAsync(ai,System.Net.IPAddress.Loopback,true),bx.NegotiateDirectScreenAsync(bi,System.Net.IPAddress.Loopback,true));
    Check(ax.HasDirectScreen&&bx.HasDirectScreen,"signed offer exchanged over encrypted relay then direct authenticated screen path");
 sharing.AttachSecure(bx);var receiver=new MediaWire(a){Secure=ax};var sender=new MediaWire(b){Secure=bx};
 byte[] jpeg;using(var image=new Bitmap(40,20)){using(var g=Graphics.FromImage(image))g.Clear(Color.CornflowerBlue);using(var s=new MemoryStream()){image.Save(s,ImageFormat.Jpeg);jpeg=s.ToArray();}}
 // Fixture-only synthetic JPEG, not an actual capture route (UiGlue covers actual capture). Each frame exists before its lease;
 // one proof is requested immediately before the send and the guard re-checks only that original proof, never a fresh one.
 async Task SendSyntheticFrameAsync(FramePacket synthetic,CancellationToken ct){
  var binding=sharing.LiveCaptureBinding();var proof=await sender.GetCaptureLeaseAsync(sid,binding,ct);
  await sender.PutFrameAsync(sid,synthetic,ct,()=>{ct.ThrowIfCancellationRequested();if(scopeForm.IsDisposed)throw new InvalidOperationException("owned fixture scope disposed");proof.Check(sharing.LiveCaptureBinding());});
 }

 var frame=new FramePacket{Generation=bd.SharingGeneration,Sequence=1,Width=40,Height=20,Jpeg=jpeg};
 await SendSyntheticFrameAsync(frame,CancellationToken.None);var got=await receiver.GetFrameForDisplayAsync(sid,CancellationToken.None,0,0);
 Check(got.Width==40&&got.Height==20&&got.Sequence==1,"actual encrypted JPEG metadata roundtrip");
 MediaWire.ValidateJpeg(got.Jpeg,40,20);Check(got.Jpeg.Length==jpeg.Length,"endpoint validates full JPEG after decryption");
 await receiver.SendInputAsync(sid,new InputCommand{Generation=bd.SharingGeneration,Sequence=1,Kind="mouseDown",X=123,Y=456,Button=0},CancellationToken.None);
 var limit=System.Diagnostics.Stopwatch.StartNew();InputLeaseBatch batch=null;
 while(limit.ElapsedMilliseconds<2000){var requestStart=System.Diagnostics.Stopwatch.StartNew();batch=await sender.PollInputLeaseAsync(sid,true,CancellationToken.None,true);var binding=sharing.LiveCaptureBinding();Func<bool> current=()=>{try{return binding.SameAs(sharing.LiveCaptureBinding())&&bx.Ready&&Object.ReferenceEquals(bx.Descriptor,bd);}catch{return false;}};var authority=new InputLeaseAuthority(batch.Generation,batch.LeaseMs,requestStart,binding,binding.Revision,binding.Epoch,bx.Descriptor,CancellationToken.None,current);List<InputCommand> bound;bool accepted=bx.TryBindInputs(authority,CancellationToken.None,out bound);if(accepted)batch.Events=bound;if(batch.Events.Count>0)break;await Task.Delay(10);}
 Check(batch.Events.Count==1&&batch.Events[0].Kind=="mouseDown"&&batch.LeaseMs<=100,"encrypted input with independent original REST authority lease");
 string text=null;var textReady=new TaskCompletionSource<bool>();var tid=new TransferIdentity(d["aId"],url+"|"+pin,sid,"clipboard-1",1,1);
 var clipboard=new TextClipboardCoordinator(x=>x.Equals(tid),(x,value)=>{text=value;textReady.TrySetResult(true);});
 bx.FeaturePayload+=(channel,value)=>{if(channel==3)clipboard.AcceptText(tid,value);};
 await ax.SendAsync(3,clipboard.SendText(tid,"안녕하세요 remote"),CancellationToken.None);if(await Task.WhenAny(textReady.Task,Task.Delay(2000))!=textReady.Task)throw new TimeoutException("Clipboard endpoint timeout");
 Check(text=="안녕하세요 remote","explicit text payload over actual encrypted session without OS clipboard side effect");
 var directField=typeof(SecureSessionAdapter).GetField("directConnection",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
    ((DirectPeerConnection)directField.GetValue(bx)).Dispose();
    await SendSyntheticFrameAsync(new FramePacket{Generation=bd.SharingGeneration,Sequence=2,Width=40,Height=20,Jpeg=jpeg},CancellationToken.None);
    var fallbackWait=System.Diagnostics.Stopwatch.StartNew();FramePacket fallback=null;
    while(fallbackWait.ElapsedMilliseconds<2000){fallback=await receiver.GetFrameForDisplayAsync(sid,CancellationToken.None,bd.SharingGeneration,1);if(fallback.Sequence==2)break;await Task.Delay(10);}
    Check(fallback!=null&&fallback.Sequence==2&&!bx.HasDirectScreen,"direct network stream break falls back to separately authenticated encrypted relay");
    await receiver.SendInputAsync(sid,new InputCommand{Generation=bd.SharingGeneration,Sequence=2,Kind="releaseAll"},CancellationToken.None);
    var publish=typeof(SecureSessionAdapter).GetMethod("ReceiveFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
    var publishers=new List<Task>();
    for(int i=3;i<=22;i++){long seq=i;publishers.Add(Task.Run(()=>{byte[] value;using(var ms=new MemoryStream())using(var writer=new BinaryWriter(ms)){writer.Write(bd.SharingGeneration);writer.Write(seq);writer.Write(40);writer.Write(20);writer.Write(jpeg.Length);writer.Write(jpeg);value=ms.ToArray();}publish.Invoke(ax,new object[]{value});}));}
    await Task.WhenAll(publishers);var newest=await ax.GetFrameAsync(CancellationToken.None,0,0);Check(newest.Sequence==22,"atomic latest-frame publication stays monotonic across concurrent valid receive paths");
    var listenerTcp=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listenerTcp.Start();
    try{using(var writing=new System.Net.Sockets.TcpClient()){
        await writing.ConnectAsync(System.Net.IPAddress.Loopback,((System.Net.IPEndPoint)listenerTcp.LocalEndpoint).Port);
        using(var reading=await listenerTcp.AcceptTcpClientAsync())using(var malformedConnection=new DirectPeerConnection(reading,CancellationToken.None))
        using(var unusedKeys=new SecurePeerChannel(ai,SecureSessionAdapter.Context(a,ad),true)){
            var readerMethod=typeof(SecureSessionAdapter).GetMethod("ReadDirectFramesAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var malformedTask=(Task)readerMethod.Invoke(ax,new object[]{malformedConnection,unusedKeys});
            await writing.GetStream().WriteAsync(new byte[4],0,4);
            if(await Task.WhenAny(malformedTask,Task.Delay(2000))!=malformedTask)throw new TimeoutException("Malformed direct packet check");
            await malformedTask;Check(ax.Failure is InvalidDataException&&!ax.Ready,"malformed direct framing is terminal, not silently treated as network fallback");
        }
    }}finally{listenerTcp.Stop();}
 }
 }
 await UiGlue.Run(a,b,sid,Check);
 }
}