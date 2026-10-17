using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static partial class Program {
 static int count;static string rawProfile,owner,grant,join,output,destination;
 const string Alias="song-vpn-server1";
 static ServerProfile profile;static DirectoryBootstrap bootstrap;
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
 static T Find<T>(Control f,string name)where T:Control{return f.Controls.Find(name,true).OfType<T>().Single();}
 static void Check(bool pass,string name){if(!pass)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
 static async Task Until(Func<bool> p,string step){var sw=Stopwatch.StartNew();while(!p()){if(sw.ElapsedMilliseconds>15000)throw new TimeoutException(step);await Task.Delay(30);}}
 static void SettingsPage(MainForm f){var tabs=Find<TabControl>(f,"ServerSettingsTabs");tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(x=>x.Name=="ServerSettingsPage");}
 static async Task Show(MainForm f){var ready=new TaskCompletionSource<bool>();f.Shown+=(s,e)=>ready.TrySetResult(true);f.Show();await ready.Task;}
 static async Task Close(MainForm f){f.Close();await Until(()=>f.IsDisposed,"Owned UI closes asynchronously");}
 static bool Pin(object sender,X509Certificate certificate,X509Chain chain,SslPolicyErrors errors){
  if(certificate==null||(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))!=0||(errors&~SslPolicyErrors.RemoteCertificateChainErrors)!=SslPolicyErrors.None)return false;
  using(var cert=new X509Certificate2(certificate)){if(DateTime.UtcNow<cert.NotBefore.ToUniversalTime()||DateTime.UtcNow>cert.NotAfter.ToUniversalTime())return false;using(var sha=SHA256.Create())return String.Equals(BitConverter.ToString(sha.ComputeHash(cert.RawData)).Replace("-",""),profile.CertificateSha256,StringComparison.OrdinalIgnoreCase);}
 }
 sealed class Reply{internal int Status;internal string Body;}
 static async Task<Reply> Http(string method,string path,object body=null,string ownerKey=null){
  return await Task.Run(()=>{ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;var req=(HttpWebRequest)WebRequest.Create(profile.RendezvousUrl.TrimEnd('/')+path);req.Method=method;req.Proxy=null;req.AllowAutoRedirect=false;req.Timeout=12000;req.ReadWriteTimeout=12000;req.ServerCertificateValidationCallback=Pin;
   if(ownerKey!=null)req.Headers["X-PeerDesk-Owner"]=ownerKey;
   if(body!=null){var bytes=Encoding.UTF8.GetBytes(Json.Serialize(body));req.ContentType="application/json";req.ContentLength=bytes.Length;using(var s=req.GetRequestStream())s.Write(bytes,0,bytes.Length);}
   try{using(var res=(HttpWebResponse)req.GetResponse())using(var s=new StreamReader(res.GetResponseStream()))return new Reply{Status=(int)res.StatusCode,Body=s.ReadToEnd()};}
   catch(WebException ex){var res=ex.Response as HttpWebResponse;if(res==null)throw;using(res)using(var s=new StreamReader(res.GetResponseStream()))return new Reply{Status=(int)res.StatusCode,Body=s.ReadToEnd()};}
  });
 }
 static ControlPlaneClient Client(SettingsState state){var c=new ControlPlaneClient(ServerProfile.Parse(state.ProfileJson));c.RestoreCredentials(state.DeviceId,state.BearerToken,state.TemporaryPassword,state.PasswordExpiresAt);return c;}
 static void Capture(MainForm f,string name){var pw=Find<TextBox>(f,"TemporaryPassword");bool was=pw.UseSystemPasswordChar;pw.UseSystemPasswordChar=true;try{f.PerformLayout();using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(Path.Combine(output,name),ImageFormat.Png);}}finally{pw.UseSystemPasswordChar=was;}}
 static bool Fits(Control c){if(!c.Visible||c.Width<=0||c.Height<=0)return false;var r=c.RectangleToScreen(c.ClientRectangle);for(Control p=c.Parent;p!=null;p=p.Parent)if(!p.RectangleToScreen(p.ClientRectangle).Contains(r))return false;return true;}
 static async Task Join(MainForm f,string password,string alias=Alias){
  SettingsPage(f);Find<ComboBox>(f,"ServerRole").SelectedIndex=0;
  Find<TextBox>(f,"GuestServerAlias").Text=alias;Find<TextBox>(f,"GuestJoinPassword").Text=password;
  Find<Button>(f,"GuestJoin").PerformClick();
  await Until(()=>Find<Button>(f,"RequestConnection").Enabled,"Guest real name/password registration");
 }
 static async Task Run(MainForm host){
  await Until(()=>host.Enabled,"Host UI initial ready");SettingsPage(host);Find<ComboBox>(host,"ServerRole").SelectedIndex=1;
  Find<TextBox>(host,"ProfileJson").Text=rawProfile;OwnerTab(host,"OwnerSettingsPage");
  Find<TextBox>(host,"OwnerSecret").Text=owner;Find<TextBox>(host,"HostDisplayName").Text=profile.Name;Find<TextBox>(host,"HostJoinPassword").Text=join;
  var save=Find<Button>(host,"HostSaveSettings");save.PerformClick();await Until(()=>save.Enabled,"Host settings save completes");
  var settings=await Http("GET","/api/v1/owner/settings",null,owner);
  Check(settings.Status==200&&settings.Body.Contains("true"),"Real owner UI sets participant password on AWS");
  Check(Find<TextBox>(host,"OwnerSecret").Text.Length==0&&Find<TextBox>(host,"HostJoinPassword").Text.Length==0,"Host UI clears private owner and participant password inputs");
  Check((await Http("GET","/api/v1/owner/settings",null,"invalid-owned-test-secret")).Status==401,"Host role does not grant owner API authority without secret");
  var existing=await Http("GET","/api/v1/directory/"+Alias);
  if(existing.Status==404){
   OwnerTab(host,"OwnerPublishingPage");Find<TextBox>(host,"OwnerSecret").Text=owner;Find<TextBox>(host,"HostPublishGrant").Text=grant;
   var publish=Find<Button>(host,"HostPublish");publish.PerformClick();await Until(()=>publish.Enabled,"Host publication completes");
   existing=await Http("GET","/api/v1/directory/"+Alias);
   Check(existing.Status==200,"Actual owner UI publishes approved server alias through key proof");Check(Find<TextBox>(host,"OwnerSecret").Text.Length==0&&Find<TextBox>(host,"HostPublishGrant").Text.Length==0,"Host publication clears owner secret and one-use grant inputs");
  }else{Check(existing.Status==200,"Existing approved alias is available");Console.WriteLine("PUBLICATION_EVIDENCE_REUSED_IF_PRIOR_OWNED_RUN");}
  var advertised=Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(existing.Body);
  var discovered=ServerProfile.Parse((string)advertised["profileJson"]);Check(discovered.CertificateSha256==profile.CertificateSha256&&discovered.RendezvousUrl==profile.RendezvousUrl,"Directory returns expected endpoint and full certificate pin");
  Check(!existing.Body.Contains(owner)&&!existing.Body.Contains(join)&&!existing.Body.Contains(grant),"Public directory response contains no owner key participant PW or publish grant");
  Capture(host,"aws-host-settings.png");
  host.Hide();
  string bPath=Path.Combine(output,"guest-b-"+Guid.NewGuid().ToString("N")+".json"),cPath=Path.Combine(output,"guest-c-"+Guid.NewGuid().ToString("N")+".json");
  SettingsState bState,cState;
  using(var b=new MainForm(Path.Combine(output,"history-b.json"),bPath,bootstrap)){
   await Show(b);SettingsPage(b);Check(Find<ComboBox>(b,"ServerRole").SelectedIndex==0,"Fresh app defaults to guest role");
   Check(!Find<TextBox>(b,"ServerAddress").Visible&&!Find<TextBox>(b,"ServerFingerprint").Visible&&!Find<TextBox>(b,"OwnerSecret").Visible,"Guest UI hides server address fingerprint and owner secret fields");
   b.Size=new Size(760,620);b.PerformLayout();
   foreach(string name in new[]{"ServerRole","GuestServerAlias","GuestJoinPassword","GuestJoin"})Check(Fits(Find<Control>(b,name)),"Minimum guest bounds "+name);
   b.Size=new Size(900,720);
   await Join(b,join);bState=new SettingsStore(bPath).Load();
   Check(bState.IsRegistered&&bState.DeviceId.Length==10&&bState.TemporaryPassword.Length==9,"Guest B gets actual short credentials using only server name and PW");
   Check(Find<TextBox>(b,"GuestJoinPassword").Text.Length==0,"Guest clears participant password after successful join");
   var tabs=Find<TabControl>(b,"ServerSettingsTabs");tabs.SelectedIndex=0;Capture(b,"aws-guest-registered.png");
   string stored=File.ReadAllText(bPath);Check(!stored.Contains(join)&&!stored.Contains(owner)&&!stored.Contains(bState.BearerToken)&&!stored.Contains(bState.TemporaryPassword),"Guest DPAPI file excludes plaintext credentials");
   await Close(b);
  }
  using(var resumed=new MainForm(Path.Combine(output,"history-b.json"),bPath,bootstrap)){
   await Show(resumed);await Until(()=>Find<Button>(resumed,"RequestConnection").Enabled,"Guest resume server identity verification");
   var renewed=new SettingsStore(bPath).Load();Check(renewed.DeviceId==bState.DeviceId&&renewed.TemporaryPassword!=bState.TemporaryPassword,"Guest restarts with same ID and fresh temporary PC password");bState=renewed;
   await Close(resumed);
  }
  using(var c=new MainForm(Path.Combine(output,"history-c.json"),cPath,bootstrap)){
   await Show(c);await Join(c,join);cState=new SettingsStore(cPath).Load();Check(cState.IsRegistered&&cState.DeviceId!=bState.DeviceId,"Guest C independently joins same named server");
   await Close(c);
  }
  Check((await Http("POST","/api/v1/register",new{invitationCode="incorrect-test-participant-password"})).Status==401,"Wrong participant password is denied by target server");
  var bClient=Client(bState);var cClient=Client(cState);
  await MediaChecks.Run(bClient,cClient,output,Check);
  var active=await Task.Run(()=>bClient.Request(cClient.DeviceId,cClient.TemporaryPassword));await Task.Run(()=>cClient.Decide(active.SessionId,true));await Task.Run(()=>bClient.Connect(active.SessionId));
  Check((await Http("POST","/api/v1/owner/devices/revoke",new{deviceId=cClient.DeviceId},owner)).Status==200,"Owner can revoke an individual registered guest");
  bool revoked=false;try{await cClient.ValidateDeviceAsync();}catch(UnauthorizedAccessException){revoked=true;}Check(revoked,"Revoked guest bearer is actually rejected");
  Check((await bClient.ValidateDeviceAsync()).DeviceId==bState.DeviceId,"Revoking C preserves guest B registration");
  if(!String.IsNullOrEmpty(destination)){
   if(File.Exists(destination)&&!File.Exists(Path.Combine(Path.GetDirectoryName(destination),"settings.phase7-before-host-guest-20261017.json")))throw new InvalidOperationException("Existing default settings lack approved phase7 backup");
   new SettingsStore(destination).Save(bState);Check(new SettingsStore(destination).Load().DeviceId==bState.DeviceId,"Verified guest B registration installed for current Windows user");
  }
  Console.WriteLine("HOST_GUEST_AWS_ACCEPTANCE_PASS count="+count);
 }
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=new UTF8Encoding(false);if(args.Length==2&&args[0]=="--room-ui")return RoomUiChecks.Run(args[1]);if(args.Length==2&&args[0]=="--help-ui")return HelpUiChecks.Run(args[1]);if(args.Length==2&&args[0]=="--visual")return VisualChecks.Run(args[1]);rawProfile=Console.ReadLine();owner=Console.ReadLine();grant=Console.ReadLine();join=Console.ReadLine();
  output=Path.GetFullPath(args[0]);destination=args.Length>1&&args[1]!="-"?Path.GetFullPath(args[1]):null;Directory.CreateDirectory(output);
  profile=ServerProfile.Parse(rawProfile);if(profile.RendezvousUrl!="https://3.112.217.41:8443")throw new InvalidOperationException("This owned AWS fixture is restricted to the approved Tokyo endpoint");
  bootstrap=new DirectoryBootstrap{RegistryUrl=profile.RendezvousUrl,CertificateSha256=profile.CertificateSha256,RegistryIdentity="peerdesk-tokyo-v1"};
  if(args.Length>2&&args[2]=="--join-preflight"){var probe=Http("POST","/api/v1/register",new{invitationCode=join}).GetAwaiter().GetResult();Console.WriteLine("CURRENT_PARTICIPANT_PASSWORD_HTTP_STATUS="+probe.Status);if(probe.Status==200){var d=Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(probe.Body);var clean=Http("POST","/api/v1/owner/devices/revoke",new{deviceId=(string)d["deviceId"]},owner).GetAwaiter().GetResult();Console.WriteLine("OWNED_PREFLIGHT_DEVICE_REVOKE_STATUS="+clean.Status);}return probe.Status==200?0:2;}
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int exit=1;
  var host=new MainForm(Path.Combine(output,"host-history.json"),Path.Combine(output,"host-"+Guid.NewGuid().ToString("N")+".json"),bootstrap);
  host.Shown+=async(s,e)=>{try{if(args.Length>2&&args[2]=="--room-config-only")await RunRoomCreator(host,true);else if(args.Length>2&&args[2]=="--room-creator")await RunRoomCreator(host);else if(args.Length>2&&args[2]=="--password-lifecycle")await RunPasswordLifecycle(host);else if(args.Length>2&&args[2]=="--operational-ready")await RunOperationalReady(host);else if(args.Length>2&&args[2]=="--password-eight")await RunPasswordEight(host);else if(args.Length>2&&args[2]=="--host-auth-probe")await RunHostManagement(host,true);else if(args.Length>2&&args[2]=="--host-management")await RunHostManagement(host);else if(args.Length>2&&args[2]=="--guest-only")await RunGuestOnly(host);else await Run(host);exit=0;}catch(Exception ex){Console.Error.WriteLine("HOST_GUEST_AWS_ACCEPTANCE_FAIL "+ex.GetType().Name+": "+ex.Message);}finally{host.Close();}};
  Application.Run(host);return exit;
 }
}
