using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static async Task RunGuestOnly(MainForm shell) {
  shell.Hide();string livePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","settings.json");byte[] liveHash;using(var sha=SHA256.Create())liveHash=sha.ComputeHash(File.ReadAllBytes(livePath));
  Check((await Http("GET","/api/v1/directory/"+Alias)).Status==200,"Existing approved server alias resolves without republishing");
  string bPath=Path.Combine(output,"guest-b-"+Guid.NewGuid().ToString("N")+".json"),cPath=Path.Combine(output,"guest-c-"+Guid.NewGuid().ToString("N")+".json");
  SettingsState bState,cState;
  using(var b=new MainForm(Path.Combine(output,"history-b.json"),bPath,bootstrap)){
   await Show(b);Check(b.Text.StartsWith("RemoteBridge",StringComparison.Ordinal),"Main product window uses RemoteBridge brand");
   SettingsPage(b);Check(Find<ComboBox>(b,"ServerRole").SelectedIndex==0,"Fresh RemoteBridge defaults to Guest");
   Check(!Find<TextBox>(b,"ServerAddress").Visible&&!Find<TextBox>(b,"ServerFingerprint").Visible,"Guest does not need endpoint or pin input");
   await Join(b,join);bState=new SettingsStore(bPath).Load();
   Check(bState.IsRegistered&&bState.DeviceId.Length==10&&bState.TemporaryPassword.Length==9,"RemoteBridge Guest B joins AWS by server name and participant password");
   Check(Find<TextBox>(b,"GuestJoinPassword").Text.Length==0,"Guest participant password clears after join");
   Find<TabControl>(b,"ServerSettingsTabs").SelectedIndex=0;Capture(b,"remotebridge-guest-b.png");
   await Close(b);
  }
  using(var c=new MainForm(Path.Combine(output,"history-c.json"),cPath,bootstrap)){
   await Show(c);await Join(c,join);cState=new SettingsStore(cPath).Load();
   Check(cState.IsRegistered&&cState.DeviceId!=bState.DeviceId,"RemoteBridge Guest C obtains independent actual registration");
   await Close(c);
  }
  var bClient=Client(bState);var cClient=Client(cState);
  try {
   Check((await bClient.ValidateDeviceAsync()).DeviceId==bState.DeviceId,"AWS authenticates actual Guest B");
   await MediaChecks.Run(bClient,cClient,output,Check);
  }
  finally {
   // Only registrations created by this exact owned run are removed. Default user registration is never overwritten.
   var cb=await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=cState.DeviceId},owner);
   var bb=await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=bState.DeviceId},owner);
   Check(cb.Status==200&&bb.Status==200,"Owned temporary test devices cleaned without changing existing user registration");
  }
  using(var sha=SHA256.Create())Check(liveHash.SequenceEqual(sha.ComputeHash(File.ReadAllBytes(livePath))),"Existing user registration settings are byte-for-byte unchanged by this test");Console.WriteLine("REMOTEBRIDGE_GUEST_AWS_ACCEPTANCE_PASS count="+count);
 }
}
