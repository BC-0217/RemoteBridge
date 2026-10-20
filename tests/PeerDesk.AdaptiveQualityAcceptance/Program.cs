using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static class Program
{
    static int n;
    static void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
    sealed class Fixture:Form { protected override bool ShowWithoutActivation { get{return true;} } }
    [STAThread] static int Main(){
        try {
            using(var f=new Fixture { ClientSize=new Size(640,360),ShowInTaskbar=false,Text="RemoteBridge owned quality fixture",Location=new Point(40,40),StartPosition=FormStartPosition.Manual }){
                f.Controls.Add(new Label{Text="Owned full-frame quality fixture",Dock=DockStyle.Fill,BackColor=Color.CornflowerBlue});
                f.Show();Application.DoEvents();
                var scope=CaptureScope.ForOwnedWindow(f.Handle,System.Diagnostics.Process.GetCurrentProcess().Id);
                using(var bitmap=scope.Capture(320,180)){
                    Check(bitmap.Width==320&&bitmap.Height==180,"selected output size applied to owned capture");
                    var bounds=scope.GetInputBounds();
                    Check(bounds.Width==640&&bounds.Height==360,"output scale never changes input source geometry");
                    var method=typeof(MediaSharingSession).GetMethod("EncodeForQuality",BindingFlags.Static|BindingFlags.NonPublic);
                    foreach(int quality in new[]{85,70,55,40,30}){
                        var frame=(FramePacket)method.Invoke(null,new object[]{bitmap,7L,9L,quality});
                        Check(frame.Jpeg.Length>0&&frame.Jpeg.Length<=524288,"bounded full JPEG q"+quality);
                        MediaWire.ValidateJpeg(frame.Jpeg,320,180);
                        Check(frame.Width==320&&frame.Height==180&&frame.Generation==7&&frame.Sequence==9,"frame geometry and identity q"+quality);
                    }
                }
                using(var natural=scope.Capture()){Check(natural.Width==640&&natural.Height==360,"legacy capture overload preserved");}
                Check(Reject(()=>scope.Capture(1921,1080)),"capture oversized dimensions rejected");
                Check(Reject(()=>scope.Capture(0,180)),"capture zero dimensions rejected");
                f.Close();
            }
            Console.WriteLine("ADAPTIVE_QUALITY_ACCEPTANCE_PASS count="+n);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static bool Reject(Action a){try{a();return false;}catch(ArgumentOutOfRangeException){return true;}}
}