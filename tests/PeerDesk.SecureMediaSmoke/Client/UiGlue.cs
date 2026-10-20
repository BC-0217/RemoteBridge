using System;using System.Collections;using System.Collections.Generic;using System.IO;using System.Linq;using System.Reflection;using System.Text;using System.Threading;using System.Threading.Tasks;using System.Windows.Forms;using PeerDesk.Windows;
internal static class UiGlue {
 [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern IntPtr FindWindow(string cls,string title);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetDlgItem(IntPtr hwnd,int id);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetFocus();
 [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int GetClassName(IntPtr hwnd,StringBuilder className,int maxCount);
 // Large OWNED synthetic observer (MediaAcceptance OwnedFixture pattern): no desktop capture, no OS clipboard.
 sealed class NativeEntry:TextBox{public int Chars;protected override void WndProc(ref Message m){if(m.Msg==0x102)Chars++;base.WndProc(ref m);}}
 sealed class NativeButton:Button{public int Downs,Ups;protected override void WndProc(ref Message m){if(m.Msg==0x201)Downs++;if(m.Msg==0x202)Ups++;base.WndProc(ref m);}}
 sealed class NativeFixture:Form{
  public readonly NativeEntry Entry=new NativeEntry();public readonly NativeButton Hit=new NativeButton();public int Clicks,Downs,HeldMoves,Ups,LastX,LastY;readonly List<System.Drawing.Point> trail=new List<System.Drawing.Point>();
  public NativeFixture(){Text="RemoteBridge OWNED native secure input fixture";StartPosition=FormStartPosition.Manual;Location=new System.Drawing.Point(60,60);ClientSize=new System.Drawing.Size(960,600);BackColor=System.Drawing.Color.White;ShowInTaskbar=false;DoubleBuffered=true;
   Entry.SetBounds(20,20,300,30);Hit.SetBounds(360,15,220,45);Hit.Text="OWNED CLICK TARGET";Hit.Click+=(s,e)=>{Clicks++;Invalidate();};Controls.Add(Entry);Controls.Add(Hit);
   Controls.Add(new Label{Text="Test window only. Drag area below. No private desktop is captured.",AutoSize=true,Location=new System.Drawing.Point(20,70)});}
  protected override bool ShowWithoutActivation {get{return LateOnly||LateDiagnostic;}}
  protected override void WndProc(ref Message m){
   if(m.Msg==0x201||m.Msg==0x200||m.Msg==0x202){long l=m.LParam.ToInt64();int x=(short)(l&0xFFFF),y=(short)((l>>16)&0xFFFF);
    if(m.Msg==0x201){Downs++;LastX=x;LastY=y;trail.Add(new System.Drawing.Point(x,y));Invalidate();}
    else if(m.Msg==0x200&&(m.WParam.ToInt64()&1)!=0){HeldMoves++;LastX=x;LastY=y;trail.Add(new System.Drawing.Point(x,y));Invalidate();}
    else if(m.Msg==0x202){Ups++;LastX=x;LastY=y;Invalidate();}}
   base.WndProc(ref m);}
  protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var b=new System.Drawing.SolidBrush(Clicks==0?System.Drawing.Color.DeepSkyBlue:System.Drawing.Color.LimeGreen))e.Graphics.FillRectangle(b,620,15,120,45);
   if(trail.Count>1)using(var p=new System.Drawing.Pen(System.Drawing.Color.OrangeRed,4))e.Graphics.DrawLines(p,trail.ToArray());}
 }
 static bool NativeMode{get{return Environment.GetEnvironmentVariable("PEERDESK_SECURE_NATIVE_CAPTURE")=="1";}}
 static bool CaptureOnly{get{return Environment.GetEnvironmentVariable("PEERDESK_SECURE_AWS_CAPTURE_ONLY")=="1";}}
 static bool LateOnly{get{return Environment.GetEnvironmentVariable("PEERDESK_SECURE_LATE_ONLY")=="1";}}
 static bool LateDiagnostic{get{return Environment.GetEnvironmentVariable("PEERDESK_SECURE_LATE_DIAGNOSTIC")=="1"||Environment.GetEnvironmentVariable("PEERDESK_SECURE_LATE_DIAGNOSTIC_LONG")=="1";}}
 static bool LateDiagnosticLong{get{return Environment.GetEnvironmentVariable("PEERDESK_SECURE_LATE_DIAGNOSTIC_LONG")=="1";}}
 static int StreamSeconds{get{return LateOnly||Environment.GetEnvironmentVariable("PEERDESK_SECURE_LONG_STREAM")=="1"?40:5;}}
 static void ObserveForeground(string point,NativeFixture fx){
  IntPtr target=fx.Handle,foreground=GetForegroundWindow();uint targetPid,foregroundPid;
  GetWindowThreadProcessId(target,out targetPid);GetWindowThreadProcessId(foreground,out foregroundPid);
  var className=new StringBuilder(128);int classLength=foreground==IntPtr.Zero?0:GetClassName(foreground,className,className.Capacity);
  Console.WriteLine("NATIVE_FOCUS_OBSERVER point="+point+" targetHwnd=0x"+target.ToInt64().ToString("X")+" targetPid="+targetPid+" foregroundHwnd=0x"+foreground.ToInt64().ToString("X")+" foregroundPid="+foregroundPid+" foregroundClass="+(classLength>0?className.ToString():"<none>"));
 }
 static void RequireOwnedForeground(NativeFixture fx){
  uint pid;GetWindowThreadProcessId(fx.Handle,out pid);
  if(pid!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id){ObserveForeground("owned-pid-check-failure",fx);throw new InvalidOperationException("NATIVE_ABORT owned fixture HWND not in this process; not claiming PASS");}
  if(GetForegroundWindow()!=fx.Handle){ObserveForeground("foreground-check-failure",fx);throw new InvalidOperationException("NATIVE_ABORT foreground mismatch; cause unknown; not claiming PASS and not re-stealing focus");}
 }
 static async Task Until(Func<bool> done,string what,NativeFixture fx,int ms=5000){
  var t=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(fx!=null)RequireOwnedForeground(fx);if(t.ElapsedMilliseconds>ms)throw new TimeoutException(what);await Task.Delay(20);}
  if(fx!=null)RequireOwnedForeground(fx);
 }
 static int TargetGuardHeldCount(MediaSharingSession sharing){
  var guard=Get(sharing,"guard");if(guard==null)return -1;
  return (int)guard.GetType().GetProperty("HeldCount",F|BindingFlags.Public).GetValue(guard);
 }
 static string Safe(Func<string> value){try{return value();}catch{return "unknown";}}
 static object TryMember(object o,string name){
  try{if(o==null)return null;var t=o.GetType();var p=t.GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(p!=null)return p.GetValue(o,null);var f=t.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);return f==null?null:f.GetValue(o);}catch{return null;}
 }
 static string Val(object o,string name){var v=TryMember(o,name);return v==null?"unknown":v is Exception?((Exception)v).Message:v.ToString();}
 static void DiagnosticSnapshot(string stage,MediaSharingSession sharing,SecureSessionAdapter sender,NativeFixture fx,long manualSeq,int charsBefore){
  object target=null,guard=null,wire=null;
  try{wire=typeof(MediaSharingSession).GetField("wire",F)?.GetValue(sharing);target=TryMember(wire,"Secure");guard=TryMember(sharing,"guard");}catch{}
  IntPtr focus=IntPtr.Zero;try{focus=GetFocus();}catch{}
  string entryHwnd=Safe(()=> "0x"+fx.Entry.Handle.ToInt64().ToString("X"));
  string targetSeq=Val(target,"lastInputSequence");
  string senderSeq=Safe(()=>sender==null?"unknown":sender.GetType().GetField("lastInputSequence",F)?.GetValue(sender)?.ToString()??"unknown");
  Console.WriteLine("NATIVE_LATE_DIAGNOSTIC stage="+stage+
   " entryFocused="+Safe(()=>fx.Entry.Focused.ToString())+" containsFocus="+Safe(()=>fx.ContainsFocus.ToString())+
   " activeControlIsEntry="+Safe(()=>Object.ReferenceEquals(fx.ActiveControl,fx.Entry).ToString())+
   " getFocusHwnd=0x"+focus.ToInt64().ToString("X")+" entryHwnd="+entryHwnd+
   " charsBefore="+charsBefore+" charsNow="+Safe(()=>fx.Entry.Chars.ToString())+" textLength="+Safe(()=>fx.Entry.Text.Length.ToString())+
   " senderSequence="+manualSeq+" senderLastInputSequence="+senderSeq+
   " targetAdapterReady="+Val(target,"Ready")+" targetLastInputSequence="+targetSeq+" targetVsManual="+(targetSeq=="unknown"?"unknown":targetSeq+"/"+manualSeq)+
   " targetFailure="+Val(target,"Failure")+" targetPendingInputBudgetMs="+Safe(()=>{var m=target?.GetType().GetMethod("PendingInputBudgetMs",F|BindingFlags.Public);return m==null?"unknown":m.Invoke(target,null)?.ToString()??"unknown";})+
   " sharingRunning="+Safe(()=>sharing.IsRunning.ToString())+" sharingLastError="+Safe(()=>sharing.LastError??"none")+
   " sharingEndReason="+Safe(()=>sharing.EndReason??"none")+" lastInputLeaseResponseMs="+Safe(()=>sharing.LastInputLeaseResponseMs.ToString())+
   " staleInputMisses="+Val(sharing,"staleInputMisses")+" staleLeaseMisses="+Val(sharing,"staleLeaseMisses")+
   " guardFailed="+Val(guard,"Failed")+" guardFailure="+Val(guard,"Failure")+" guardHeldCount="+Val(guard,"HeldCount"));
 }
 static async Task VerifyNative(MediaSharingSession sharing,SecureSessionAdapter adapter,NativeFixture fx,Action<bool,string> check,string serverUrl){
  bool lateDiagnostic=LateDiagnostic;bool lateOnly=LateOnly||lateDiagnostic;
  if(lateOnly)ObserveForeground("late-only-before-stream-no-target-activation",fx);
  else {
   // Activate exactly once; never retry stealing foreground from the user.
   fx.Activate();
   try{await Until(()=>GetForegroundWindow()==fx.Handle,"owned fixture became foreground",null,3000);}catch(TimeoutException){throw new InvalidOperationException("NATIVE_ABORT owned fixture could not become foreground once; not claiming PASS");}
   RequireOwnedForeground(fx);ObserveForeground("initial-activation-complete",fx);
  }
  sharing.AllowRemoteInput=true;sharing.RequestStatefulInput=true;
  long seq=0;FramePacket first=null;long gen=0;Func<InputCommand,Task> send=null;
  try{
   await sharing.StartAsync(CancellationToken.None);first=await adapter.GetFrameAsync(CancellationToken.None,0,0);
   check(sharing.IsRunning&&sharing.StatefulInputActive&&first.Width>0&&first.Height>0&&first.Jpeg.Length>0&&sharing.LastError==null&&(!lateOnly||TargetGuardHeldCount(sharing)==0),lateOnly?"late-only mode: encrypted first frame, running and held input zero before delayed activation":"native mode: actual MainForm sharing captures large owned fixture with remote input enabled and delivers encrypted frame");
   gen=first.Generation;
   Func<int,int> sx=x=>(int)Math.Round(x*65535.0/(fx.ClientSize.Width-1));Func<int,int> sy=y=>(int)Math.Round(y*65535.0/(fx.ClientSize.Height-1));
   send=c=>{RequireOwnedForeground(fx);c.Generation=gen;c.Sequence=++seq;return adapter.SendInputAsync(c,CancellationToken.None);};
   if(!lateOnly){
    fx.Entry.Focus();fx.Entry.Text="";ObserveForeground("before-first-text-send",fx);
    await send(new InputCommand{Kind="text",Text="q"});
    await Until(()=>fx.Entry.Text=="q","native text reaches owned textbox",fx);check(fx.Entry.Text=="q"&&fx.Entry.Chars>0,"native mode: E2EE text input delivered as native WM_CHAR to owned textbox");
    await send(new InputCommand{Kind="mouseDown",Button=0,X=sx(200),Y=sy(300)});
    await Until(()=>fx.Downs>0,"native WM_LBUTTONDOWN on owned drag area",fx);
    await send(new InputCommand{Kind="move",X=sx(480),Y=sy(420)});
    await Until(()=>fx.HeldMoves>0&&Math.Abs(fx.LastX-480)<=3&&Math.Abs(fx.LastY-420)<=3,"native held WM_MOUSEMOVE with MK_LBUTTON",fx);
    int dragUpsBefore=fx.Ups;
    await send(new InputCommand{Kind="mouseUp",Button=0,X=sx(480),Y=sy(420)});
    await Until(()=>fx.Ups>dragUpsBefore&&TargetGuardHeldCount(sharing)==0,"native WM_LBUTTONUP consumed and target guard released",fx);
    check(fx.Downs>0&&fx.HeldMoves>0&&fx.Ups>dragUpsBefore,"native mode: E2EE mouseDown/held move/mouseUp drag observed and released on owned window");
    await send(new InputCommand{Kind="releaseAll"});
    await Until(()=>TargetGuardHeldCount(sharing)==0,"target guard clear before click",fx);
    int clicksBefore=fx.Clicks;
    await send(new InputCommand{Kind="click",Button=0,X=sx(470),Y=sy(37)});
    await Until(()=>fx.Clicks>clicksBefore&&fx.Hit.Downs>0&&fx.Hit.Ups>0,"native button click",fx);
    check(fx.Clicks>clicksBefore&&fx.Hit.Downs>0&&fx.Hit.Ups>0,"native mode: E2EE click produces native WM_LBUTTONDOWN/UP and Button.Click on owned button");
    await send(new InputCommand{Kind="releaseAll"});
    await Until(()=>TargetGuardHeldCount(sharing)==0,"target guard clear after click before stream",fx);
   }
  }catch{
   ObserveForeground("native-input-failure",fx);
   Console.Error.WriteLine("NATIVE_DIAGNOSTIC running="+sharing.IsRunning+" lastError="+(sharing.LastError??"none")+" end="+(sharing.EndReason??"none")+" text="+fx.Entry.Text.Length+" downs="+fx.Downs+" heldMoves="+fx.HeldMoves+" ups="+fx.Ups+" clicks="+fx.Clicks+" fgOwned="+(GetForegroundWindow()==fx.Handle));
   if(!lateOnly&&first!=null&&sharing.IsRunning)try{await adapter.SendInputAsync(new InputCommand{Generation=first.Generation,Sequence=++seq,Kind="releaseAll"},CancellationToken.None);}catch{}
   throw;
  }
  // Fixed-duration encrypted stream: unique, strictly increasing sequences while sharing stays healthy.
  int seconds=lateDiagnostic?(LateDiagnosticLong?40:5):StreamSeconds;var clock=System.Diagnostics.Stopwatch.StartNew();long lastGen=first.Generation,lastSeq=first.Sequence;int unique=1;
  while(clock.Elapsed.TotalSeconds<seconds){
   if(!sharing.IsRunning||sharing.LastError!=null)throw new InvalidOperationException("NATIVE_STREAM sharing stopped at "+clock.Elapsed.TotalSeconds.ToString("0.0")+"s lastError="+(sharing.LastError??"none")+" end="+(sharing.EndReason??"none"));
   var f=await adapter.GetFrameAsync(CancellationToken.None,lastGen,lastSeq);
   if(f.Generation!=lastGen)throw new InvalidDataException("NATIVE_STREAM generation changed mid-stream");
   if(f.Sequence<lastSeq)throw new InvalidDataException("NATIVE_STREAM sequence went backwards "+lastSeq+"->"+f.Sequence);
   if(f.Sequence>lastSeq){unique++;lastSeq=f.Sequence;}
   if(lateOnly&&TargetGuardHeldCount(sharing)!=0)throw new InvalidOperationException("NATIVE_LATE_ONLY held input became nonzero during no-input stream");
   await Task.Delay(100);
  }
  Console.WriteLine(lateDiagnostic?"NATIVE_LATE_DIAGNOSTIC encryptedFrames seconds="+seconds+" uniqueFrames="+unique+" lastSeq="+lastSeq+" 40sCaptureObserved="+(seconds==40):"NATIVE_STREAM seconds="+seconds+" uniqueFrames="+unique+" lastSeq="+lastSeq);
  check(unique>=Math.Max(3,seconds)&&sharing.IsRunning&&sharing.LastError==null&&(!lateOnly||TargetGuardHeldCount(sharing)==0),lateDiagnostic?"late diagnostic: encrypted "+seconds+"s capture health only; not a 40s gate PASS":"native mode: encrypted frames for fixed "+seconds+"s with unique increasing sequence, sharing running, no LastError");
  if(lateOnly){
   int diagnosticCharsBefore=-1;
   try{
    fx.Activate();
    try{await Until(()=>GetForegroundWindow()==fx.Handle,"late-only fixture became foreground on its single activation",null,3000);}catch(TimeoutException){throw new InvalidOperationException("NATIVE_ABORT late-only single activation did not foreground target; no retry");}
    RequireOwnedForeground(fx);ObserveForeground("late-only-one-time-activation-confirmed",fx);
    bool focusResult=fx.Entry.Focus();fx.Entry.Text="";int charsBefore=fx.Entry.Chars;diagnosticCharsBefore=charsBefore;long seqBefore=seq;ObserveForeground("late-only-before-first-text-send",fx);
    if(lateDiagnostic)Console.WriteLine("NATIVE_LATE_DIAGNOSTIC focusReturn="+focusResult+" entryFocused="+Safe(()=>fx.Entry.Focused.ToString())+" containsFocus="+Safe(()=>fx.ContainsFocus.ToString())+" activeControlIsEntry="+Safe(()=>Object.ReferenceEquals(fx.ActiveControl,fx.Entry).ToString()));
    if(lateDiagnostic)DiagnosticSnapshot("before-send",sharing,adapter,fx,seqBefore,charsBefore);
    await send(new InputCommand{Kind="text",Text="q"});
    await Until(()=>fx.Entry.Text=="q"&&fx.Entry.Chars==charsBefore+1,"late-only single WM_CHAR reaches owned textbox",fx);
    if(lateDiagnostic)DiagnosticSnapshot("after-one-WM_CHAR",sharing,adapter,fx,seq,charsBefore);
    check(fx.Entry.Text=="q"&&fx.Entry.Chars==charsBefore+1,lateDiagnostic?"late diagnostic: exactly one new WM_CHAR observed":"late-only E2EE text produces exactly one new WM_CHAR");
    await send(new InputCommand{Kind="releaseAll"});
    await Until(()=>TargetGuardHeldCount(sharing)==0,"late-only release leaves target held input zero",fx);
    check(TargetGuardHeldCount(sharing)==0,"late-only release verified held count zero");
   }catch(Exception ex){
    ObserveForeground("late-only-failure",fx);
    if(lateDiagnostic)DiagnosticSnapshot("failure-before-cleanup type="+ex.GetType().Name,sharing,adapter,fx,seq,diagnosticCharsBefore);
    try{await sharing.StopAsync();}catch{}
    throw;
   }
   return;
  }
  // Late-input regression: first input passing must not hide later input loss after the long stream.
  try{
   RequireOwnedForeground(fx);
   fx.Entry.Focus();fx.Entry.Text="";int charsBefore=fx.Entry.Chars;
   await send(new InputCommand{Kind="text",Text="z"});
   await Until(()=>fx.Entry.Chars>charsBefore&&fx.Entry.Text=="z","late native text reaches owned textbox after stream",fx);
   Console.WriteLine("NATIVE_LATE_INPUT PASS charsBefore="+charsBefore+" charsAfter="+fx.Entry.Chars+" seq="+seq+" gen="+gen);
   check(fx.Entry.Chars>charsBefore&&fx.Entry.Text=="z","native mode: late E2EE text input after fixed stream delivered as new native WM_CHAR to owned textbox");
   await send(new InputCommand{Kind="releaseAll"});
   await Until(()=>TargetGuardHeldCount(sharing)==0,"target guard clear after late input before viewer close",fx);
  }catch{
   Console.Error.WriteLine("NATIVE_LATE_INPUT FAIL running="+sharing.IsRunning+" lastError="+(sharing.LastError??"none")+" end="+(sharing.EndReason??"none")+" text="+fx.Entry.Text.Length+" chars="+fx.Entry.Chars+" fgOwned="+(GetForegroundWindow()==fx.Handle));
   if(sharing.IsRunning)try{await adapter.SendInputAsync(new InputCommand{Generation=gen,Sequence=++seq,Kind="releaseAll"},CancellationToken.None);}catch{}
   throw;
  }
 }
 const BindingFlags F=BindingFlags.NonPublic|BindingFlags.Instance;
 static object Get(object o,string name){return o.GetType().GetField(name,F).GetValue(o);}
 static void Set(object o,string name,object value){o.GetType().GetField(name,F).SetValue(o,value);}
 static object Call(object o,string name,params object[] values){return o.GetType().GetMethod(name,F).Invoke(o,values);}
 static byte[] Packet(byte kind,string id,Action<BinaryWriter> write=null){using(var s=new MemoryStream())using(var w=new BinaryWriter(s,new UTF8Encoding(false,true))){w.Write(kind);w.Write(id);if(write!=null)write(w);return s.ToArray();}}
 static async Task VerifyCapture(MediaSharingSession sharing,SecureSessionAdapter adapter,Action<bool,string> check){
  try{sharing.AllowRemoteInput=false;await sharing.StartAsync(CancellationToken.None);var captured=await adapter.GetFrameAsync(CancellationToken.None,0,0);
   check(sharing.IsRunning&&sharing.StatefulInputActive&&captured.Width>0&&captured.Height>0&&captured.Jpeg.Length>0,"actual prepared MainForm sharing starts owned-HWND capture and delivers encrypted frame with native input disabled");
   Console.WriteLine("OWNED_CAPTURE_TIMING encodeMs="+sharing.LastCaptureEncodeMs.ToString("0.0")+" rawCaptureMs="+sharing.LastRawCaptureMs.ToString("0.0")+" encodeOnlyMs="+sharing.LastEncodeOnlyMs.ToString("0.0")+" captureStartProofAgeMs="+sharing.CaptureStartProofAgeMs.ToString("0.0")+" captureEndProofAgeMs="+sharing.CaptureEndProofAgeMs.ToString("0.0"));
  }catch{Console.Error.WriteLine("OWNED_CAPTURE_DIAGNOSTIC running="+sharing.IsRunning+" lastError="+(sharing.LastError??"none")+" end="+(sharing.EndReason??"none")+" encodeMs="+sharing.LastCaptureEncodeMs.ToString("0.0")+" rawCaptureMs="+sharing.LastRawCaptureMs.ToString("0.0")+" encodeOnlyMs="+sharing.LastEncodeOnlyMs.ToString("0.0")+" captureStartProofAgeMs="+sharing.CaptureStartProofAgeMs.ToString("0.0")+" captureEndProofAgeMs="+sharing.CaptureEndProofAgeMs.ToString("0.0")+" inputLeaseResponseMs="+sharing.LastInputLeaseResponseMs+" qualityLevel="+sharing.CurrentQuality.Level);throw;}
 }
 internal static async Task Run(ControlPlaneClient a,ControlPlaneClient b,string sid,Action<bool,string> check){
  string root=Path.Combine(Path.GetTempPath(),"RemoteBridge.UiGlue."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Directory.CreateDirectory(Path.Combine(root,"a"));Directory.CreateDirectory(Path.Combine(root,"b"));
  try{
   using(var af=new MainForm(Path.Combine(root,"a","history.json"),Path.Combine(root,"a","settings.json"),null))
   using(var bf=new MainForm(Path.Combine(root,"b","history.json"),Path.Combine(root,"b","settings.json"),null))
   using(var ownedCapture=new Form{Text="RemoteBridge owned synthetic capture",ClientSize=new System.Drawing.Size(320,180),BackColor=System.Drawing.Color.CornflowerBlue,ShowInTaskbar=false})
   using(var native=NativeMode?new NativeFixture():null){
    if(NativeMode&&!CaptureOnly)throw new InvalidOperationException("PEERDESK_SECURE_NATIVE_CAPTURE=1 requires PEERDESK_SECURE_AWS_CAPTURE_ONLY=1.");
    var ah=af.Handle;var bh=bf.Handle;bf.Show();ownedCapture.Show();if(native!=null){native.Show();if(LateOnly||LateDiagnostic){ObserveForeground("late-only-visible-without-activation",native);if(GetForegroundWindow()==native.Handle)throw new InvalidOperationException("NATIVE_LATE_ONLY target unexpectedly foreground before permitted activation");}}
    foreach(var form in new[]{af,bf})foreach(var name in new[]{"passwordTimer","snapshotTimer","presenceTimer"})((System.Windows.Forms.Timer)Get(form,name)).Stop();
    foreach(var form in new[]{af,bf}){((CheckBox)form.Controls.Find("AllowSessionFiles",true).Single()).Checked=true;}
    Set(af,"client",a);Set(bf,"client",b);Set(af,"registrationVerified",true);Set(bf,"registrationVerified",true);
    var row=new SessionItem{SessionId=sid,InitiatorDeviceId=a.DeviceId,TargetDeviceId=b.DeviceId,Status="Connected"};
    ((List<SessionItem>)Get(af,"known")).Add(row);((List<SessionItem>)Get(bf,"known")).Add(row);
    var ai=(SecurePeerIdentity)Call(af,"RequireDeviceIdentity");var bi=(SecurePeerIdentity)Call(bf,"RequireDeviceIdentity");
    ((PeerTrustStore)Get(af,"peerTrust")).Trust(a.ServerUrl,a.CertificateSha256,b.DeviceId,bi.Fingerprint);
    ((PeerTrustStore)Get(bf,"peerTrust")).Trust(b.ServerUrl,b.CertificateSha256,a.DeviceId,ai.Fingerprint);
    using(var sharing=new MediaSharingSession(b,sid,CaptureScope.ForOwnedWindow(native!=null?native.Handle:Environment.GetEnvironmentVariable("PEERDESK_TEST_COMPLEX_CAPTURE")=="1"?bh:ownedCapture.Handle,System.Diagnostics.Process.GetCurrentProcess().Id)){RequestStatefulInput=true}){
     var target=(Task)Call(bf,"PrepareSecureTargetAsync",sharing,b,sid,CancellationToken.None);
     var viewer=(Task<ViewerForm>)Call(af,"MakeSecureViewerAsync",a,sid,CancellationToken.None,new Func<bool>(()=>true));
     await Task.WhenAll(target,viewer);using(var view=viewer.Result){
      check(!sharing.IsRunning&&view.SendSelectedFile!=null&&view.SendClipboardText!=null,"actual MainForm target/viewer E2EE preparation with persisted independent peer trust and no capture");
      var ax=(SecureSessionAdapter)((IDictionary)Get(af,"secureChannels"))[sid];var bx=(SecureSessionAdapter)((IDictionary)Get(bf,"secureChannels"))[sid];
      check(ax.Ready&&bx.Ready&&!ax.HasDirectScreen&&!bx.HasDirectScreen,"actual MainForm default relay negotiation and feature setup");
      if(native!=null){
       await VerifyNative(sharing,ax,native,check,b.ServerUrl);
       view.Show();view.Close();check(view.UserClosed&&view.UserIntentCancellation.IsCancellationRequested,"actual user viewer close immediately tombstones and cancels reconnect intent");
       var endWait=System.Diagnostics.Stopwatch.StartNew();while(sharing.IsRunning&&endWait.ElapsedMilliseconds<15000)await Task.Delay(50);
       Console.WriteLine("NATIVE_END running="+sharing.IsRunning+" end="+(sharing.EndReason??"none")+" lastError="+(sharing.LastError??"none"));
       // Typed normal end only; a null EndReason (generic EOF/error) is never treated as normal.
       check(!sharing.IsRunning&&(sharing.EndReason=="session-ended"||sharing.EndReason=="user-stopped")&&sharing.LastError==null,"native mode: actual viewer close ends target sharing with typed normal EndReason and no LastError");
       return;
      }
      if(CaptureOnly){await VerifyCapture(sharing,ax,check);view.Show();view.Close();check(view.UserClosed&&view.UserIntentCancellation.IsCancellationRequested,"actual user viewer close immediately tombstones and cancels reconnect intent");return;}
      var fa=((IDictionary)Get(af,"featureStates"))[sid];var fb=((IDictionary)Get(bf,"featureStates"))[sid];
      string id=Guid.NewGuid().ToString("N"),destination=Path.Combine(root,"received.txt");var bytes=Encoding.UTF8.GetBytes("owned synthetic file");string hash=SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(bytes));
      var identity=(TransferIdentity)Call(bf,"FeatureIdentity",fb,id);
      bool consent=true;var receiver=new FileTransferCoordinator(root,x=>x.Equals(identity)&&bx.Ready,x=>{bool yes=consent;consent=false;return yes;});
      receiver.AcceptIncoming(identity,"original.txt",bytes.Length,hash);
      var incomingType=typeof(MainForm).GetNestedType("IncomingFile",BindingFlags.NonPublic);var incoming=Activator.CreateInstance(incomingType,true);
      Set(incoming,"Receiver",receiver);Set(incoming,"Identity",identity);Set(incoming,"Destination",destination);Set(incoming,"DisplayName","original.txt");Set(incoming,"Sha256",hash);
      ((IDictionary)Get(fb,"Incoming")).Add(id,incoming);
      Func<byte[],Task<long>> request=packet=>(Task<long>)Call(af,"FileRequest",fa,id,packet,CancellationToken.None);
      long offset=await request(Packet(2,id,w=>{w.Write(0L);w.Write(hash);w.Write(bytes.Length);w.Write(bytes);}));
      check(offset==bytes.Length,"actual encrypted MainForm file chunk persisted before ACK");
      await ax.SendAsync(4,Packet(4,id),CancellationToken.None);
      using(var consentTimer=new System.Windows.Forms.Timer{Interval=10}){
       bool clicked=false;consentTimer.Tick+=(s,e)=>{var dialog=FindWindow("#32770","파일 이어받기");uint pid;if(dialog==IntPtr.Zero)return;GetWindowThreadProcessId(dialog,out pid);if(pid!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id)return;var yes=GetDlgItem(dialog,6);if(yes!=IntPtr.Zero){clicked=true;SendMessage(yes,0x00F5,IntPtr.Zero,IntPtr.Zero);}};
       consentTimer.Start();long resumed=await request(Packet(5,id,w=>{w.Write((long)bytes.Length);w.Write(hash);}));consentTimer.Stop();check(clicked&&resumed==bytes.Length,"actual file resume asks fresh UI consent and preserves original offer hash/name after destination rename");
      }
      long done=await request(Packet(3,id));check(done==bytes.Length&&File.ReadAllBytes(destination).SequenceEqual(bytes),"actual encrypted MainForm completion rename and full hash ACK");
      check(await request(Packet(3,id))==bytes.Length,"completed transfer ACK idempotent without writing another file");
      bool denied=false;try{await request(Packet(1,id));}catch(IOException){denied=true;}check(denied,"duplicate completed offer rejected before consent dialog or filesystem creation");
      var pending=(Task<long>)Call(af,"FileRequest",fa,Guid.NewGuid().ToString("N"),Packet(4,Guid.NewGuid().ToString("N")),CancellationToken.None);
      Call(af,"CloseFeatureChannel",sid);bool cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}
      check(cancelled,"session stop cancels outstanding file ACK wait");
      await VerifyCapture(sharing,ax,check);
      view.Show();view.Close();check(view.UserClosed&&view.UserIntentCancellation.IsCancellationRequested,"actual user viewer close immediately tombstones and cancels reconnect intent");
     }
    }
   }
  }finally{Directory.Delete(root,true);}
 }
}
