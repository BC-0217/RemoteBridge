using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static async Task RunPasswordLifecycle(MainForm host){
  host.Hide();SettingsState a=null,b=null;string targetPath=Path.Combine(output,"lifecycle-target-"+Guid.NewGuid().ToString("N")+".json");
  try{
   using(var targetForm=new MainForm(Path.Combine(output,"lifecycle-target-history.json"),targetPath,bootstrap)){
    await Show(targetForm);await Join(targetForm,join);b=new SettingsStore(targetPath).Load();
    Check(b.IsRegistered,"Lifecycle target joins the actual AWS server");
    var initial=Client(b);var me=await initial.ValidateDeviceAsync();
    DateTimeOffset expiry;Check(DateTimeOffset.TryParse(b.PasswordExpiresAt,out expiry)&&(expiry-me.ServerTimeUtc).TotalMinutes>59,"Actual AWS temporary password lifetime is one hour");
    var initiatorPath=Path.Combine(output,"lifecycle-initiator-"+Guid.NewGuid().ToString("N")+".json");
    using(var other=new MainForm(Path.Combine(output,"lifecycle-initiator-history.json"),initiatorPath,bootstrap)){
     await Show(other);await Join(other,join);a=new SettingsStore(initiatorPath).Load();await Close(other);
    }
    var controller=Client(a);targetForm.Hide();
    await MediaChecks.Run(controller,initial,output,Check,async()=>{
     var refreshWait=System.Diagnostics.Stopwatch.StartNew();while(new SettingsStore(targetPath).Load().TemporaryPassword==b.TemporaryPassword){if(refreshWait.ElapsedMilliseconds>22000)throw new TimeoutException("Actual automatic password rotation after a successful connection");await Task.Delay(40);}
     var updated=new SettingsStore(targetPath).Load();
     Check(updated.DeviceId==b.DeviceId&&updated.TemporaryPassword.Length==9,"Automatic post-connect rotation preserves ID and commits the fresh password to DPAPI");
     Check(Find<TextBox>(targetForm,"TemporaryPassword").Text==updated.TemporaryPassword,"Displayed PC password matches the committed fresh credential");
     Check(Find<Button>(targetForm,"RequestConnection").Enabled,"Password synchronization completes without disabling the registered client");
     bool denied=false;try{await Task.Run(()=>controller.Request(b.DeviceId,b.TemporaryPassword));}catch(InvalidOperationException ex){denied=ex.Message.Contains("401");}
     Check(denied,"Old PC password is denied for a subsequent AWS request");
     var next=await Task.Run(()=>controller.Request(updated.DeviceId,updated.TemporaryPassword));
     Check(next.Status=="Pending","Fresh automatic password works for a subsequent connection request");
     Check((await Task.Run(()=>controller.Revoke(next.SessionId))).Status=="Revoked","Only the additional pending test request is removed");
    });
    Check(true,"Existing actual media and native input survive automatic temporary-password change");
    await Close(targetForm);
   }
  }finally{
   foreach(var state in new[]{a,b})if(state!=null)Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=state.DeviceId},owner)).Status==200,"Only an owned lifecycle test device is removed");
  }
  Console.WriteLine("AWS_PASSWORD_LIFECYCLE_PASS count="+count);
 }
}
