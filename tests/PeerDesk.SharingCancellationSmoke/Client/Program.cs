using System;using System.Diagnostics;using System.Threading;using System.Threading.Tasks;using System.Windows.Forms;using PeerDesk.Windows;
class Program {
 [STAThread]static int Main(string[] args){
 var profile=ServerProfile.Parse(Console.ReadLine());var c=new ControlPlaneClient(profile);
 c.RestoreCredentials(Console.ReadLine(),Console.ReadLine(),Console.ReadLine(),Console.ReadLine());string sid=Console.ReadLine();
 if(args.Length>0&&args[0]=="--end-feedback")return EndFeedback(c,sid,profile);
 if(args.Length>0&&args[0]=="--mouse-events")return MouseEvents(c,sid,profile);
 if(args.Length>0&&args[0]=="--motion-budget")return Motion(c,sid,profile);
 if(args.Length>0&&(args[0]=="--perf"||args[0]=="--send-order"||args[0]=="--send-close"))return Perf(c,sid,profile,args[0]!="--perf",args[0]=="--send-close");
 if(args.Length>0&&args[0].StartsWith("--"))return Slow(c,sid,args[0]);
 using(var form=new Form()){form.Width=200;form.Height=100;var scope=CaptureScope.ForOwnedWindow(form.Handle,Process.GetCurrentProcess().Id);
 using(var media=new MediaSharingSession(c,sid,scope))using(var ct=new CancellationTokenSource()){
 ct.Cancel();bool rejected=false;try{media.StartAsync(ct.Token).GetAwaiter().GetResult();}catch{rejected=true;}
 if(!rejected||media.IsRunning)throw new Exception("pre-cancel did not stop");Console.WriteLine("PRE_CANCEL_PASS");
 if(Console.ReadLine()!="CONTINUE")throw new Exception("missing stage gate");
 }
 using(var media=new MediaSharingSession(c,sid,scope))using(var ct=new CancellationTokenSource()){
 ct.CancelAfter(500);bool rejected=false;try{media.StartAsync(ct.Token).GetAwaiter().GetResult();}catch{rejected=true;}
 if(!rejected||media.IsRunning)throw new Exception("delayed cancellation did not stop");
 media.StopAsync().GetAwaiter().GetResult();Console.WriteLine("DELAY_CANCEL_PASS");
 }}
 return 0;
 }

 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetCapture();
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr hwnd,uint flag);

 static int EndFeedback(ControlPlaneClient target,string sid,ServerProfile profile){
 var requester=new ControlPlaneClient(profile);requester.RestoreCredentials(Console.ReadLine(),Console.ReadLine(),Console.ReadLine(),Console.ReadLine());
 var wire=new MediaWire(target);int result=1;
 var context=new ApplicationContext();var timer=new System.Windows.Forms.Timer{Interval=1};
 timer.Tick+=async(s,e)=>{timer.Stop();try{
 var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
 var viewerWire=new MediaWire(requester);
 try{await viewerWire.GetFrameAsync(sid);throw new Exception("unstarted share unexpectedly active");}catch(MediaHttpException ex){if(ex.StatusCode!=409||ex.EndReason!=null)throw new Exception("unstarted409 called normal");}
 foreach(string reason in new[]{"user","error","revoke"}){
 var caps=await wire.SetSharingWithCapabilitiesAsync(sid,true,true,CancellationToken.None);
 byte[] jpg;using(var b=new System.Drawing.Bitmap(8,8))using(var m=new System.IO.MemoryStream()){b.Save(m,System.Drawing.Imaging.ImageFormat.Jpeg);jpg=m.ToArray();}
 await wire.PutFrameAsync(sid,new FramePacket{Jpeg=jpg,Width=8,Height=8,Generation=caps.Generation,Sequence=1},CancellationToken.None);
 using(var viewer=new ViewerForm(requester,sid)){
 var h=viewer.Handle;await(Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);
 if(viewer.FrameCount!=1)throw new Exception("fresh actual frame missing");
 if(reason=="revoke")await requester.RevokeAsync(sid,CancellationToken.None);else await wire.SetSharingAsync(sid,false,CancellationToken.None,reason);
 await(Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);
 string expected=reason=="user"?"user-stopped":reason=="error"?"error-stopped":"session-ended";
 if(viewer.EndReason!=expected)throw new Exception("typed end mismatch "+reason+" got "+viewer.EndReason);
 string text=((Label)typeof(ViewerForm).GetField("state",flags).GetValue(viewer)).Text;
 if(reason!="error"&&text.Contains("오류"))throw new Exception("normal end labeled error");
 if(reason=="user"&&!text.Contains("정상 종료"))throw new Exception("missing normal stop wording");
 if(reason=="error"&&!text.Contains("오류"))throw new Exception("real error hidden");
 Console.WriteLine("END_FEEDBACK_VIEWER_PASS "+expected);
 }
 }
 Console.WriteLine("TIMING_PASS --end-feedback");result=0;
 }catch(Exception ex){Console.WriteLine("END_FEEDBACK_FAIL "+ex.Message);}finally{context.ExitThread();}};
 timer.Start();Application.Run(context);timer.Dispose();return result;
 }

 static int MouseEvents(ControlPlaneClient target,string sid,ServerProfile profile){
 var requester=new ControlPlaneClient(profile);requester.RestoreCredentials(Console.ReadLine(),Console.ReadLine(),Console.ReadLine(),Console.ReadLine());
 var wire=new MediaWire(target);var caps=wire.SetSharingWithCapabilitiesAsync(sid,true,true,CancellationToken.None).GetAwaiter().GetResult();
 byte[] jpg;using(var b=new System.Drawing.Bitmap(320,240))using(var m=new System.IO.MemoryStream()){b.Save(m,System.Drawing.Imaging.ImageFormat.Jpeg);jpg=m.ToArray();}
 wire.PutFrameAsync(sid,new FramePacket{Jpeg=jpg,Width=320,Height=240,Generation=caps.Generation,Sequence=1},CancellationToken.None).GetAwaiter().GetResult();
 int result=1;using(var viewer=new ViewerForm(requester,sid)){
 viewer.StartPosition=FormStartPosition.Manual;viewer.Location=new System.Drawing.Point(40,40);
 var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
 Func<string,object> field=n=>typeof(ViewerForm).GetField(n,flags).GetValue(viewer);
 viewer.Shown+=async(s,e)=>{bool injectedDown=false;try{
 await(Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);
 if(viewer.FrameCount!=1)throw new Exception("no actual frame");
 viewer.Activate();viewer.Focus();await Task.Delay(50);
 if(GetForegroundWindow()!=viewer.Handle)throw new Exception("owned Viewer is not foreground; no native injection");
 var point=viewer.Display.PointToScreen(new System.Drawing.Point(viewer.Display.Width/2,viewer.Display.Height/2));
 if(GetAncestor(WindowFromPoint(point),2)!=viewer.Handle)throw new Exception("owned Viewer point obstructed");
 NativeInput.Apply(new InputCommand{Kind="move"},point.X,point.Y);await Task.Delay(40);
 NativeInput.Apply(new InputCommand{Kind="click"},point.X,point.Y);await Task.Delay(40); // establish real viewer focus
 NativePointer.Down(point.X,point.Y);injectedDown=true;await Task.Delay(100);
 int held=((System.Collections.Generic.HashSet<int>)field("heldButtons")).Count;
 Console.WriteLine("MOUSE_DIAG down held="+held+" capture="+(GetCapture()==viewer.Display.Handle)+" viewerFocus="+viewer.ContainsFocus);
 if(held!=1||GetCapture()!=viewer.Display.Handle)throw new Exception("real PictureBox down lost hold/capture");
 for(int i=1;i<=4;i++){if(GetForegroundWindow()!=viewer.Handle||GetCapture()!=viewer.Display.Handle)throw new Exception("owned drag scope lost");
 NativeInput.Apply(new InputCommand{Kind="move"},point.X+i*15,point.Y+i*8);await Task.Delay(70);}
 await Task.Delay(260);
 if(((System.Collections.Generic.HashSet<int>)field("heldButtons")).Count!=1)throw new Exception("real drag hold vanished");
 NativePointer.Up();injectedDown=false;await Task.Delay(200);
 if(((System.Collections.Generic.HashSet<int>)field("heldButtons")).Count!=0)throw new Exception("MouseUp did not clear local hold");
 var wait=Stopwatch.StartNew();while((bool)field("pumping")&&wait.ElapsedMilliseconds<1500)await Task.Delay(10);
 if((bool)field("inputFailed"))throw new Exception("Viewer send failed");
 await wire.SetSharingAsync(sid,false,CancellationToken.None,"user");
 await(Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);
 var endWait=Stopwatch.StartNew();while(viewer.EndReason==null&&endWait.ElapsedMilliseconds<2000){await Task.Delay(25);await(Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);}
 if(viewer.EndReason!="user-stopped")throw new Exception("normal stop not recognized by actual Viewer: "+viewer.EndReason+" / "+((Label)field("state")).Text);
 var label=(Label)field("state");if(!label.Text.Contains("정상 종료")||label.Text.Contains("오류"))throw new Exception("normal stop mislabeled");
 Console.WriteLine("NORMAL_END_VIEWER_PASS user-stopped no error wording");
 Console.WriteLine("MOUSE_EVENTS_PASS actual native mouse -> PictureBox event handlers -> pinned HTTPS");
 Console.WriteLine("TIMING_PASS --mouse-events");result=0;
 }catch(Exception ex){Console.WriteLine("MOUSE_EVENTS_FAIL "+ex.Message);}finally{if(injectedDown)NativePointer.Up();await viewer.CloseSessionAsync();viewer.Close();}};
 Application.Run(viewer);
 }return result;
 }
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr WindowFromPoint(System.Drawing.Point p);
 static class NativePointer{
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct INPUT{public uint t;public UNION u;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]struct UNION{[System.Runtime.InteropServices.FieldOffset(0)]public MOUSE m;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct MOUSE{public int x,y;public uint data,flags,time;public UIntPtr extra;}
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint SendInput(uint n,INPUT[] input,int size);
 static void Send(uint flags,int x=0,int y=0){var v=SystemInformation.VirtualScreen;var i=new INPUT{t=0,u=new UNION{m=new MOUSE{x=(int)((long)(x-v.Left)*65535/Math.Max(1,v.Width-1)),y=(int)((long)(y-v.Top)*65535/Math.Max(1,v.Height-1)),flags=flags}}};if(SendInput(1,new[]{i},System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)))!=1)throw new Exception("owned mouse injection failed");}
 public static void Down(int x,int y){Send(0xC003,x,y);}public static void Up(){Send(4);}
 }

 static int Motion(ControlPlaneClient target,string sid,ServerProfile profile) {
 var requester=new ControlPlaneClient(profile);requester.RestoreCredentials(Console.ReadLine(),Console.ReadLine(),Console.ReadLine(),Console.ReadLine());
 var wire=new MediaWire(target);var caps=wire.SetSharingWithCapabilitiesAsync(sid,true,true,CancellationToken.None).GetAwaiter().GetResult();
 byte[] jpg;using(var b=new System.Drawing.Bitmap(8,8))using(var m=new System.IO.MemoryStream()){b.Save(m,System.Drawing.Imaging.ImageFormat.Jpeg);jpg=m.ToArray();}
 wire.PutFrameAsync(sid,new FramePacket{Jpeg=jpg,Width=8,Height=8,Generation=caps.Generation,Sequence=1},CancellationToken.None).GetAwaiter().GetResult();
 int result=1; using(var viewer=new ViewerForm(requester,sid)) {
 viewer.ShowInTaskbar=false;viewer.Opacity=0;var h=viewer.Handle; // no Show/Activate: no user focus change
 var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
 Func<string,object> field=n=>typeof(ViewerForm).GetField(n,flags).GetValue(viewer);
 Action<string,object> set=(n,v)=>typeof(ViewerForm).GetField(n,flags).SetValue(viewer,v);
 Action<InputCommand> enqueue=e=>typeof(ViewerForm).GetMethod("Enqueue",flags).Invoke(viewer,new object[]{e});
 var context=new ApplicationContext();
 var timer=new System.Windows.Forms.Timer{Interval=1};timer.Tick+=async(s,e)=>{timer.Stop();try {
 await (Task)typeof(ViewerForm).GetMethod("PollOnce",flags).Invoke(viewer,null);
 if(viewer.FrameCount!=1)throw new Exception("no real HTTPS frame");
 var ticks=(System.Collections.Generic.Queue<long>)field("sentInputTicks");var clock=(Stopwatch)field("queueClock");
 // Deterministic budget state injection, not a claim of 25 actual historical POSTs.
 for(int i=0;i<25;i++)ticks.Enqueue(clock.ElapsedMilliseconds);
 enqueue(new InputCommand{Kind="move",X=1,Y=1});enqueue(new InputCommand{Kind="move",X=2,Y=2});enqueue(new InputCommand{Kind="move",X=3,Y=3});
 var pending=(System.Collections.IList)field("pending");if(pending.Count!=2)throw new Exception("adjacent tail move did not coalesce");
 enqueue(new InputCommand{Kind="mouseUp",X=3,Y=3});
 var wait=Stopwatch.StartNew();while(!(bool)field("sending")&&wait.ElapsedMilliseconds<400)await Task.Delay(5);
 if(!(bool)field("sending"))throw new Exception("edge blocked by motion budget");
 ((System.Collections.Generic.HashSet<int>)field("heldKeys")).Add(17);
 enqueue(new InputCommand{Kind="keyDown",KeyCode=17});
 typeof(ViewerForm).GetMethod("ReleaseAllLocal",flags).Invoke(viewer,null);
 if(pending.Count!=2)throw new Exception("release barrier retained unsent down");
 while(pending.Count>0&&wait.ElapsedMilliseconds<1000)await Task.Delay(5);
 if(pending.Count!=0)throw new Exception("release not sent within bounded wait");
 Console.WriteLine("MOTION_LOCAL_PASS adjacent-coalesce budget-state-injected edge-unblocked release-barrier");
 Console.WriteLine("TIMING_PASS --motion-budget");result=0;
 }catch(Exception ex){Console.WriteLine("MOTION_FAIL "+ex.Message);}finally{await viewer.CloseSessionAsync();context.ExitThread();}};
 timer.Start();Application.Run(context);timer.Dispose();
 }return result;
 }
 static int Perf(ControlPlaneClient c,string sid,ServerProfile profile,bool sendOrder,bool closePending){
 var a=new ControlPlaneClient(profile);a.RestoreCredentials(Console.ReadLine(),Console.ReadLine(),Console.ReadLine(),Console.ReadLine());int result=1;
 using(var form=new Form()){form.Width=1280;form.Height=720;form.Shown+=async(s,e)=>{
 using(var media=new MediaSharingSession(c,sid,CaptureScope.ForOwnedWindow(form.Handle,Process.GetCurrentProcess().Id))){ViewerForm viewer=null;try{
 await media.StartAsync();viewer=new ViewerForm(a,sid);viewer.Show();var cpu=Process.GetCurrentProcess().TotalProcessorTime;var watch=Stopwatch.StartNew();
 if(sendOrder){while(viewer.FrameCount<2&&watch.ElapsedMilliseconds<3000)await Task.Delay(10);if(viewer.FrameCount==0)throw new Exception("viewer no valid frame");var send=typeof(ViewerForm).GetMethod("SendText",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);var first=(Task)send.Invoke(viewer,new object[]{(char)113});var second=(Task)send.Invoke(viewer,new object[]{(char)119});var third=(Task)send.Invoke(viewer,new object[]{(char)101});if(closePending){await Task.Delay(30);await viewer.CloseSessionAsync();}await Task.WhenAll(first,second,third);if(closePending&&((System.Collections.ICollection)typeof(ViewerForm).GetField("pending",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(viewer)).Count!=0)throw new Exception("pending input remained after close");await Task.Delay(350);}else await Task.Delay(5000);
 if((!closePending&&!media.IsRunning)||viewer.LastFrameSequence<2)throw new Exception("performance baseline no active frames "+media.LastError);
 Console.WriteLine("PERF cpuMs="+(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds+" elapsedMs="+watch.ElapsedMilliseconds+" rendered="+viewer.FrameCount+" lastSequence="+viewer.LastFrameSequence+" repeatedRenderLowerBound="+Math.Max(0,viewer.FrameCount-viewer.LastFrameSequence));
 Console.WriteLine("TIMING_PASS "+(closePending?"--send-close":sendOrder?"--send-order":"--perf"));result=0;
 }catch(Exception ex){Console.WriteLine("PERF_FAIL "+ex.Message);}finally{if(viewer!=null){await viewer.CloseSessionAsync();viewer.Close();}await media.StopAsync();form.Close();}}};Application.Run(form);}return result;
 }
 static int Slow(ControlPlaneClient c,string sid,string mode){int result=1;using(var form=new Form()){form.Width=240;form.Height=160;form.Shown+=async (s,e)=>{using(var media=new MediaSharingSession(c,sid,CaptureScope.ForOwnedWindow(form.Handle,Process.GetCurrentProcess().Id))){try{
 await media.StartAsync();var sw=Stopwatch.StartNew();
 if(mode=="--input-expired"||mode=="--input32"){var native=typeof(MediaSharingSession).Assembly.GetType("PeerDesk.Windows.NativeInput");var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;native.GetField("TestInputCountOnly",flags).SetValue(null,true);form.Activate();Console.WriteLine("INPUT_STARTED");while(sw.ElapsedMilliseconds<3500){int applies=(int)native.GetField("TestInputApplyCount",flags).GetValue(null);int misses=(int)typeof(MediaSharingSession).GetField("staleInputMisses",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(media);if(mode=="--input32"&&applies==32||mode=="--input-expired"&&misses==1){if(mode=="--input-expired"&&applies!=0)throw new Exception("expired batch applied");Console.WriteLine("INPUT_BATCH_PASS mode="+mode+" applied="+applies+" staleMisses="+misses);Console.WriteLine("TIMING_PASS "+mode);result=0;return;}if(!media.IsRunning)throw new Exception("input fixture stopped "+media.LastError);await Task.Delay(10);}throw new Exception("input batch timeout");}
 Func<int> count=()=>{var f=typeof(CaptureScope).GetField("TestCaptureDelayCalls",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);return f==null?0:(int)f.GetValue(null);};
 if(mode=="--capture-stop"||mode=="--capture-revoke"){while(count()==0&&sw.ElapsedMilliseconds<3000)await Task.Delay(5);if(count()==0)throw new Exception("capture not reached");
 if(mode=="--capture-stop"){media.CancelLocal();await media.StopAsync();}else Console.WriteLine("CAPTURE_STARTED");}
 if(mode=="--slow-fixed"){await Task.Delay(1100);if(!media.IsRunning||count()<2)throw new Exception("slow frames did not continue: "+media.LastError);}
 else {while(media.IsRunning&&sw.ElapsedMilliseconds<5000)await Task.Delay(20);if(media.IsRunning)throw new Exception("did not stop");
 if(mode=="--slow"&&media.LastError!="lease/capture guard expired")throw new Exception("baseline error mismatch "+media.LastError);
 if(mode=="--lease-stale"&&(count()!=0||media.LastError==null||!media.LastError.Contains("3 consecutive")))throw new Exception("stale bounded mismatch "+media.LastError);
 if(mode=="--lease-generation"&&(media.LastError==null||!media.LastError.Contains("generation changed")))throw new Exception("generation mismatch "+media.LastError);
 if(mode=="--capture-revoke"&&(media.LastError==null||!media.LastError.Contains("409")))throw new Exception("revoked mismatch "+media.LastError);
 }
 Console.WriteLine("TIMING_PASS "+mode+" error="+(media.LastError??"none"));result=0;
 }catch(Exception ex){Console.WriteLine("TIMING_FAIL "+mode+" "+ex.Message);}finally{await media.StopAsync();form.Close();}}};Application.Run(form);}return result;}
}
