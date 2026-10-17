using System;
using System.Drawing;
using System.Windows.Forms;
namespace PeerDesk.MediaAcceptance
{
 internal sealed class CountedTextBox:TextBox {
  public int NativeChars,NativeKeyDowns;
  protected override void WndProc(ref Message m){if(m.Msg==0x102)NativeChars++;if(m.Msg==0x100)NativeKeyDowns++;base.WndProc(ref m);}
 }
 internal sealed class CountedButton:Button {
  public int NativeMouseDowns;
  protected override void WndProc(ref Message m){if(m.Msg==0x201)NativeMouseDowns++;base.WndProc(ref m);}
 }
 internal sealed class OwnedFixture:Form {
  public readonly CountedTextBox Entry=new CountedTextBox();
  public readonly CountedButton Hit=new CountedButton();
  public int Clicks;
  public OwnedFixture(){
   Text="PeerDesk OWNED media/input acceptance fixture";
   StartPosition=FormStartPosition.Manual;Location=new Point(80,100);ClientSize=new Size(480,320);
   BackColor=Color.White;
   Entry.SetBounds(20,210,180,30);Hit.SetBounds(260,205,160,45);Hit.Text="OWNED CLICK TARGET";
   Hit.Click+=(s,e)=>{Clicks++;Invalidate(new Rectangle(15,15,160,110));};
   Controls.Add(Entry);Controls.Add(Hit);
   var label=new Label{Text="Test window only. No private desktop is captured.",AutoSize=true,Location=new Point(20,160)};
   Controls.Add(label);
  }
  protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var b=new SolidBrush(Clicks==0?Color.DeepSkyBlue:Color.LimeGreen))e.Graphics.FillRectangle(b,20,20,150,100);}
 }
}
