using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
using PeerDesk.MediaAcceptance;
internal static class AutoAcceptance {
 static int checks;static string profileJson,join,output,owner;static bool measure,statefulTest;static MediaSharingSession observedSharing;
 static T F<T>(Control root,string name)where T:Control{return root.Controls.Find(name,true).OfType<T>().Single();}
 static void Check(bool yes,string text){if(!yes)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
 static async Task Until(Func<bool> yes,string why,int limit=12000){var w=Stopwatch.StartNew();while(!yes()){if(w.ElapsedMilliseconds>limit){if(observedSharing!=null){Console.WriteLine("DIAG_OBSERVED_SHARING running="+observedSharing.IsRunning+" error="+observedSharing.LastError);foreach(string n in new[]{"staleInputMisses","staleLeaseMisses","firstWorkerError"}){var f=typeof(MediaSharingSession).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic);Console.WriteLine("DIAG_OBSERVED_"+n+"="+f.GetValue(observedSharing));}}foreach(var main in Application.OpenForms.OfType<MainForm>())foreach(var field in typeof(MainForm).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Where(x=>x.FieldType==typeof(MediaSharingSession))){var share=field.GetValue(main) as MediaSharingSession;if(share!=null)Console.WriteLine("DIAG_TIMEOUT running="+share.IsRunning+" error="+share.LastError);}foreach(var viewer in Application.OpenForms.OfType<ViewerForm>())foreach(var field in typeof(ViewerForm).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Where(x=>new[]{"sequence","inputFailed","frameValid","statefulVersion"}.Contains(x.Name)))Console.WriteLine("DIAG_VIEWER "+field.Name+"="+field.GetValue(viewer));throw new TimeoutException(why);}await Task.Delay(25);}}
 static IEnumerable<SessionItem> Known(MainForm f){return (IEnumerable<SessionItem>)typeof(MainForm).GetField("known",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static bool Sharing(MainForm f){return (bool)typeof(MainForm).GetProperty("SharingIsRunning",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(f,null);}
 static long NextSequence(){var v=Application.OpenForms.OfType<ViewerForm>().Single();var f=typeof(ViewerForm).GetField("sequence",BindingFlags.Instance|BindingFlags.NonPublic);long n=(long)f.GetValue(v)+1;f.SetValue(v,n);return n;}
 static Form Consent(){return Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.GetType().Name=="ConnectionConsentForm");}
 static void Seed(string path,ControlPlaneClient c,ServerProfile p){new SettingsStore(path).Save(new SettingsState{ProfileJson=profileJson,IsRegistered=true,DeviceId=c.DeviceId,BearerToken=c.Token,TemporaryPassword=c.TemporaryPassword,PasswordExpiresAt=c.PasswordExpiresAt,BoundServerUrl=p.RendezvousUrl,BoundCertificateSha256=p.CertificateSha256});}
 static async Task Show(MainForm f){var ready=new TaskCompletionSource<bool>();f.Shown+=(s,e)=>ready.TrySetResult(true);f.Show();await ready.Task;await Until(()=>F<Button>(f,"RequestConnection").Enabled,"Registered UI ready");}
 static void Request(MainForm a,MainForm b,string password=null){F<TabControl>(a,"ServerSettingsTabs").SelectedIndex=0;F<TextBox>(a,"TargetDeviceId").Text=F<TextBox>(b,"MyDeviceId").Text;F<TextBox>(a,"TargetPassword").Text=password??"";F<Button>(a,"RequestConnection").PerformClick();}
 static async Task Close(MainForm f){if(f==null||f.IsDisposed)return;f.Close();await Until(()=>f.IsDisposed,"Owned form closes");}
 static async Task Run(OwnedFixture owned){
  var p=ServerProfile.Parse(profileJson);var ca=new ControlPlaneClient(p);var cb=new ControlPlaneClient(p);ca.Register(join);cb.Register(join);if(measure){var sp=System.Net.ServicePointManager.FindServicePoint(new Uri(p.RendezvousUrl));Console.WriteLine("PERF_TRANSPORT connections="+sp.ConnectionLimit+" expect100="+sp.Expect100Continue+" nagle="+sp.UseNagleAlgorithm);}
  string sa=Path.Combine(output,"a-settings.json"),sb=Path.Combine(output,"b-settings.json");Seed(sa,ca,p);Seed(sb,cb,p);
  var bootstrap=new DirectoryBootstrap(p.RendezvousUrl,p.CertificateSha256,"owned-auto-fixture");
  var a=new MainForm(Path.Combine(output,"a-history.json"),sa,bootstrap,CaptureScope.ForOwnedWindow(owned.Handle,Process.GetCurrentProcess().Id));
  var b=new MainForm(Path.Combine(output,"b-history.json"),sb,bootstrap,CaptureScope.ForOwnedWindow(owned.Handle,Process.GetCurrentProcess().Id));
  try{
   await Show(a);await Show(b);
   if(Environment.GetEnvironmentVariable("PEERDESK_TEST_ACCESS_AUTH")=="1"){await AccessAuth(a,b,ca,cb,sa,sb);return;}
   Request(a,b);await Until(()=>Consent()!=null,"Incoming consent appears without refresh");
   var prompt=Consent();using(var shot=new Bitmap(prompt.Width,prompt.Height)){prompt.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(output,"automatic-consent.png"),System.Drawing.Imaging.ImageFormat.Png);}Check(prompt.Owner==b,"Consent belongs to the actual target window");
   Check(F<TextBox>(prompt,"ConsentRequesterId").Text.Contains(ca.DeviceId),"Consent identifies the requesting PC");
   string words=F<Label>(prompt,"ConsentPermission").Text;
   Check(words.Contains("화면")&&words.Contains("키보드"),"Consent explicitly discloses screen and input permission");
   F<Button>(prompt,"RejectConnection").PerformClick();await Until(()=>prompt.IsDisposed,"Actual rejection closes prompt");
   await Until(()=>Known(a).Any(x=>x.Status=="Rejected"),"Initiator automatically sees rejection");
   Check(!Sharing(b)&&!Application.OpenForms.OfType<ViewerForm>().Any(),"Reject starts neither capture nor viewer");
   Request(a,b);await Until(()=>Consent()!=null,"Next request receives a fresh consent");
   prompt=Consent();F<Button>(prompt,"ApproveConnection").PerformClick();await Until(()=>prompt.IsDisposed,"Approval closes prompt");
   await Until(()=>Sharing(b),"One approval automatically starts target sharing");
   await Until(()=>Known(a).Any(x=>x.Status=="Connected"),"Initiator automatically connects after approval");
   await Until(()=>Application.OpenForms.OfType<ViewerForm>().Any(x=>x.FrameCount>0),"Viewer opens and renders without manual connect/share/view buttons");
   Check(Application.OpenForms.OfType<ViewerForm>().Count()==1,"Exactly one viewer opens");
   observedSharing=(MediaSharingSession)typeof(MainForm).GetField("sharing",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(b);
   var item=Known(a).Single(x=>x.Status=="Connected");var wire=new MediaWire(ca);
   // This receiver/native fixture uses synthetic sender calls; real mouse-event capture is covered by --mouse-events.
   Application.OpenForms.OfType<ViewerForm>().Single().Display.Enabled=false;
   var frame=await wire.GetFrameAsync(item.SessionId);if(frame!=null)File.WriteAllBytes(Path.Combine(output,"automatic-owned-frame.jpg"),frame.Jpeg);Check(frame!=null,"Real frame arrives over pinned HTTPS");
   using(var ms=new MemoryStream(frame.Jpeg))using(var image=new Bitmap(ms)){Check(image.Width==owned.ClientSize.Width&&image.Height==owned.ClientSize.Height,"Only the explicit owned test window is captured");}
   if(measure){var v=Application.OpenForms.OfType<ViewerForm>().Single();int first=v.FrameCount;long seq=v.LastFrameSequence;var watch=Stopwatch.StartNew();TimeSpan cpu=Process.GetCurrentProcess().TotalProcessorTime;await Task.Delay(5000);double seconds=watch.Elapsed.TotalSeconds;Console.WriteLine("PERF_OWNED seconds="+seconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" rendered="+(v.FrameCount-first)+" sequenceDelta="+(v.LastFrameSequence-seq)+" cpuMs="+(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds.ToString("F0",System.Globalization.CultureInfo.InvariantCulture)+" privateBytes="+Process.GetCurrentProcess().PrivateMemorySize64+" sampleJpegBytes="+frame.Jpeg.Length);}
   if(Environment.GetEnvironmentVariable("PEERDESK_TEST_NO_NATIVE")!="1"){
   owned.Activate();owned.Entry.Focus();await Until(()=>owned.Entry.Focused&&CaptureScope.ForOwnedWindow(owned.Handle,Process.GetCurrentProcess().Id).ForegroundMatches(),"Owned native Win32 foreground verified",3000);var inputWatch=Stopwatch.StartNew();
   await wire.SendInputAsync(item.SessionId,new InputCommand{Generation=frame.Generation,Sequence=NextSequence(),Kind="text",Text="q"});
   try{await Until(()=>owned.Entry.Text=="q","Actual native input after one-click consent");}catch{Console.WriteLine("DIAG_TEXT textLength="+owned.Entry.TextLength+" nativeChars="+owned.Entry.NativeChars+" activeOwned="+(Form.ActiveForm==owned)+" nativeForeground="+CaptureScope.ForOwnedWindow(owned.Handle,Process.GetCurrentProcess().Id).ForegroundMatches()+" entryFocused="+owned.Entry.Focused+" ctrl="+GetAsyncKeyState(17)+" shift="+GetAsyncKeyState(16)+" alt="+GetAsyncKeyState(18));throw;}
   Check(owned.Entry.NativeChars>0,"Native WM_CHAR reached the owned target");if(measure)Console.WriteLine("PERF_NATIVE_INPUT_MS="+inputWatch.ElapsedMilliseconds);
   await wire.SendInputAsync(item.SessionId,new InputCommand{Generation=frame.Generation,Sequence=NextSequence(),Kind="key",KeyCode=8});
   await Until(()=>owned.Entry.Text=="","Native backspace after one-click consent");
   Check(owned.Entry.NativeKeyDowns>0,"Native WM_KEYDOWN reached the owned target");
   var clickWatch=Stopwatch.StartNew();
   if(statefulTest){var viewer=Application.OpenForms.OfType<ViewerForm>().Single();Check(frame.StatefulInputVersion==1,"Target explicitly negotiated stateful input");var rect=(Rectangle)typeof(ViewerForm).GetMethod("ImageRect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(viewer,null);var point=new Point(rect.Left+330*rect.Width/owned.ClientSize.Width,rect.Top+225*rect.Height/owned.ClientSize.Height);var send=typeof(ViewerForm).GetMethod("SendPointer",BindingFlags.Instance|BindingFlags.NonPublic);send.Invoke(viewer,new object[]{"mouseDown",point,0,false});send.Invoke(viewer,new object[]{"mouseUp",point,0,true});Console.WriteLine("PERF_CLICK_PATH=actual Viewer ordered sender (synthetic event coordinates, owned foreground retained)");}
   else await wire.SendInputAsync(item.SessionId,new InputCommand{Generation=frame.Generation,Sequence=NextSequence(),Kind="click",X=(int)Math.Round(330*65535.0/(owned.ClientSize.Width-1)),Y=(int)Math.Round(225*65535.0/(owned.ClientSize.Height-1)),Button=0});
   try{await Until(()=>owned.Clicks==1,"Native mouse click after one-click consent");}catch{var fields=typeof(MainForm).GetFields(BindingFlags.Instance|BindingFlags.NonPublic);foreach(var f in fields.Where(x=>x.FieldType==typeof(MediaSharingSession))){var session=f.GetValue(b) as MediaSharingSession;if(session==null)continue;Console.WriteLine("DIAG_SHARING running="+session.IsRunning+" error="+session.LastError);foreach(string n in new[]{"staleInputMisses","staleLeaseMisses","generation","firstWorkerError"}){var sf=typeof(MediaSharingSession).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic);if(sf!=null)Console.WriteLine("DIAG_"+n+"="+sf.GetValue(session));}}Console.WriteLine("DIAG_OWNED containsFocus="+owned.ContainsFocus+" active="+(Form.ActiveForm==owned)+" clickCount="+owned.Clicks+" mouseDowns="+owned.Hit.NativeMouseDowns);throw;}
   Check(owned.Hit.NativeMouseDowns>0,"Native WM_LBUTTONDOWN reached the owned target");
   if(measure){long nativeMs=clickWatch.ElapsedMilliseconds;var v=Application.OpenForms.OfType<ViewerForm>().Single();await Until(()=>{var image=v.Display.Image as Bitmap;if(image==null)return false;var color=image.GetPixel(30,30);return Math.Abs(color.G-Color.LimeGreen.G)<30&&Math.Abs(color.R-Color.LimeGreen.R)<30;},"Clicked visual feedback renders");Console.WriteLine("PERF_CLICK_NATIVE_MS="+nativeMs+" VISUAL_MS="+clickWatch.ElapsedMilliseconds);}
   if(statefulTest)await StatefulNative(owned,wire,item.SessionId,frame.Generation);
   }
   bool peerClose=Environment.GetEnvironmentVariable("PEERDESK_TEST_PEER_CLOSE")=="1";
   if(peerClose)Application.OpenForms.OfType<ViewerForm>().Single().Close();else {F<TabControl>(b,"ServerSettingsTabs").SelectedIndex=1;F<Button>(b,"StopShare").PerformClick();}
   await Until(()=>!Sharing(b),"User sharing-stop terminates the worker");await Task.Delay(4500);
   Check(!Sharing(b),"Connected snapshots do not restart stopped sharing");
   if(peerClose){Console.WriteLine("PEER_END_DIAG reason="+observedSharing.EndReason+" error="+observedSharing.LastError);Check(observedSharing.EndReason=="session-ended"&&observedSharing.LastError==null,"Actual Viewer close ends target sharing without error");var endedLabel=(Label)typeof(MainForm).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(b);Check(!endedLabel.Text.Contains("오류"),"Actual target UI reports normal peer disconnect");}
   if(!peerClose&&Environment.GetEnvironmentVariable("PEERDESK_TEST_NO_NATIVE")=="1"){var endedViewer=Application.OpenForms.OfType<ViewerForm>().Single();await Until(()=>endedViewer.EndReason=="user-stopped","Actual user Stop reaches viewer as normal");Check(endedViewer.EndReason=="user-stopped","Actual MainForm Stop reaches Viewer normal end");var label=(Label)typeof(MainForm).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(b);Check(!label.Text.Contains("오류"),"Actual sharer stop does not show an error");}
   foreach(var v in Application.OpenForms.OfType<ViewerForm>().ToArray())v.Close();
   await Until(()=>!Application.OpenForms.OfType<ViewerForm>().Any(),"Viewer close terminates its session");
   await Task.Delay(2300);Check(!Application.OpenForms.OfType<ViewerForm>().Any(),"Closed viewer never reopens automatically");
   Console.WriteLine("AUTO_CONNECT_ACCEPTANCE_PASS count="+checks);
   Console.WriteLine("MEDIA_ACCEPTANCE_PASS count="+checks);
  }finally{await Close(a);await Close(b);if(owner!=null){await ca.OwnerRevokeDeviceAsync(owner,ca.DeviceId);await cb.OwnerRevokeDeviceAsync(owner,cb.DeviceId);owner=null;Console.WriteLine("OWNED_AWS_PROBE_DEVICES_REVOKED");}}
 }
 static async Task AccessAuth(MainForm a,MainForm b,ControlPlaneClient ca,ControlPlaneClient cb,string sa,string sb){
  var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  await Until(()=>{var e=typeof(MainForm).GetField("accessRuntimeEpoch",flags);return e!=null&&e.GetValue(b)!=null;},"Target runtime capability active");
  Check(F<CheckBox>(b,"PasswordAutoAccess").Checked,"Auto permission clearly enabled");
  string fixedPw="Owned-Fixed!"+Guid.NewGuid().ToString("N").Substring(0,12);
  F<TabControl>(b,"ServerSettingsTabs").SelectedTab=(TabPage)F<Control>(b,"AccessPolicyPage");
  await Until(()=>F<Button>(b,"SetFixedPassword").Enabled,"Fixed password editor ready");
  var tabs=F<TabControl>(b,"ServerSettingsTabs");var access=F<Control>(b,"AccessPolicyPage");
  Check(tabs.TabPages.IndexOf((TabPage)access)==2&&access.Parent==tabs,"Access settings is a real selectable main tab");
  tabs.SelectedTab=(TabPage)access;await Task.Delay(100);
  Check(tabs.SelectedTab==access&&access.Visible,"Access settings tab becomes visible");
  F<TextBox>(b,"FixedPassword").Text=fixedPw;
  Check(F<Button>(b,"SetFixedPassword").Visible&&F<Button>(b,"SetFixedPassword").Enabled,"Fixed editor action is available");
  F<Button>(b,"SetFixedPassword").PerformClick();await Task.Delay(400);
  using(var shot=new Bitmap(b.Width,b.Height)){b.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(output,"access-policy-ui.png"),System.Drawing.Imaging.ImageFormat.Png);}
  await Until(()=>F<Label>(b,"FixedPasswordState").Text.Contains("설정됨"),"Fixed password UI saved");
  Check((await cb.GetFixedPasswordStateAsync(System.Threading.CancellationToken.None)).Configured,"Fixed password actually configured on server");
  Check(!File.ReadAllText(sb).Contains(fixedPw),"Fixed password plaintext not written locally");
  using(var dismiss=new System.Windows.Forms.Timer{Interval=25}){dismiss.Tick+=(s,e)=>{IntPtr popup=GetLastActivePopup(a.Handle);uint pid;GetWindowThreadProcessId(popup,out pid);if(popup!=a.Handle&&popup!=IntPtr.Zero&&pid==(uint)Process.GetCurrentProcess().Id&&GetWindow(popup,4)==a.Handle)SendMessage(popup,0x10,IntPtr.Zero,IntPtr.Zero);};dismiss.Start();Request(a,b,"DefinitelyWrong!");}
  await Until(()=>F<Button>(a,"RequestConnection").Enabled,"Wrong request finishes");
  await Task.Delay(500);Check(Consent()==null&&!Sharing(b)&&!Known(b).Any(x=>x.Status=="Pending"),"Wrong supplied password creates no prompt or sharing");
  var status=(Label)typeof(MainForm).GetField("status",flags).GetValue(a);Check(status.Text.Contains("비밀번호"),"Wrong password UI explains password mismatch");
  foreach(string kind in new[]{"fixed","temporary"}){
   string password=kind=="fixed"?fixedPw:F<TextBox>(b,"TemporaryPassword").Text;
   Request(a,b,password);
   bool sawPrompt=false;var watch=Stopwatch.StartNew();
   while(!Application.OpenForms.OfType<ViewerForm>().Any(v=>v.FrameCount>0)){
    sawPrompt|=Consent()!=null;if(watch.ElapsedMilliseconds>12000)throw new TimeoutException(kind+" automatic frame");await Task.Delay(20);
   }
   Check(!sawPrompt,kind+" correct PW bypasses approval dialog");
   Check(Sharing(b)&&Known(a).Any(x=>x.Status=="Connected"),kind+" auto starts actual pinnedHTTPS screen sharing");
   foreach(var v in Application.OpenForms.OfType<ViewerForm>().ToArray())v.Close();
   await Until(()=>!Sharing(b)&&!Application.OpenForms.OfType<ViewerForm>().Any(),"Automatic session fully ends");
   await Until(()=>F<Button>(a,"RequestConnection").Enabled,"Request ready again");
  }
  Request(a,b,"");await Until(()=>Consent()!=null,"Blank PW manual approval appears");
  Check(!Sharing(b),"Blank PW does not start capture before approval");
  F<Button>(Consent(),"RejectConnection").PerformClick();
  await Until(()=>Consent()==null,"Blank manual prompt rejected");
  Console.WriteLine("ACCESS_AUTH_UI_ACCEPTANCE_PASS count="+checks);Console.WriteLine("MEDIA_ACCEPTANCE_PASS count="+checks);
 }
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetLastActivePopup(IntPtr h);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr h,uint c);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
 sealed class NativeProbe:NativeWindow,IDisposable{
  public int Downs,Ups,HeldMoves,Wheels,LastX,LastY;
  public NativeProbe(IntPtr hwnd){AssignHandle(hwnd);}
  protected override void WndProc(ref Message m){if(m.Msg==0x201)Downs++;if(m.Msg==0x202)Ups++;if(m.Msg==0x200&&(m.WParam.ToInt64()&1)!=0){HeldMoves++;LastX=(short)(m.LParam.ToInt64()&65535);LastY=(short)((m.LParam.ToInt64()>>16)&65535);}if(m.Msg==0x20a)Wheels++;base.WndProc(ref m);}
  public void Dispose(){ReleaseHandle();}
 }
 static async Task StatefulNative(OwnedFixture owned,MediaWire wire,string session,long gen){
  var viewer=Application.OpenForms.OfType<ViewerForm>().Single();
  await Until(()=>!(bool)typeof(ViewerForm).GetField("pumping",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(viewer),"Viewer sender drained");
  long seq=(long)typeof(ViewerForm).GetField("sequence",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(viewer);
  Func<InputCommand,Task> send=c=>{c.Generation=gen;c.Sequence=++seq;return wire.SendInputAsync(session,c);};
  Func<int,int> sx=x=>(int)Math.Round(x*65535.0/(owned.ClientSize.Width-1));
  Func<int,int> sy=y=>(int)Math.Round(y*65535.0/(owned.ClientSize.Height-1));
  owned.Activate();owned.Entry.Focus();await Until(()=>owned.ContainsFocus,"Native stateful target active");
   if(!Sharing(Application.OpenForms.OfType<MainForm>().Last()))throw new Exception("Stateful sharing ended before viewer keyboard test");
   owned.Activate();owned.Entry.Focus();owned.Entry.Text="viewer";owned.Entry.SelectionStart=6;
   var cmd=typeof(ViewerForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic);
   Action<int,bool,Keys> down=(vk,sys,mods)=>{var m=Message.Create(viewer.Handle,sys?0x104:0x100,(IntPtr)vk,IntPtr.Zero);Check((bool)cmd.Invoke(viewer,new object[]{m,(Keys)vk|mods}),"Viewer consumes VK down "+vk);};
   down(17,false,Keys.Control);await Until(()=>(GetAsyncKeyState(17)&0x8000)!=0,"Viewer sender Ctrl down native");
   down(65,false,Keys.Control);try{await Until(()=>owned.Entry.SelectionLength==6,"Viewer Ctrl+A native");}catch{Console.WriteLine("DIAG_COMBO selection="+owned.Entry.SelectionLength+" textLength="+owned.Entry.TextLength+" Ctrl="+GetAsyncKeyState(17)+" A="+GetAsyncKeyState(65)+" activeOwned="+(Form.ActiveForm==owned)+" focusedEntry="+owned.Entry.Focused);foreach(var f in typeof(ViewerForm).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Where(x=>new[]{"sequence","inputFailed","frameValid","statefulVersion","pumping","state"}.Contains(x.Name)))Console.WriteLine("DIAG_DIRECT "+f.Name+"="+(f.GetValue(viewer) is Control?((Control)f.GetValue(viewer)).Text:f.GetValue(viewer)));throw;}
   SendMessage(viewer.Handle,0x101,(IntPtr)65,IntPtr.Zero);SendMessage(viewer.Handle,0x101,(IntPtr)17,IntPtr.Zero);
   await Until(()=>(GetAsyncKeyState(17)&0x8000)==0&&(GetAsyncKeyState(65)&0x8000)==0,"Viewer key-up events release native combo");
   Check(owned.Entry.SelectionLength==6,"Actual Viewer keyboard sender Ctrl+A and KeyUp");
   down(18,true,Keys.Alt);await Until(()=>(GetAsyncKeyState(18)&0x8000)!=0,"Viewer SYSKEYDOWN Alt native");
   SendMessage(viewer.Handle,0x105,(IntPtr)18,IntPtr.Zero);
   await Until(()=>(GetAsyncKeyState(18)&0x8000)==0,"Viewer WM_SYSKEYUP releases native Alt");
   Check((GetAsyncKeyState(18)&0x8000)==0,"Viewer WM_SYSKEYUP route releases Alt");
   await Until(()=>!(bool)typeof(ViewerForm).GetField("pumping",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(viewer),"Viewer keyboard sender drained");
   seq=(long)typeof(ViewerForm).GetField("sequence",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(viewer);
   if(Environment.GetEnvironmentVariable("PEERDESK_TEST_KEYBOARD_ONLY")=="1")return;
  using(var probe=new NativeProbe(owned.Handle)){
   try{
    await send(new InputCommand{Kind="mouseDown",Button=0,X=sx(80),Y=sy(70)});
    await Until(()=>probe.Downs>0&&(GetAsyncKeyState(1)&0x8000)!=0,"Native left-down remains held");
    Check((GetAsyncKeyState(1)&0x8000)!=0,"Mouse down is not a tap");
    var hold=Stopwatch.StartNew();while(hold.ElapsedMilliseconds<900){await send(new InputCommand{Kind="holdHeartbeat"});await Task.Delay(150);}
    Check((GetAsyncKeyState(1)&0x8000)!=0,"Explicit heartbeats keep native hold beyond 750ms");
    await send(new InputCommand{Kind="move",X=sx(150),Y=sy(90)});
    await Until(()=>probe.HeldMoves>0&&Math.Abs(probe.LastX-150)<=2&&Math.Abs(probe.LastY-90)<=2,"Native drag moves while MK_LBUTTON held");
    Check(probe.HeldMoves>0,"Dragging delivers held WM_MOUSEMOVE");
    await send(new InputCommand{Kind="mouseUp",Button=0,X=sx(150),Y=sy(90)});
    await Until(()=>probe.Ups>0&&(GetAsyncKeyState(1)&0x8000)==0,"Native mouse up releases");
    Check((GetAsyncKeyState(1)&0x8000)==0,"Mouse release leaves no stuck button");
    await send(new InputCommand{Kind="wheel",X=sx(150),Y=sy(90),WheelDelta=-120});
    await Until(()=>probe.Wheels>0,"Native WM_MOUSEWHEEL reaches owned window");
    Check(probe.Wheels>0,"Native wheel delivered");
    owned.Entry.Text="abcd";owned.Entry.Focus();owned.Entry.SelectionStart=4;owned.Entry.SelectionLength=0;
    await send(new InputCommand{Kind="keyDown",KeyCode=17});
    await Until(()=>(GetAsyncKeyState(17)&0x8000)!=0,"Native Ctrl held");
    await send(new InputCommand{Kind="keyDown",KeyCode=65});
    await Until(()=>owned.Entry.SelectionLength==4,"Native Ctrl+A selects target text");
    await send(new InputCommand{Kind="keyUp",KeyCode=65});
    await send(new InputCommand{Kind="keyUp",KeyCode=17});
    await Until(()=>(GetAsyncKeyState(17)&0x8000)==0&&(GetAsyncKeyState(65)&0x8000)==0,"Ctrl and A released");
    Check(owned.Entry.SelectionLength==4,"Ctrl+A combination works");
    foreach(int vk in new[]{16,18}){
     await send(new InputCommand{Kind="keyDown",KeyCode=vk});await Until(()=>(GetAsyncKeyState(vk)&0x8000)!=0,"Native modifier held "+vk);
     await send(new InputCommand{Kind="keyUp",KeyCode=vk});await Until(()=>(GetAsyncKeyState(vk)&0x8000)==0,"Native modifier released "+vk);
     Check((GetAsyncKeyState(vk)&0x8000)==0,"Modifier down/up "+vk);
    }
    await send(new InputCommand{Kind="keyDown",KeyCode=16});await Until(()=>(GetAsyncKeyState(16)&0x8000)!=0,"Shift held before releaseAll");
    await send(new InputCommand{Kind="releaseAll"});await Until(()=>(GetAsyncKeyState(16)&0x8000)==0,"Native releaseAll cleanup");
    Check((GetAsyncKeyState(16)&0x8000)==0,"releaseAll releases native held modifier");
   }finally{
    try{await send(new InputCommand{Kind="releaseAll"});}catch{}
    typeof(ViewerForm).GetField("sequence",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(viewer,seq);
   }

  }
 }
 [STAThread]static int Main(string[] args){Console.OutputEncoding=new System.Text.UTF8Encoding(false);profileJson=Console.ReadLine();join=Console.ReadLine();statefulTest=args.Contains("--stateful")||Environment.GetEnvironmentVariable("PEERDESK_TEST_STATEFUL")=="1";measure=args.Contains("--measure")||Environment.GetEnvironmentVariable("PEERDESK_TEST_MEASURE")=="1";if(args.Skip(1).Contains("--aws"))owner=Console.ReadLine();output=args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"rb-auto-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int result=1;using(var owned=new OwnedFixture()){owned.Shown+=async(s,e)=>{try{await Run(owned);result=0;}catch(Exception ex){Console.Error.WriteLine("AUTO_CONNECT_ACCEPTANCE_FAIL "+ex.GetType().Name+": "+ex.Message+" "+ex.StackTrace);}finally{owned.Close();}};Application.Run(owned);}return result;}
}
