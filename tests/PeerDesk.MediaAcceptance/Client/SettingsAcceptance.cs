using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;

namespace PeerDesk.MediaAcceptance
{
 internal static class SettingsAcceptance
 {
  static T Find<T>(Control root,string name) where T:Control {
   var c=root.Controls.Find(name,true).OfType<T>().SingleOrDefault();
   if(c==null)throw new Exception("Settings UI control missing: "+name);return c;
  }
  static async Task Until(Func<bool> test,string name,int timeout=12000) {
   var clock=System.Diagnostics.Stopwatch.StartNew();
   while(!test()){if(clock.ElapsedMilliseconds>timeout)throw new TimeoutException(name);await Task.Delay(20);}
  }
  static async Task Close(MainForm ui){ui.Close();await Until(()=>ui.IsDisposed,"settings fixture closes");}
  static async Task ShowAndReady(MainForm ui) {
   var shown=new TaskCompletionSource<bool>();ui.Shown+=(s,e)=>shown.TrySetResult(true);ui.Show();await shown.Task;
   await Until(()=>ui.Enabled&&!(bool)typeof(MainForm).GetField("lifecycleBusy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(ui)&&(Find<Button>(ui,"RegisterDevice").Enabled||Find<Button>(ui,"RequestConnection").Enabled||Find<Button>(ui,"ResetProfile").Enabled),"settings startup completes");
  }
  internal static async Task Run(string profile,string invite,string output,Action<bool,string> check) {
   string dir=output??Path.Combine(Path.GetTempPath(),"PeerDesk-owned-settings-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(dir);
   string hp=Path.Combine(dir,"settings-ui-history.json"),sp=Path.Combine(dir,"settings-ui-"+Guid.NewGuid().ToString("N")+".json");
   string id,password;
   using(var ui=new MainForm(hp,sp)) {
    await ShowAndReady(ui);
    check(!Find<Button>(ui,"RequestConnection").Enabled,"unregistered startup cannot request remote control");
    check(!Find<Button>(ui,"CopyTemporaryPassword").Enabled&&!Find<Button>(ui,"CopyAllCredentials").Enabled,"unregistered credentials cannot be copied");
    check(!String.IsNullOrWhiteSpace(Find<TextBox>(ui,"MyDeviceId").Text)&&!Regex.IsMatch(Find<TextBox>(ui,"MyDeviceId").Text,"^[1-9][0-9]{9}$"),"unregistered ID is explained, not fabricated or blank");
    check(Find<TabControl>(ui,"ServerSettingsTabs").SelectedTab.Name=="ServerSettingsPage","first startup opens server setup");
    Find<ComboBox>(ui,"ServerRole").SelectedIndex=1;Find<CheckBox>(ui,"ShowAdvancedProfile").Checked=true;Find<TextBox>(ui,"ProfileJson").Text=profile;Find<TextBox>(ui,"InvitationCode").Text=invite;
    Find<Button>(ui,"RegisterDevice").PerformClick();
    await Until(()=>Find<Button>(ui,"RequestConnection").Enabled&&File.Exists(sp),"actual registration is persisted");
    id=Find<TextBox>(ui,"MyDeviceId").Text;password=Find<TextBox>(ui,"TemporaryPassword").Text;
    check(Regex.IsMatch(id,"^[1-9][0-9]{9}$")&&Regex.IsMatch(password,"^(?=.*[A-Z])(?=.*[a-z])[A-Za-z]{9}$"),"registration replaces placeholders with actual ID and password");
    var saved=new SettingsStore(sp).Load();string stored=File.ReadAllText(sp);
    check(saved.IsRegistered&&saved.DeviceId==id&&saved.TemporaryPassword==password,"DPAPI current-user credential roundtrip");
    check(!stored.Contains(saved.BearerToken)&&!stored.Contains(password)&&!stored.Contains(invite),"settings file has no plaintext token password or invitation");
    await Close(ui);
   }
   using(var resumed=new MainForm(hp,sp)) {
    await ShowAndReady(resumed);
    check(Find<Button>(resumed,"RequestConnection").Enabled&&Find<TextBox>(resumed,"MyDeviceId").Text==id,"restart validates server and retains the same actual ID; status="+Find<Label>(resumed,"ConnectionStatus").Text);
    string renewed=Find<TextBox>(resumed,"TemporaryPassword").Text;
    check(Regex.IsMatch(renewed,"^(?=.*[A-Z])(?=.*[a-z])[A-Za-z]{9}$")&&renewed!=password,"restart gets an actual fresh temporary password");
    check(!Find<TextBox>(resumed,"TemporaryPassword").UseSystemPasswordChar,"resumed password is visible by user choice");
    await Close(resumed);
   }
   Console.WriteLine("REQUEST_PASSWORD_EXPIRE");
   if(await Task.Run(()=>Console.ReadLine())!="PASSWORD_EXPIRED")throw new Exception("Missing password expiry acknowledgement");
   using(var expired=new MainForm(hp,sp)) {
    await ShowAndReady(expired);
    check(Find<Button>(expired,"RequestConnection").Enabled&&Find<TextBox>(expired,"MyDeviceId").Text==id,"expired saved password recovers through authenticated owner rotation");
    await Close(expired);
   }
   var good=new SettingsStore(sp).Load();
   var poisoned=new SettingsStore(sp).Load();poisoned.BearerToken="OWNED_INVALID_TEST_TOKEN";new SettingsStore(sp).Save(poisoned);
   using(var denied=new MainForm(hp,sp)) {
    await ShowAndReady(denied);
    check(!Find<Button>(denied,"RequestConnection").Enabled,"server401 never enables saved credentials");
    check(Find<TextBox>(denied,"TemporaryPassword").Text!=good.TemporaryPassword,"server401 does not present cached password as usable");
    await Close(denied);
   }
   new SettingsStore(sp).Save(good);
   using(var reset=new MainForm(hp,sp)) {
    await ShowAndReady(reset);
    check(Find<Button>(reset,"RequestConnection").Enabled,"valid saved registration can recover after stale-token test");
    Find<TabControl>(reset,"ServerSettingsTabs").SelectedTab=Find<TabControl>(reset,"ServerSettingsTabs").TabPages.Cast<TabPage>().Single(p=>p.Name=="ServerSettingsPage");Find<ComboBox>(reset,"ServerRole").SelectedIndex=1;Find<Button>(reset,"ResetProfile").PerformClick();
    await Until(()=>Find<Button>(reset,"RegisterDevice").Enabled,"credential reset completes");
    var cleared=new SettingsStore(sp).Load();
    check(!cleared.IsRegistered&&String.IsNullOrEmpty(cleared.BearerToken)&&cleared.ProfileJson==profile,"reset clears protected credentials while retaining public profile");
    await Close(reset);
   }
   using(var clean=new MainForm(hp,sp)) {
    await ShowAndReady(clean);
    check(!Find<Button>(clean,"RequestConnection").Enabled,"reset registration is not silently restored on next startup");
    await Close(clean);
   }
   new SettingsStore(sp).Save(good);
   Console.WriteLine("REQUEST_SERVER_STOP");
   if(await Task.Run(()=>Console.ReadLine())!="SERVER_STOPPED")throw new Exception("Missing offline acknowledgement");
   using(var offline=new MainForm(hp,sp)) {
    await ShowAndReady(offline);
    check(!Find<Button>(offline,"RequestConnection").Enabled,"offline saved identity never enables remote control");
    check(!Find<Button>(offline,"CopyTemporaryPassword").Enabled&&!Find<Button>(offline,"CopyAllCredentials").Enabled,"offline cached credentials cannot be copied");
    check(Find<TextBox>(offline,"TemporaryPassword").Text!=good.TemporaryPassword,"offline startup does not expose cached password as usable");
    if(output!=null){using(var image=new Bitmap(offline.Width,offline.Height)){offline.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(dir,"owned-settings-offline.png"));}}
    await Close(offline);
   }
  }
 }
}
