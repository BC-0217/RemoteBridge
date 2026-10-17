using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;

namespace PeerDesk.MediaAcceptance
{
 internal static class UiAcceptance
 {
  static T Find<T>(Control root,string name) where T:Control {
   var found=root.Controls.Find(name,true).OfType<T>().SingleOrDefault();
   if(found==null)throw new Exception("UI control missing/type mismatch: "+name);
   return found;
  }
  static async Task Until(Func<bool> condition,string name) {
   var end=System.Diagnostics.Stopwatch.StartNew();
   while(!condition()){if(end.ElapsedMilliseconds>5000)throw new TimeoutException(name);await Task.Delay(20);}
  }
  static void SelectPage(Form form,string pageName) { var tabs=Find<TabControl>(form,"ServerSettingsTabs");tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(p=>p.Name==pageName);if(pageName=="ServerSettingsPage"){Find<ComboBox>(form,"ServerRole").SelectedIndex=1;Find<TabControl>(form,"HostSettingsTabs").SelectedIndex=0;Find<CheckBox>(form,"ShowAdvancedProfile").Checked=true;} }
  static bool Fits(Control control,Form form) {
   if(!control.Visible||control.Width<=0||control.Height<=0)return false;
   var bounds=control.RectangleToScreen(new Rectangle(Point.Empty,control.Size));
   for(Control parent=control.Parent;parent!=null;parent=parent.Parent)if(!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))return false;
   return form.RectangleToScreen(form.ClientRectangle).Contains(bounds);
  }
  internal static async Task Run(string profile,string invitation,ControlPlaneClient target,string output,Action<bool,string> check)
  {
   string dir=output??Path.Combine(Path.GetTempPath(),"PeerDesk-owned-ui-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(dir);
   string historyPath=Path.Combine(dir,"owned-ui-history.json");
   using(var ui=new MainForm(historyPath,Path.Combine(dir,"owned-ui-settings-"+Guid.NewGuid().ToString("N")+".json"))) {
    ui.StartPosition=FormStartPosition.Manual;ui.Location=new Point(40,40);ui.Show();
    var tabs=Find<TabControl>(ui,"ServerSettingsTabs");
    var profileBox=Find<TextBox>(ui,"ProfileJson");
    var invite=Find<TextBox>(ui,"InvitationCode");
    var register=Find<Button>(ui,"RegisterDevice");
    var ownId=Find<TextBox>(ui,"MyDeviceId");
    var password=Find<TextBox>(ui,"TemporaryPassword");
    var targetId=Find<TextBox>(ui,"TargetDeviceId");
    var targetPassword=Find<TextBox>(ui,"TargetPassword");
    var request=Find<Button>(ui,"RequestConnection");
    var sessionList=Find<ListBox>(ui,"Sessions");
    await Until(()=>ui.Enabled&&register.Enabled,"initial empty-settings startup ready");
    SelectPage(ui,"ServerSettingsPage");
    profileBox.Text=profile;invite.Text=invitation;register.PerformClick();
    await Until(()=>request.Enabled&&System.Text.RegularExpressions.Regex.IsMatch(ownId.Text,"^[1-9][0-9]{9}$"),"real UI registration");
    SelectPage(ui,"HomePage");ui.PerformLayout();
    check(ownId.ReadOnly&&ownId.Text.Length>0,"UI shows server-assigned own ID");
    check(password.ReadOnly&&password.Text.Length==9,"UI receives real temporary password");
    check(invite.Text.Length==0,"UI clears private registration invitation");
    check(targetPassword.UseSystemPasswordChar,"target password field is masked");
    check(!password.UseSystemPasswordChar,"own temporary password is visible by user choice at startup");
    Find<Button>(ui,"TogglePassword").PerformClick();check(password.UseSystemPasswordChar,"own password can be hidden");
    Find<Button>(ui,"TogglePassword").PerformClick();check(!password.UseSystemPasswordChar,"own password can be shown again");
    Find<Button>(ui,"CopyDeviceId").PerformClick();check(Clipboard.GetText()=="ID : "+ownId.Text,"ID copy uses labelled paste format");
    Find<Button>(ui,"CopyTemporaryPassword").PerformClick();check(Clipboard.GetText()=="PW : "+password.Text,"PW copy uses labelled paste format");
    Find<Button>(ui,"CopyAllCredentials").PerformClick();check(Clipboard.GetText()=="ID : "+ownId.Text+"\r\nPW : "+password.Text,"all copy uses exact two-line labelled paste format");
    Find<Button>(ui,"CopyDeviceId").PerformClick();
    string originalPassword=password.Text;
    Find<Button>(ui,"RotatePassword").PerformClick();
    await Until(()=>request.Enabled&&password.Text!=originalPassword,"asynchronous password rotation completed");
    check(password.Text.Length==9&&password.Text!=originalPassword,"UI rotates actual server temporary password");
    string ownerPassword=password.Text;
    SelectPage(ui,"HomePage");
    target.RotatePassword();
    targetId.Text=target.DeviceId;targetPassword.Text=target.TemporaryPassword;
    request.PerformClick();
    await Until(()=>sessionList.Items.Count>0,"UI request reaches server");
    var pending=target.Pending();
    var actual=pending.Single(x=>x.InitiatorDeviceId==ownId.Text);
    check(actual.Status=="Pending","UI ID/password request awaits target approval");
    check(targetPassword.Text.Length==0,"UI clears remote temporary password after request");
    SelectPage(ui,"SessionManagementPage");sessionList.SelectedIndex=0;
    target.Decide(actual.SessionId,true);
    Find<Button>(ui,"ConnectSession").PerformClick();
    await Until(()=>sessionList.Items.Cast<SessionItem>().Any(x=>x.SessionId==actual.SessionId&&x.Status=="Connected"),"UI connects approved session");
    check(true,"actual UI Connect handler reaches Connected");
    await Until(()=>File.Exists(historyPath),"bounded connection history persisted");
    string stored=File.ReadAllText(historyPath);
    check(stored.Contains(target.DeviceId)&&stored.Contains("Connected"),"history records actual connected target");
    check(!stored.Contains(ownerPassword)&&!stored.Contains(originalPassword)&&!stored.Contains(target.TemporaryPassword)&&!stored.Contains(invitation)&&!stored.Contains(actual.SessionId),"history excludes passwords invitation and session identifier");
    check(new FileInfo(historyPath).Length<=65536,"history file remains bounded");
    Find<Button>(ui,"DisconnectSession").PerformClick();
    check(sessionList.Items.Cast<SessionItem>().Any(x=>x.SessionId==actual.SessionId&&x.Status=="Revoked"),"UI disconnect revokes real session");
    SelectPage(ui,"HomePage");Find<Button>(ui,"TogglePassword").PerformClick();
    check(password.UseSystemPasswordChar,"evidence screenshot hides the temporary password explicitly");
    ui.Refresh();await Task.Delay(100);
    if(output!=null) {
     using(var bitmap=new Bitmap(ui.Width,ui.Height)){ui.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(dir,"owned-main-ui.png"),ImageFormat.Png);}
    }
    check(Find<Control>(ui,"ConnectionHistory")!=null,"recent connection list is present");
    check(Fits(Find<Label>(ui,"ConnectionStatus"),ui),"connection status is visible at default size");
    Size originalSize=ui.Size;ui.Size=ui.MinimumSize;ui.PerformLayout();await Task.Delay(60);
    foreach(string name in new[]{"MyDeviceId","TemporaryPassword","TargetDeviceId","TargetPassword","RequestConnection","ConnectionHistory","ConnectionStatus","PasswordExpiry","CopyDeviceId","CopyTemporaryPassword","CopyAllCredentials","TogglePassword","RotatePassword"}) { var c=Find<Control>(ui,name);if(!Fits(c,ui))for(Control p=c;p!=null;p=p.Parent)Console.WriteLine("BOUNDS "+p.Name+" "+p.GetType().Name+" visible="+p.Visible+" "+p.RectangleToScreen(p.ClientRectangle));check(Fits(c,ui),"minimum-size visible bounds: "+name); }
    if(output!=null)using(var bitmap=new Bitmap(ui.Width,ui.Height)){ui.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(dir,"owned-main-ui-minimum.png"),ImageFormat.Png);}
    ui.Size=originalSize;ui.PerformLayout();Find<Button>(ui,"TogglePassword").PerformClick();
    var reloaded=new ConnectionHistory(historyPath);
    check(reloaded.Warning==null&&reloaded.ForServer(target.ServerUrl,target.CertificateSha256).Count>=3,"connection history survives reload for the same server");
    target.RotatePassword();
    targetPassword.Text=target.TemporaryPassword;request.PerformClick();
    var next=target.Pending().Single(x=>x.InitiatorDeviceId==ownId.Text);
    target.Decide(next.SessionId,true);SelectPage(ui,"SessionManagementPage");sessionList.SelectedIndex=sessionList.Items.Count-1;Find<Button>(ui,"ConnectSession").PerformClick();
    Find<Button>(ui,"OpenViewer").PerformClick();
    await Until(()=>Application.OpenForms.OfType<ViewerForm>().Any(),"actual UI viewer opens before reset");
    SelectPage(ui,"ServerSettingsPage");
    Find<Button>(ui,"ResetProfile").PerformClick();
    await Until(()=>!System.Text.RegularExpressions.Regex.IsMatch(ownId.Text,"^[1-9][0-9]{9}$"),"UI reset must not deadlock with asynchronous viewer revoke");
    check(!System.Text.RegularExpressions.Regex.IsMatch(password.Text,"^[A-Za-z]{9}$")&&targetPassword.Text.Length==0,"reset clears temporary credentials");
    await Until(()=>!Application.OpenForms.OfType<ViewerForm>().Any(),"reset closes the previous viewer");
    check(true,"profile reset completes actual viewer revoke and close");
    ui.Close();await Until(()=>ui.IsDisposed,"UI asynchronous close");
   }
  }
 }
}
