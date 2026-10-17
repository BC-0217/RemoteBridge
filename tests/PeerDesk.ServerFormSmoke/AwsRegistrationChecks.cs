using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeerDesk.Windows;

internal static class AwsRegistrationChecks {
 static T Find<T>(Control root,string name) where T:Control { return (T)root.Controls.Find(name,true).Single(); }
 static async Task Wait(Func<bool> predicate) {
  var timer=System.Diagnostics.Stopwatch.StartNew();
  while(!predicate()){if(timer.ElapsedMilliseconds>15000)throw new TimeoutException("AWS UI resume");await Task.Delay(20);}
 }
 static WebException Web(Exception ex){for(var e=ex;e!=null;e=e.InnerException){var w=e as WebException;if(w!=null)return w;}return null;}
 static void Capture(Form form,string path){var pw=Find<TextBox>(form,"TemporaryPassword");bool old=pw.UseSystemPasswordChar;pw.UseSystemPasswordChar=true;try{form.PerformLayout();using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(path,ImageFormat.Png);}}finally{pw.UseSystemPasswordChar=old;}}
 internal static async Task Run(MainForm first,string settings,string invitation,string[] args,Action<bool,string> check) {
  if(args.Length==0)return;
  if(args.Length!=2)throw new ArgumentException("Expected fresh destination-settings path and owned evidence directory.");
  string destination=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
  if(File.Exists(destination)){var prior=new SettingsStore(destination).Load();if(!prior.IsRegistered||prior.DeviceId.Length!=32||prior.BoundServerUrl!="https://3.112.217.41:8443"||!File.Exists(Path.Combine(Path.GetDirectoryName(destination),"settings.phase6-before-short-id-20261016.json")))throw new InvalidOperationException("Refusing to replace unapproved or unbacked settings.");}
  Directory.CreateDirectory(output);
  var saved=new SettingsStore(settings).Load();
  var profile=ServerProfile.Parse(saved.ProfileJson);
  if(profile.RendezvousUrl!="https://3.112.217.41:8443")throw new InvalidOperationException("This optional acceptance mode is restricted to the approved Tokyo endpoint.");
  Capture(first,Path.Combine(output,"aws-server-settings.png"));
  first.Hide();
  using(var resumed=new MainForm(Path.Combine(output,"aws-history.json"),settings)) {
   resumed.StartPosition=FormStartPosition.Manual;resumed.Location=new Point(40,40);
   var shown=new TaskCompletionSource<bool>();resumed.Shown+=(s,e)=>shown.TrySetResult(true);
   resumed.Show();await shown.Task;await Wait(()=>Find<Button>(resumed,"RequestConnection").Enabled);
   var current=new SettingsStore(settings).Load();
   check(current.DeviceId==saved.DeviceId&&Find<TextBox>(resumed,"MyDeviceId").Text==saved.DeviceId,"AWS restart authenticates and retains the actual device ID");
   check(current.TemporaryPassword!=saved.TemporaryPassword&&!Find<TextBox>(resumed,"TemporaryPassword").UseSystemPasswordChar,"AWS restart rotates a fresh visible password");
   var owner=new ControlPlaneClient(profile);owner.RestoreCredentials(current.DeviceId,current.BearerToken,current.TemporaryPassword,current.PasswordExpiresAt);
   var badProfile=ServerProfile.Parse(saved.ProfileJson.Replace(profile.CertificateSha256,new string('0',64)));
   bool wrongPin=false;try{await new ControlPlaneClient(badProfile).CheckHealthAsync();}catch(Exception ex){var w=Web(ex);wrongPin=w!=null&&(w.Status==WebExceptionStatus.TrustFailure||w.Status==WebExceptionStatus.SecureChannelFailure);}
   check(wrongPin,"AWS wrong leaf pin is rejected at TLS boundary");
   await owner.CheckHealthAsync();check(true,"AWS correct leaf pin still reaches the same live service");
   bool wrongInvite=false;try{await Task.Run(()=>new ControlPlaneClient(profile).Register("invalid-test-invitation-20261016"));}catch(Exception ex){var w=Web(ex);var response=w==null?null:w.Response as HttpWebResponse;wrongInvite=response!=null&&response.StatusCode==HttpStatusCode.Unauthorized;}
   check(wrongInvite,"AWS wrong invitation is denied with actual HTTP401");
   var target=new ControlPlaneClient(profile);await Task.Run(()=>target.Register(invitation));
   var session=await Task.Run(()=>owner.Request(target.DeviceId,target.TemporaryPassword));
   check(session.Status=="Pending","AWS connection request still requires target approval");
   check((await Task.Run(()=>target.Decide(session.SessionId,true))).Status=="Approved","AWS target explicitly approves");
   check((await Task.Run(()=>owner.Connect(session.SessionId))).Status=="Connected","AWS approved session connects");
   check((await Task.Run(()=>owner.Revoke(session.SessionId))).Status=="Revoked","AWS connection is revoked on request");
   Find<TabControl>(resumed,"ServerSettingsTabs").SelectedIndex=0;
   Find<Button>(resumed,"CopyAllCredentials").PerformClick();check(Clipboard.GetText()=="ID : "+current.DeviceId+"\r\nPW : "+current.TemporaryPassword,"AWS clipboard contains exact labelled short ID and PW");
   Find<Button>(resumed,"CopyDeviceId").PerformClick();
   Capture(resumed,Path.Combine(output,"aws-registered-main.png"));
   new SettingsStore(destination).Save(current);
   check(new SettingsStore(destination).Load().DeviceId==saved.DeviceId,"AWS verified registration installed for this Windows user with DPAPI");
   Console.WriteLine("AWS_REGISTRATION_ACCEPTANCE_PASS");
   resumed.Close();await Wait(()=>resumed.IsDisposed);
  }
 }
}
