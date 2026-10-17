using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static class VisualChecks {
 static T Find<T>(Control f,string name)where T:Control{return f.Controls.Find(name,true).OfType<T>().Single();}
 static int failures;static void Check(bool ok,string name){if(!ok)failures++;Console.WriteLine((ok?"PASS ":"FAIL ")+name);}
 static bool Fits(Control c){if(!c.Visible)return false;var r=c.RectangleToScreen(c.ClientRectangle);for(Control p=c.Parent;p!=null;p=p.Parent)if(!p.RectangleToScreen(p.ClientRectangle).Contains(r)){Console.WriteLine("BOUNDS "+c.Name+" "+r+" ancestor "+p.GetType().Name+" "+p.RectangleToScreen(p.ClientRectangle));return false;}return r.Width>0&&r.Height>0;}
 static void Capture(Form f,string path){using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(path,ImageFormat.Png);}}
 internal static int Run(string output){
  Directory.CreateDirectory(output);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  var b=new DirectoryBootstrap("https://localhost:8443",new string('A',64),"owned-visual-only");
  string unique=Guid.NewGuid().ToString("N");int result=1;
  using(var f=new MainForm(Path.Combine(output,"visual-history-"+unique+".json"),Path.Combine(output,"visual-settings-"+unique+".json"),b)){
   f.Shown+=(s,e)=>{try{
    var tabs=Find<TabControl>(f,"ServerSettingsTabs");tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(x=>x.Name=="ServerSettingsPage");
    Check(Find<ComboBox>(f,"ServerRole").SelectedIndex==0,"Visual fresh app defaults to Guest");
    Check(!Find<TextBox>(f,"ServerAddress").Visible&&!Find<TextBox>(f,"ServerFingerprint").Visible,"Guest visually hides detailed endpoint fields");
    f.Size=new Size(760,620);f.PerformLayout();
    foreach(string n in new[]{"ServerRole","GuestServerAlias","GuestJoinPassword","GuestJoin"})Check(Fits(Find<Control>(f,n)),"Minimum guest visible bounds "+n);
    Capture(f,Path.Combine(output,"guest-settings-minimum.png"));
    Find<ComboBox>(f,"ServerRole").SelectedIndex=1;
    var host=Find<TabControl>(f,"HostSettingsTabs");host.SelectedTab=host.TabPages.Cast<TabPage>().Single(x=>x.Name=="HostOwnerPage");f.PerformLayout();
    var management=Find<TabControl>(f,"OwnerManagementTabs");
    foreach(var page in new[]{"OwnerSettingsPage","OwnerPublishingPage","OwnerDevicesPage"}){
     management.SelectedTab=management.TabPages.Cast<TabPage>().Single(p=>p.Name==page);f.PerformLayout();
     var names=page=="OwnerSettingsPage"?new[]{"OwnerSecret","HostLoadSettings","HostCurrentDisplayName","HostPasswordState","HostDisplayName","HostJoinPassword","HostSaveSettings"}:page=="OwnerPublishingPage"?new[]{"OwnerSecret","HostLoadSettings","HostPublishedAlias","HostAliasInput","VerifyHostAlias","CopyHostAlias","HostPublishGrant","HostPublish"}:new[]{"OwnerSecret","HostLoadSettings","OwnerDevices","OwnerListDevices","RevokeDevice"};
     foreach(var n in names)Check(Fits(Find<Control>(f,n)),"Minimum "+page+" visible bounds "+n);
     Capture(f,Path.Combine(output,page+"-minimum.png"));
    }
    management.SelectedTab=management.TabPages.Cast<TabPage>().Single(p=>p.Name=="OwnerSettingsPage");
    Check(Find<TextBox>(f,"OwnerSecret").UseSystemPasswordChar&&Find<TextBox>(f,"HostJoinPassword").UseSystemPasswordChar&&Find<TextBox>(f,"HostPublishGrant").UseSystemPasswordChar,"Owner private inputs masked by default");
    Capture(f,Path.Combine(output,"host-owner-settings-minimum.png"));if(failures>0)throw new Exception("Required visual assertions failed: "+failures);Console.WriteLine("HOST_GUEST_VISUAL_PASS");result=0;
   }catch(Exception ex){Console.Error.WriteLine("HOST_GUEST_VISUAL_FAIL "+ex.Message);}finally{f.Close();}};
   Application.Run(f);
  }
  return result;
 }
}
