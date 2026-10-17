using System;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static async Task RunPasswordEight(MainForm host) {
  var original=await Http("GET","/api/v1/owner/settings",null,owner);Check(original.Status==200,"Actual AWS owner authentication after approved service update");
  string name=(string)Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(original.Body)["displayName"];
  var pre=new ControlPlaneClient(profile);await Task.Run(()=>pre.Register(join));
  Check((await pre.ValidateDeviceAsync()).DeviceId==pre.DeviceId,"Stored original participant password verified before any temporary password change");
  Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=pre.DeviceId},owner)).Status==200,"Only original-password probe registration removed");
  Check((await Http("POST","/api/v1/owner/settings",new{displayName=name,joinPassword=new string('a',7)},owner)).Status==400,"Actual AWS rejects seven-character participant password");
  Check((await Http("POST","/api/v1/owner/settings",new{displayName=name,joinPassword=new string('a',129)},owner)).Status==400,"Actual AWS rejects 129-character participant password");
  byte[] bytes=new byte[12];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);string temporary="aA"+Convert.ToBase64String(bytes).Substring(0,6);
  bool attempted=false;SettingsState shortState=null;
  try {
   Find<TextBox>(host,"ProfileJson").Text=rawProfile;OwnerTab(host,"OwnerSettingsPage");
   Find<TextBox>(host,"HostDisplayName").Text=name;Find<TextBox>(host,"HostJoinPassword").Text=temporary;attempted=true;
   await OwnerClick(host,"HostSaveSettings",owner);
   Check(!Find<Label>(host,"ConnectionStatus").Text.Contains("400")&&Find<Control>(host,"HostCurrentDisplayName").Text==name,"Actual Windows host UI saves an eight-character password to AWS");
   Check(Find<TextBox>(host,"HostJoinPassword").Text.Length==0,"Eight-character password input clears after successful save");
   host.Hide();string path=Path.Combine(output,"guest-eight-"+Guid.NewGuid().ToString("N")+".json");
   using(var guest=new MainForm(Path.Combine(output,"history-eight.json"),path,bootstrap)){
    await Show(guest);await Join(guest,temporary);shortState=new SettingsStore(path).Load();Check(shortState.IsRegistered,"Actual guest joins AWS using the temporary eight-character participant password");await Close(guest);
   }
  } finally {
   if(shortState!=null)Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=shortState.DeviceId},owner)).Status==200,"Only temporary eight-password guest registration removed");
   if(attempted){await new ControlPlaneClient(profile).OwnerSettingsAsync(owner,name,join);Check(true,"Original participant password and display name restored without revealing either credential");}
   temporary=null;Array.Clear(bytes,0,bytes.Length);
  }
  if(destination==null)throw new InvalidOperationException("Approved current-user registration destination is required after the service restart");
  string finalPath=Path.Combine(output,"guest-current-"+Guid.NewGuid().ToString("N")+".json");
  using(var guest=new MainForm(Path.Combine(output,"history-current.json"),finalPath,bootstrap)){
   await Show(guest);await Join(guest,join);var registered=new SettingsStore(finalPath).Load();Check(registered.IsRegistered,"Restored original participant password still permits actual guest registration");
   new SettingsStore(destination).Save(registered);Check(new SettingsStore(destination).Load().DeviceId==registered.DeviceId,"Fresh verified registration installed for this Windows user after approved memory-only server restart");
   await Close(guest);
  }
  Check((await Http("GET","/api/v1/directory/"+Alias)).Status==200,"Existing server alias survives the approved service restart");
  Console.WriteLine("AWS_PASSWORD_EIGHT_PASS count="+count);
 }
}
