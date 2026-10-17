using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
namespace PeerDesk.MediaAcceptance
{
 internal static class Program {
  [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
  static int passed;static string profileJson,invite,outputDir;
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
  static async Task Until(Func<bool> condition,string name,int ms=5000){var sw=Stopwatch.StartNew();while(!condition()){if(sw.ElapsedMilliseconds>ms)throw new TimeoutException(name);await Task.Delay(20);}}
  static async Task FrameDenied(MediaWire wire,string id,string name){
   try{await wire.GetFrameAsync(id);}catch(Exception ex){
    for(Exception e=ex;e!=null;e=e.InnerException){var w=e as WebException;var r=w==null?null:w.Response as HttpWebResponse;if(r!=null && ((int)r.StatusCode==409||(int)r.StatusCode==403||(int)r.StatusCode==400)){Check(true,name);return;}}
    throw new Exception(name+" did not fail at HTTP authorization boundary",ex);
   }
   throw new Exception(name+" unexpectedly returned a frame");
  }
  static async Task<FramePacket> WaitFrame(MediaWire wire,string id,long after=0){
   var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<5000){var f=await wire.GetFrameAsync(id);if(f!=null&&f.Sequence>after)return f;await Task.Delay(40);}throw new TimeoutException("No actual JPEG received");
  }
  static void CheckColor(byte[] jpeg,Color expected,string name){
   using(var stream=new MemoryStream(jpeg))using(var image=new Bitmap(stream)){var c=image.GetPixel(30,30);Check(Math.Abs(c.R-expected.R)<30&&Math.Abs(c.G-expected.G)<30&&Math.Abs(c.B-expected.B)<30,name);}
  }
  static PictureBox FindPicture(Control root){if(root is PictureBox)return (PictureBox)root;foreach(Control c in root.Controls){var p=FindPicture(c);if(p!=null)return p;}return null;}
  static InputCommand Command(FramePacket f,long seq,string kind){return new InputCommand{Generation=f.Generation,Sequence=seq,Kind=kind,Text="",Button=0};}
  static int Norm(int p,int size){return (int)Math.Round(p*65535.0/(size-1));}
  static async Task Exercise(OwnedFixture form){
   var p=ServerProfile.Parse(profileJson);var a=new ControlPlaneClient(p);var b=new ControlPlaneClient(p);var stranger=new ControlPlaneClient(p);
   a.Register(invite);b.Register(invite);stranger.Register(invite);
   var wire=new MediaWire(a);var strangerWire=new MediaWire(stranger);
   var session=a.Request(b.DeviceId,b.TemporaryPassword);
   await FrameDenied(wire,session.SessionId,"no frame before approval");
   Check(b.Decide(session.SessionId,true).Status=="Approved","target explicitly approved fixture");
   Check(a.Connect(session.SessionId).Status=="Connected","fixture control-plane connected");
   MediaSharingSession sharing=null;ViewerForm viewer=null;
   try{
    var scope=CaptureScope.ForOwnedWindow(form.Handle,Process.GetCurrentProcess().Id);
    sharing=new MediaSharingSession(b,session.SessionId,scope);
    await sharing.StartAsync();
    var frame=await WaitFrame(wire,session.SessionId);
    Check(frame.Width==form.ClientSize.Width&&frame.Height==form.ClientSize.Height,"real PrintWindow client dimensions");
    CheckColor(frame.Jpeg,Color.DeepSkyBlue,"rendered blue fixture decoded after HTTPS relay");
    if(outputDir!=null){Directory.CreateDirectory(outputDir);File.WriteAllBytes(Path.Combine(outputDir,"owned-media-frame.jpg"),frame.Jpeg);}
    await FrameDenied(strangerWire,session.SessionId,"stranger cannot read active media");
    form.Activate();form.Entry.Focus();
    await Until(()=>GetForegroundWindow()==form.Handle,"owned fixture foreground");
    var text=Command(frame,1,"text");text.Text="q";
    await wire.SendInputAsync(session.SessionId,text);
    try{await Until(()=>form.Entry.Text=="q"||!sharing.IsRunning,"native Unicode text event");}catch{throw new Exception("native diagnostic running="+sharing.IsRunning+" last="+sharing.LastError+" foreground="+(GetForegroundWindow()==form.Handle)+" textboxFocus="+form.Entry.Focused+" chars="+form.Entry.NativeChars+" keys="+form.Entry.NativeKeyDowns+" text="+form.Entry.Text);}
    Check(sharing.IsRunning,"input worker remains active: "+sharing.LastError);
    Check(form.Entry.Text=="q","native Unicode text delivered");
    Check(form.Entry.NativeChars>0,"native WM_CHAR reached owned textbox");
    var backspace=Command(frame,2,"key");backspace.KeyCode=8;
    await wire.SendInputAsync(session.SessionId,backspace);
    await Until(()=>form.Entry.Text=="","native key tap backspace");
    Check(form.Entry.NativeKeyDowns>0,"native WM_KEYDOWN reached owned textbox");
    var textAgain=Command(frame,3,"text");textAgain.Text="q";await wire.SendInputAsync(session.SessionId,textAgain);
    await Until(()=>form.Entry.Text=="q","native text restored");
    var click=Command(frame,4,"click");click.X=Norm(330,form.ClientSize.Width);click.Y=Norm(225,form.ClientSize.Height);
    await wire.SendInputAsync(session.SessionId,click);
    await Until(()=>form.Clicks==1,"native mouse click");
    Check(form.Hit.NativeMouseDowns>0,"native WM_LBUTTONDOWN reached owned button");
    FramePacket changed=null;var until=Stopwatch.StartNew();
    while(until.ElapsedMilliseconds<5000){
     changed=await WaitFrame(wire,session.SessionId,frame.Sequence);
     using(var ms=new MemoryStream(changed.Jpeg))using(var im=new Bitmap(ms)){var color=im.GetPixel(30,30);if(Math.Abs(color.G-Color.LimeGreen.G)<30&&Math.Abs(color.R-Color.LimeGreen.R)<30)break;}await Task.Delay(40);
    }
    if(changed==null)throw new Exception("No updated frame");
    CheckColor(changed.Jpeg,Color.LimeGreen,"actual click changed rendered output and relayed frame");
    if(outputDir!=null)File.WriteAllBytes(Path.Combine(outputDir,"owned-media-after-input.jpg"),changed.Jpeg);
    await sharing.StopAsync();
    Check(!sharing.IsRunning,"local stop ended capture/input worker");
    await FrameDenied(wire,session.SessionId,"local sharing stop denies frame");
    var oldGen=frame.Generation;
    sharing.Dispose();sharing=new MediaSharingSession(b,session.SessionId,scope);
    await sharing.StartAsync();
    var fresh=await WaitFrame(wire,session.SessionId);
    Check(fresh.Generation!=oldGen,"explicit restart changed generation");
    var oldInput=Command(frame,5,"text");oldInput.Text="x";
    bool oldDenied=false;
    try{await wire.SendInputAsync(session.SessionId,oldInput);}catch(Exception){oldDenied=true;}
    Check(oldDenied,"old generation input refused");
    a.Revoke(session.SessionId);
    await Until(()=>!sharing.IsRunning,"remote revoke stopped host");
    await FrameDenied(wire,session.SessionId,"revoked session no media");
    await Task.Delay(200);
    Check(form.Entry.Text=="q"&&form.Clicks==1,"no further native input after revoke");
    sharing.Dispose();sharing=null;
    b.RotatePassword();
    var expiring=a.Request(b.DeviceId,b.TemporaryPassword);b.Decide(expiring.SessionId,true);a.Connect(expiring.SessionId);
    sharing=new MediaSharingSession(b,expiring.SessionId,scope);await sharing.StartAsync();await WaitFrame(wire,expiring.SessionId);
    Console.WriteLine("REQUEST_EXPIRE");var reply=await Task.Run(()=>Console.ReadLine());if(reply!="EXPIRED")throw new Exception("No fixture expiry acknowledgement");
    await Until(()=>!sharing.IsRunning,"expiry stopped actual host");
    await FrameDenied(wire,expiring.SessionId,"expired session no media");
    Check(form.Entry.Text=="q"&&form.Clicks==1,"expiry does not add native input");
    sharing.Dispose();sharing=null;
    b.RotatePassword();
    var uiSession=a.Request(b.DeviceId,b.TemporaryPassword);b.Decide(uiSession.SessionId,true);a.Connect(uiSession.SessionId);
    sharing=new MediaSharingSession(b,uiSession.SessionId,scope);await sharing.StartAsync();await WaitFrame(wire,uiSession.SessionId);
    viewer=new ViewerForm(a,uiSession.SessionId);viewer.StartPosition=FormStartPosition.Manual;viewer.Location=new Point(650,100);viewer.Show();
    await Until(()=>{var box=FindPicture(viewer);return box!=null&&box.Image!=null;},"actual ViewerForm image");
    Check(true,"actual ViewerForm received and rendered frame");
    Check(viewer.Display.SizeMode==PictureBoxSizeMode.Zoom,"viewer uses mapped Zoom layout");
    await viewer.CloseSessionAsync();viewer.Close();await Until(()=>viewer.IsDisposed,"viewer closed after revoke");viewer=null;
    await Until(()=>!sharing.IsRunning,"viewer close disconnected host");
    Check(true,"closing actual viewer stopped sharing");
    sharing.Dispose();sharing=null;
    b.RotatePassword();
    var focusSession=a.Request(b.DeviceId,b.TemporaryPassword);b.Decide(focusSession.SessionId,true);a.Connect(focusSession.SessionId);
    sharing=new MediaSharingSession(b,focusSession.SessionId,scope);await sharing.StartAsync();var focusFrame=await WaitFrame(wire,focusSession.SessionId);
    using(var guard=new Form{Text="PeerDesk OWNED focus guard",StartPosition=FormStartPosition.Manual,Location=new Point(650,500),ClientSize=new Size(300,100)}){
     var guardText=new TextBox{Dock=DockStyle.Top};guard.Controls.Add(guardText);guard.Show();guard.Activate();guardText.Focus();
     await Until(()=>GetForegroundWindow()==guard.Handle,"second owned guard foreground");
     var forbidden=Command(focusFrame,1,"text");forbidden.Text="x";await wire.SendInputAsync(focusSession.SessionId,forbidden);
     await Task.Delay(400);
     Check(guardText.Text==""&&form.Entry.Text=="q","owned-window input scope refuses foreground mismatch");
     guard.Close();
    }
    await sharing.StopAsync();sharing.Dispose();sharing=null;
    await UiAcceptance.Run(profileJson,invite,b,outputDir,Check);
    await SettingsAcceptance.Run(profileJson,invite,outputDir,Check);
   }finally{if(viewer!=null&&!viewer.IsDisposed){await viewer.CloseSessionAsync();viewer.Close();await Until(()=>viewer.IsDisposed,"cleanup viewer close");}if(sharing!=null){await sharing.StopAsync();sharing.Dispose();}}
  }
  [STAThread]static int Main(string[] args){
   try{
    Console.OutputEncoding=new System.Text.UTF8Encoding(false);
    SetProcessDPIAware();profileJson=Console.ReadLine();invite=Console.ReadLine();if(profileJson==null||invite==null)throw new Exception("Missing test input");
    outputDir=args.Length>0?Path.GetFullPath(args[0]):null;
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    int exit=1;
    using(var form=new OwnedFixture()){
     form.Shown+=async(s,e)=>{try{await Exercise(form);Console.WriteLine("MEDIA_ACCEPTANCE_PASS count="+passed);exit=0;}catch(Exception ex){Console.Error.WriteLine("MEDIA_ACCEPTANCE_FAIL "+ex);}finally{form.Close();}};
     Application.Run(form);
    }
    return exit;
   }catch(Exception ex){Console.Error.WriteLine("MEDIA_ACCEPTANCE_FATAL "+ex.GetType().Name+": "+ex.Message);return 1;}
  }
 }
}
