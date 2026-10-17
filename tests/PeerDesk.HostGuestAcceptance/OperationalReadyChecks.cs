using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static async Task RunOperationalReady(MainForm host){
  Find<TextBox>(host,"ProfileJson").Text=rawProfile;OwnerTab(host,"OwnerSettingsPage");await OwnerClick(host,"HostLoadSettings",owner);
  Check(Find<Control>(host,"HostPasswordState").Text.Contains("\uC124\uC815\uB428"),"Actual operational host has a configured participant password");
  Check(Find<Control>(host,"HostCurrentDisplayName").Text=="ChanServer-1","Actual operational host shows the requested display name");
  Check((await Http("GET","/api/v1/directory/"+Alias)).Status==200,"Published guest connection name is available on the live directory");
  var probe=await Http("POST","/api/v1/register",new{invitationCode=join});Check(probe.Status==200,"Current protected participant password is verified before interactive guest steps");
  var pd=Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(probe.Body);string probeId=(string)pd["deviceId"];
  Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=probeId},owner)).Status==200,"Only the password-preflight probe registration is removed");
  host.Hide();SettingsState a=null,b=null;string session=null;ControlPlaneClient initiator=null;
  try {
   foreach(int n in new[]{1,2}){
    string path=Path.Combine(output,"ready-"+n+"-"+Guid.NewGuid().ToString("N")+".json");
    using(var form=new MainForm(Path.Combine(output,"history-ready-"+n+".json"),path,bootstrap)){await Show(form);await Join(form,join);var state=new SettingsStore(path).Load();Check(state.IsRegistered,"Actual guest "+n+" joins by live alias and current participant password");if(n==1)a=state;else b=state;await Close(form);}
   }
   initiator=Client(a);var target=Client(b);var request=await Task.Run(()=>initiator.Request(target.DeviceId,target.TemporaryPassword));session=request.SessionId;Check(request.Status=="Pending","Operational remote request still requires explicit target approval");
   Check((await Task.Run(()=>target.Decide(session,true))).Status=="Approved","Owned target explicitly approves the actual AWS request");
   Check((await Task.Run(()=>initiator.Connect(session))).Status=="Connected","Two isolated clients actually connect through the current AWS service");
   Check((await Task.Run(()=>initiator.Revoke(session))).Status=="Revoked","Owned operational test connection is explicitly terminated");session=null;
  }finally{
   if(session!=null&&initiator!=null){try{await Task.Run(()=>initiator.Revoke(session));}catch{Console.Error.WriteLine("Owned session cleanup deferred to exact device revoke");}}
   foreach(var state in new[]{a,b})if(state!=null)Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=state.DeviceId},owner)).Status==200,"Only an owned operational test registration is removed");
  }
  Console.WriteLine("AWS_OPERATIONAL_READY_PASS count="+count);
 }
}
