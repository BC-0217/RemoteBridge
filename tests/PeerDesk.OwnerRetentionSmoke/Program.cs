using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PeerDesk.Windows;
class Program {
 static int n;static void Check(bool b,string name){if(!b)throw new Exception(name);Console.WriteLine("PASS "+name);n++;}
 static T F<T>(Control f,string name)where T:Control{return f.Controls.Find(name,true).OfType<T>().Single();}
 static string Profile(string name,string url,string pin){return new JavaScriptSerializer().Serialize(new{Version=1,Name=name,RendezvousUrl=url,RelayUrl=url,CertificateSha256=pin});}
 [STAThread]static int Main(string[] args){
 Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int result=1;
 string dir=Path.Combine(Path.GetTempPath(),"peerdesk-owner-retain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
 var form=new MainForm(Path.Combine(dir,"history.json"),Path.Combine(dir,"settings.json"));
 form.Shown+=async(s,e)=>{try{
 var profile=F<TextBox>(form,"ProfileJson");var key=F<TextBox>(form,"OwnerSecret");
 string secret=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.Create().GetBytesCompat(32));
 string pin=new string('A',64),url="https://localhost:4443";
 profile.Text=Profile("Owned",url,pin);key.Text=secret;
 Check(key.UseSystemPasswordChar,"Management key remains masked");
 profile.Text=Profile("Renamed",url,pin);Check(key.Text==secret,"Same URL/pin display-name change preserves memory key");
 profile.Text=Profile("Renamed",url+"/",pin.ToLowerInvariant());Check(key.Text==secret,"Equivalent URL and pin case preserve memory key");
 profile.Text=Profile("Changed","https://localhost:4444",pin);Check(key.Text.Length==0,"Different URL discards memory key");
 key.Text=secret;profile.Text=Profile("Changed","https://localhost:4444",new string('B',64));Check(key.Text.Length==0,"Different full pin discards memory key");
 key.Text=secret;profile.Text="{";Check(key.Text.Length==0,"Invalid profile discards bound key");
 key.Text=secret;profile.Text=Profile("Recovered",url,pin);Check(key.Text.Length==0,"Key typed with invalid binding cannot carry to a valid server");
 key.Text=secret;
 typeof(MainForm).GetMethod("ResetProfile",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
 for(int i=0;i<100&&key.Text.Length!=0;i++)await Task.Delay(10);
 Check(key.Text.Length==0,"Explicit reset discards memory key");
 profile.Text=Profile("Final",url,pin);key.Text=secret;
 Check(!Directory.GetFiles(dir).Any(f=>File.ReadAllText(f).Contains(secret)),"Management key is not written in isolated settings");
 form.Close();
 Check(key.Text.Length==0,"Closing clears key before control disposal");
 Console.WriteLine("OWNER_RETENTION_UI_PASS "+n);result=0;
 }catch(Exception ex){Console.Error.WriteLine(ex);form.Close();}};
 Application.Run(form);return result;
 }
}
static class RandomCompat{internal static byte[] GetBytesCompat(this System.Security.Cryptography.RandomNumberGenerator r,int count){using(r){var b=new byte[count];r.GetBytes(b);return b;}}}
