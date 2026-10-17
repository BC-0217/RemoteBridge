using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static async Task RunRoomCreator(MainForm creator,bool configOnly=false){
  Find<TextBox>(creator,"ProfileJson").Text=rawProfile;OwnerTab(creator,"RoomSettingsPage");
  await OwnerClick(creator,"RoomLoad",owner);
  var before=await Http("GET","/api/v1/owner/room",null,owner);
  Check(before.Status==200,"Actual owner room endpoint is available after the approved deployment");
  var prior=Json.Deserialize<Dictionary<string,object>>(before.Body);
  Check((bool)prior["selfPublishEnabled"],"Self-publishing is enabled only on the approved owned server");
  await OwnerClick(creator,"RoomLoad",new string('X',43));
  Check(Find<Label>(creator,"ConnectionStatus").Text.Contains("401"),"Wrong owner authentication remains denied in the simple creator UI");
  var unchanged=await Http("GET","/api/v1/owner/room",null,owner);
  Check(unchanged.Status==200&&unchanged.Body==before.Body,"Rejected owner attempt cannot change existing room information");
  Find<TextBox>(creator,"RoomServerId").Text="ChanServer-1";Find<TextBox>(creator,"RoomJoinPassword").Clear();
  await OwnerClick(creator,"RoomSave",owner);
  var saved=await Http("GET","/api/v1/owner/room",null,owner);var state=Json.Deserialize<Dictionary<string,object>>(saved.Body);
  Check(saved.Status==200&&(string)state["serverId"]=="chanserver-1"&&(bool)state["joinPasswordConfigured"],"Actual creator saves the normalized shared server ID while preserving the participant password");
  Check(Find<TextBox>(creator,"RoomServerId").Text=="chanserver-1","UI shows the actual canonical ID rather than a separate display name");
  Check(Find<TextBox>(creator,"OwnerSecret").Text.Length==0&&Find<TextBox>(creator,"RoomJoinPassword").Text.Length==0,"Submitted creator action clears private inputs");
  Check(Find<Button>(creator,"RoomCopy").Enabled,"Verified server ID remains copyable without revealing an unretrievable existing password");
  Find<Button>(creator,"RoomCopy").PerformClick();Check(Clipboard.GetText()=="chanserver-1","Blank-password save copies only the verified server ID and never fabricates a password");
  var current=await Http("GET","/api/v1/directory/chanserver-1");var legacy=await Http("GET","/api/v1/directory/"+Alias);
  Check(current.Status==200&&legacy.Status==200,"New ID is discoverable and existing shared ID remains usable");
  Check(!current.Body.Contains(owner)&&!current.Body.Contains(join),"Public lookup never contains the owner key or shared password");
  var record=Json.Deserialize<Dictionary<string,object>>(current.Body);var actual=ServerProfile.Parse((string)record["profileJson"]);
  Check(actual.RendezvousUrl==profile.RendezvousUrl&&actual.CertificateSha256==profile.CertificateSha256,"New shared ID resolves to the exact approved server URL and full TLS pin");
  Capture(creator,"creator-live-configured.png");if(configOnly){Console.WriteLine("AWS_ROOM_CONFIGURATION_PASS count="+count);return;}creator.Hide();SettingsState a=null,b=null;
  try{
   for(int n=0;n<2;n++){
    var path=Path.Combine(output,"room-guest-"+n+"-"+Guid.NewGuid().ToString("N")+".json");
    using(var guest=new MainForm(Path.Combine(output,"room-history-"+n+".json"),path,bootstrap)){
     await Show(guest);await Join(guest,join,n==0?"ChanServer-1":"chanserver-1");var registration=new SettingsStore(path).Load();
     Check(registration.IsRegistered,"A real guest joins using only the new shared server ID and preserved password");
     if(n==0)a=registration;else b=registration;await Close(guest);
    }
   }
   await MediaChecks.Run(Client(a),Client(b),output,Check);
  }finally{
   foreach(var registration in new[]{a,b})if(registration!=null)Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=registration.DeviceId},owner)).Status==200,"Only this run's canonical-ID test PC is removed");
  }
  if(!String.IsNullOrEmpty(destination)){
   using(var currentPcUi=new MainForm(Path.Combine(Path.GetDirectoryName(destination),"history.json"),destination,bootstrap)){
    await Show(currentPcUi);await Until(()=>Find<Button>(currentPcUi,"GuestJoin").Enabled,"Current PC is ready to rejoin after the approved service restart");
    await Join(currentPcUi,join,"chanserver-1");Check(new SettingsStore(destination).Load().IsRegistered,"Current PC is actually re-registered through the final UI without saving the shared password");await Close(currentPcUi);
   }
  }
  Console.WriteLine("AWS_ROOM_CREATOR_PASS count="+count);
 }
}
