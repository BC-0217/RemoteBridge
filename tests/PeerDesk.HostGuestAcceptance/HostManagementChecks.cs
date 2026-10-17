using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static void OwnerTab(MainForm host,string page) {
  SettingsPage(host);Find<ComboBox>(host,"ServerRole").SelectedIndex=1;
  var t=Find<TabControl>(host,"HostSettingsTabs");t.SelectedTab=t.TabPages.Cast<TabPage>().Single(p=>p.Name=="HostOwnerPage");
  var m=Find<TabControl>(host,"OwnerManagementTabs");m.SelectedTab=m.TabPages.Cast<TabPage>().Single(p=>p.Name==page);host.PerformLayout();
 }
 static async Task OwnerClick(MainForm host,string name,string key) {
  Find<TextBox>(host,"OwnerSecret").Text=key;var b=Find<Button>(host,name);Check(b.Visible&&b.Enabled,"Real visible action enabled "+name);b.PerformClick();await Until(()=>b.Enabled,"Owner action completes "+name);
 }
 static async Task RunHostManagement(MainForm host,bool authProbe=false) {
  string live=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","settings.json");byte[] before;using(var h=SHA256.Create())before=h.ComputeHash(File.ReadAllBytes(live));
  Find<TextBox>(host,"ProfileJson").Text=rawProfile;OwnerTab(host,"OwnerSettingsPage");
  Check(Find<Control>(host,"HostCurrentDisplayName").Text!="ChanServer-1","No fabricated owner configuration before authenticated load");
  await OwnerClick(host,"HostLoadSettings",owner);
  Check(Find<TextBox>(host,"HostDisplayName").Text=="ChanServer-1","Authenticated owner load fills actual AWS display name");
  Check(Find<Control>(host,"HostCurrentDisplayName").Text.Contains("ChanServer-1"),"Current display name is visibly retained");
  Check(Find<Control>(host,"HostPasswordState").Text.Contains("\uC124\uC815\uB428"),"Existing participant password configured state is visible");
  Check(Find<TextBox>(host,"OwnerSecret").Text.Length==0,"Owner key clears after authenticated load");
  await OwnerClick(host,"HostLoadSettings",new string('X',43));Check(Find<Label>(host,"ConnectionStatus").Text.Contains("401"),"Actual wrong owner key shows inline HTTP401 and restores editable UI");
  Check(!Find<Control>(host,"HostCurrentDisplayName").Text.Contains("ChanServer-1"),"Wrong owner authentication clears stale verified summary");
  if(authProbe){Console.WriteLine("HOST_OWNER_AUTH_PROBE_PASS count="+count);return;}
  await OwnerClick(host,"HostLoadSettings",owner);
  Find<TextBox>(host,"HostDisplayName").Text="ChanServer-1";Find<TextBox>(host,"HostJoinPassword").Clear();
  await OwnerClick(host,"HostSaveSettings",owner);
  var read=await Http("GET","/api/v1/owner/settings",null,owner);var d=Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(read.Body);
  Check(read.Status==200&&(string)d["displayName"]=="ChanServer-1"&&(bool)d["joinPasswordConfigured"],"Blank new-password owner save preserves actual configured password");
  Check(Find<Control>(host,"HostCurrentDisplayName").Text.Contains("ChanServer-1"),"Save result remains visible rather than only transient status");
  Check(Find<TextBox>(host,"OwnerSecret").Text.Length==0&&Find<TextBox>(host,"HostJoinPassword").Text.Length==0,"Save clears both sensitive inputs");
  Capture(host,"host-current-settings.png");
  OwnerTab(host,"OwnerPublishingPage");Check(!Find<Button>(host,"CopyHostAlias").Enabled,"Unverified alias cannot be copied");
  Find<TextBox>(host,"HostAliasInput").Text=Alias;var verify=Find<Button>(host,"VerifyHostAlias");verify.PerformClick();await Until(()=>verify.Enabled,"Actual directory alias verification");
  Check(Find<Control>(host,"HostPublishedAlias").Text.Contains(Alias)&&Find<Button>(host,"CopyHostAlias").Enabled,"Actual directory profile-bound alias is visibly shown and copyable");
  Find<Button>(host,"CopyHostAlias").PerformClick();Check(Clipboard.GetText().Contains(Alias),"Verified guest alias copy button writes the actual alias");
  Find<TextBox>(host,"HostAliasInput").Text="missing-owned-test-alias";verify.PerformClick();await Until(()=>verify.Enabled,"Unknown alias verification completes");Check(Find<Label>(host,"ConnectionStatus").Text.Contains("404"),"Actual unknown alias shows inline HTTP404 and restores editable UI");
  Check(!Find<Button>(host,"CopyHostAlias").Enabled,"Failed alias lookup does not leave stale verified copy enabled");
  Find<TextBox>(host,"HostAliasInput").Text=Alias;verify.PerformClick();await Until(()=>verify.Enabled,"Known alias verification restored");
  Capture(host,"host-published-alias.png");
  foreach(var size in new[]{new Size(760,620),new Size(900,720)}){host.Size=size;
   foreach(var page in new[]{"OwnerSettingsPage","OwnerPublishingPage","OwnerDevicesPage"}){OwnerTab(host,page);
    var names=page=="OwnerSettingsPage"?new[]{"OwnerSecret","HostLoadSettings","HostCurrentDisplayName","HostPasswordState","HostDisplayName","HostJoinPassword","HostSaveSettings"}:page=="OwnerPublishingPage"?new[]{"OwnerSecret","HostLoadSettings","HostAliasInput","VerifyHostAlias","HostPublishedAlias","CopyHostAlias","HostPublishGrant","HostPublish"}:new[]{"OwnerSecret","HostLoadSettings","OwnerDevices","OwnerListDevices","RevokeDevice"};
    foreach(var name in names)Check(Fits(Find<Control>(host,name)),"Host "+size.Width+" visible ancestor bounds "+name);
    Capture(host,page+"-"+size.Width+".png");
   }
  }
  OwnerTab(host,"OwnerSettingsPage");Find<TextBox>(host,"ProfileJson").Text=rawProfile.Replace(profile.CertificateSha256,new string('B',64));
  Check(!Find<Control>(host,"HostCurrentDisplayName").Text.Contains("ChanServer-1")&&!Find<Button>(host,"CopyHostAlias").Enabled,"Profile binding change invalidates previous owner and alias summaries");
  Find<TextBox>(host,"ProfileJson").Text=rawProfile;
  host.Hide();string path=Path.Combine(output,"guest-proof-"+Guid.NewGuid().ToString("N")+".json");SettingsState state=null;
  try {using(var b=new MainForm(Path.Combine(output,"guest-proof-history.json"),path,bootstrap)){await Show(b);await Join(b,join);state=new SettingsStore(path).Load();Check(state.IsRegistered,"Original participant password still registers a real AWS guest after name-only save");await Close(b);}}
  finally {if(state!=null)Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=state.DeviceId},owner)).Status==200,"Only this run's guest proof registration is removed");}
  using(var h=SHA256.Create())Check(before.SequenceEqual(h.ComputeHash(File.ReadAllBytes(live))),"Existing default user settings remain byte-for-byte unchanged");
  Console.WriteLine("HOST_MANAGEMENT_AWS_PASS count="+count);
 }
}
