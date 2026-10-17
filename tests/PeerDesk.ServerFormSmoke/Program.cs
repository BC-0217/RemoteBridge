using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static class Program {
 static int count;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
 static T Find<T>(Control root,string name) where T:Control{return (T)root.Controls.Find(name,true).Single();}
 static async Task Wait(Func<bool> predicate){var until=DateTime.UtcNow.AddSeconds(12);while(!predicate()){if(DateTime.UtcNow>=until)throw new TimeoutException("UI completion");await Task.Delay(20);}}
 [STAThread] static int Main(string[] args){
  Console.OutputEncoding=new System.Text.UTF8Encoding(false);
  string raw=Console.ReadLine(),invite=Console.ReadLine();
  var profile=ServerProfile.Parse(raw);
  string dir=Path.Combine(Path.GetTempPath(),"PeerDesk-simple-form-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  string settings=Path.Combine(dir,"settings.json");int result=1;
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  var form=new MainForm(Path.Combine(dir,"history.json"),settings);
  form.Shown+=async(s,e)=>{
   try{
    var tabs=Find<TabControl>(form,"ServerSettingsTabs");tabs.SelectedIndex=tabs.TabPages.Count-1;Find<ComboBox>(form,"ServerRole").SelectedIndex=1;
    Find<TextBox>(form,"ServerName").Text=profile.Name;
    Find<TextBox>(form,"ServerAddress").Text=profile.RendezvousUrl;
    Find<TextBox>(form,"ServerFingerprint").Text=profile.CertificateSha256;
    var generated=ServerProfile.Parse(Find<TextBox>(form,"ProfileJson").Text);
    Check(generated.RendezvousUrl==profile.RendezvousUrl&&generated.RelayUrl==profile.RendezvousUrl&&generated.CertificateSha256==profile.CertificateSha256,"simple form generates validated public profile");
    var health=Find<Button>(form,"CheckServerConnection");health.PerformClick();await Wait(()=>health.Enabled);
    var status=Find<Label>(form,"ConnectionStatus").Text;
    Check(status.StartsWith("서버 연결 확인됨 — 아직 등록되지 않았습니다.",StringComparison.Ordinal),"actual pinned HTTPS health action completes without error");
    Check(Find<TextBox>(form,"MyDeviceId").Text=="(미등록)"&&!Find<Button>(form,"RequestConnection").Enabled,"health does not fabricate device registration");
    form.Size=new Size(760,620);await Task.Delay(30);
    foreach(string name in new[]{"ServerName","ServerAddress","ServerFingerprint","InvitationCode","RegisterDevice","CheckServerConnection"}){
     var ctl=form.Controls.Find(name,true).Single();Rectangle rect=ctl.RectangleToScreen(ctl.ClientRectangle);bool visible=rect.Width>0&&rect.Height>0;
     for(Control parent=ctl.Parent;parent!=null;parent=parent.Parent)visible &= parent.RectangleToScreen(parent.ClientRectangle).Contains(rect);
     if(!visible){Console.WriteLine("BOUNDS "+name+" "+rect);for(Control p=ctl.Parent;p!=null;p=p.Parent)Console.WriteLine("ANCESTOR "+p.GetType().Name+" "+p.Name+" "+p.RectangleToScreen(p.ClientRectangle));}Check(visible,"minimum visible "+name);
    }
    Find<TextBox>(form,"InvitationCode").Text=invite;
    Find<Button>(form,"RegisterDevice").PerformClick();
    await Wait(()=>Find<Button>(form,"RequestConnection").Enabled);
    string id=Find<TextBox>(form,"MyDeviceId").Text,pw=Find<TextBox>(form,"TemporaryPassword").Text;
    Check(CredentialFormat.IsNewDeviceId(id)&&CredentialFormat.IsNewPassword(pw)&&!Find<TextBox>(form,"TemporaryPassword").UseSystemPasswordChar,"simple form actual registration displays new-format ID and default-visible password");
    Check(Find<TextBox>(form,"TargetPassword").UseSystemPasswordChar&&Find<TextBox>(form,"InvitationCode").UseSystemPasswordChar,"target password and invitation stay masked");
    Check(tabs.TabPages.Count==3&&tabs.TabPages[0].Name=="HomePage"&&tabs.TabPages[1].Name=="SessionManagementPage"&&tabs.TabPages[2].Name=="ServerSettingsPage","home/session/server tabs with server settings last");
    Check(Find<Button>(form,"CopyDeviceId").Enabled&&Find<Button>(form,"CopyTemporaryPassword").Enabled&&Find<Button>(form,"CopyAllCredentials").Enabled,"copy buttons enabled only after verified registration");
    tabs.SelectedIndex=0;await Task.Delay(30);
    foreach(string name in new[]{"MyDeviceId","TemporaryPassword","CopyDeviceId","CopyTemporaryPassword","CopyAllCredentials","TogglePassword","RotatePassword","TargetDeviceId","TargetPassword","RequestConnection"}){
     var ctl=form.Controls.Find(name,true).Single();Rectangle rect=ctl.RectangleToScreen(ctl.ClientRectangle);bool visible=rect.Width>0&&rect.Height>0;
     for(Control parent=ctl.Parent;parent!=null;parent=parent.Parent)visible &= parent.RectangleToScreen(parent.ClientRectangle).Contains(rect);
     Check(visible,"minimum visible home "+name);
    }
    tabs.SelectedIndex=tabs.TabPages.Count-1;
    Check(Find<TextBox>(form,"InvitationCode").Text.Length==0,"invitation cleared after registration");
    Check(Find<TextBox>(form,"ServerAddress").ReadOnly||!Find<TextBox>(form,"ServerAddress").Enabled,"registered server edit locked");
    var saved=new SettingsStore(settings).Load();
    Check(saved.IsRegistered&&saved.DeviceId==id&&saved.TemporaryPassword==pw,"simple form registration persists DPAPI state");
    string wire=File.ReadAllText(settings);Check(!wire.Contains(pw)&&!wire.Contains(invite),"settings has no plaintext password/invitation");
    await AwsRegistrationChecks.Run(form,settings,invite,args,Check);
    Console.WriteLine("SERVER_FORM_SMOKE_PASS count="+count);result=0;
   }catch(Exception ex){Console.Error.WriteLine("SERVER_FORM_SMOKE_FAIL "+ex.GetType().Name+": "+ex.Message);}
   finally{form.Close();}
  };
  Application.Run(form);return result;
 }
}
