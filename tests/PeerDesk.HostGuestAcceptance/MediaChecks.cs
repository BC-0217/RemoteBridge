using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PeerDesk.Windows;
using PeerDesk.MediaAcceptance;
internal static class MediaChecks {
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 static async Task Until(Func<bool> p,string step){var sw=Stopwatch.StartNew();while(!p()){if(sw.ElapsedMilliseconds>10000)throw new TimeoutException(step);await Task.Delay(40);}}
 static async Task<FramePacket> Frame(MediaWire wire,string id,long after=0){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<10000){var f=await wire.GetFrameAsync(id);if(f!=null&&f.Sequence>after)return f;await Task.Delay(60);}throw new TimeoutException("AWS owned-window frame");}
 static bool ColorMatches(byte[] bytes,Color color){using(var ms=new MemoryStream(bytes))using(var b=new Bitmap(ms)){var c=b.GetPixel(30,30);return Math.Abs(c.R-color.R)<30&&Math.Abs(c.G-color.G)<30&&Math.Abs(c.B-color.B)<30;}}
 static int Norm(int p,int size){return (int)Math.Round(p*65535.0/(size-1));}
 internal static async Task Run(ControlPlaneClient initiator,ControlPlaneClient target,string output,Action<bool,string> check,Func<Task> afterConnected=null) {
  using(var form=new OwnedFixture()){
   form.Show();form.Activate();form.Entry.Focus();
   await Until(()=>GetForegroundWindow()==form.Handle,"Owned media fixture foreground");
   var session=await Task.Run(()=>initiator.Request(target.DeviceId,target.TemporaryPassword));
   check(session.Status=="Pending","AWS guest media request initially requires approval");
   check((await Task.Run(()=>target.Decide(session.SessionId,true))).Status=="Approved","AWS guest target explicitly approves");
   check((await Task.Run(()=>initiator.Connect(session.SessionId))).Status=="Connected","AWS guest approved session reaches Connected");
   using(var sharing=new MediaSharingSession(target,session.SessionId,CaptureScope.ForOwnedWindow(form.Handle,Process.GetCurrentProcess().Id))){
    await sharing.StartAsync();var wire=new MediaWire(initiator);var f=await Frame(wire,session.SessionId);
    check(f.Width==form.ClientSize.Width&&f.Height==form.ClientSize.Height,"AWS relay returns actual owned-window dimensions");
    check(ColorMatches(f.Jpeg,Color.DeepSkyBlue),"AWS relay JPEG decodes actual blue fixture");
    if(afterConnected!=null)await afterConnected();
    File.WriteAllBytes(Path.Combine(output,"aws-owned-media-before.jpg"),f.Jpeg);
    form.Activate();form.Entry.Focus();await Until(()=>GetForegroundWindow()==form.Handle,"Owned fixture foreground before input");
    await wire.SendInputAsync(session.SessionId,new InputCommand{Generation=f.Generation,Sequence=1,Kind="text",Text="q",Button=0});
    await Until(()=>form.Entry.Text=="q"||!sharing.IsRunning,"AWS native text delivery");
    check(sharing.IsRunning&&form.Entry.Text=="q"&&form.Entry.NativeChars>0,"AWS relay delivers native Unicode input only to owned textbox");
    await wire.SendInputAsync(session.SessionId,new InputCommand{Generation=f.Generation,Sequence=2,Kind="key",Text="",Button=0,KeyCode=8});
    await Until(()=>form.Entry.Text=="","AWS native key tap");
    check(form.Entry.NativeKeyDowns>0,"AWS relay delivers native keydown");
    await wire.SendInputAsync(session.SessionId,new InputCommand{Generation=f.Generation,Sequence=3,Kind="click",Text="",Button=0,X=Norm(330,form.ClientSize.Width),Y=Norm(225,form.ClientSize.Height)});
    await Until(()=>form.Clicks==1||!sharing.IsRunning,"AWS native mouse click");
    check(sharing.IsRunning&&form.Clicks==1&&form.Hit.NativeMouseDowns>0,"AWS relay delivers native click to owned button");
    FramePacket changed=null;var timer=Stopwatch.StartNew();
    while(timer.ElapsedMilliseconds<10000){changed=await Frame(wire,session.SessionId,f.Sequence);if(ColorMatches(changed.Jpeg,Color.LimeGreen))break;await Task.Delay(60);}
    check(changed!=null&&ColorMatches(changed.Jpeg,Color.LimeGreen),"AWS returned JPEG reflects real click-driven paint change");
    File.WriteAllBytes(Path.Combine(output,"aws-owned-media-after.jpg"),changed.Jpeg);
    using(var viewer=new ViewerForm(initiator,session.SessionId)){viewer.Show();await Until(()=>viewer.FrameCount>0,"Actual RemoteBridge viewer receives AWS frame");check(viewer.Text.StartsWith("RemoteBridge",StringComparison.Ordinal)&&viewer.Display.Image!=null,"Actual branded RemoteBridge viewer displays AWS-relayed frame");using(var image=new Bitmap(viewer.Width,viewer.Height)){viewer.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,"remotebridge-viewer.png"),System.Drawing.Imaging.ImageFormat.Png);}
    await sharing.StopAsync();check(!sharing.IsRunning,"AWS local sharing stop stops native worker");
    check((await Task.Run(()=>initiator.Revoke(session.SessionId))).Status=="Revoked","AWS guest session revoke succeeds");
    bool denied=false;try{await wire.GetFrameAsync(session.SessionId);}catch{denied=true;}check(denied,"AWS revoked session cannot read further frames");await viewer.CloseSessionAsync();viewer.Close();await Until(()=>viewer.IsDisposed,"RemoteBridge viewer closes");}
   }
   form.Close();
  }
 }
}
