using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
class Program {
 static int count;static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
 static object Invoke(Type t,string name,params object[] args){return t.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,args);}
 static string Prop(object o,string name){return (string)o.GetType().GetProperty(name).GetValue(o,null);}
 [STAThread]static int Main(string[] args){
 try{
 string product=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
 Check(Directory.GetFiles(Path.GetDirectoryName(product)).Length==1,"Product folder contains only EXE, no config/sidecar");
 var assembly=Assembly.LoadFrom(product);var type=assembly.GetType("PeerDesk.Windows.DirectoryBootstrap",true);
 string missing=Path.Combine(root,"missing");Directory.CreateDirectory(missing);
 object embedded=Invoke(type,"LoadDefaultFromDirectory",missing);Check(embedded!=null,"Missing sidecar loads embedded anchor");
 var json=new JavaScriptSerializer().Serialize(new{RegistryUrl=Prop(embedded,"RegistryUrl"),CertificateSha256=Prop(embedded,"CertificateSha256"),RegistryIdentity=Prop(embedded,"RegistryIdentity")});
 Check(new JavaScriptSerializer().DeserializeObject(json)!=null,"Embedded public data parses");
 var resource=assembly.GetManifestResourceNames().Single(x=>x.EndsWith("DirectoryBootstrap.json",StringComparison.Ordinal));
 using(var stream=assembly.GetManifestResourceStream(resource))using(var read=new StreamReader(stream)){var raw=read.ReadToEnd();var keys=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(raw);Check(keys.Count==3&&keys.ContainsKey("RegistryUrl")&&keys.ContainsKey("CertificateSha256")&&keys.ContainsKey("RegistryIdentity"),"Embedded resource has exactly three public fields");}
 string configured=Path.Combine(root,"override");Directory.CreateDirectory(configured);string file=Path.Combine(configured,"directory-bootstrap.json");
 string custom=new JavaScriptSerializer().Serialize(new{RegistryUrl="https://localhost:4443",CertificateSha256=new string('A',64),RegistryIdentity="owned-test"});
 File.WriteAllText(file,custom,new UTF8Encoding(false));object value=Invoke(type,"LoadDefaultFromDirectory",configured);
 Check(value!=null&&Prop(value,"RegistryUrl")=="https://localhost:4443","Valid explicit sidecar takes precedence");
 foreach(string bad in new[]{"{",custom.Replace("\"RegistryUrl\":","\"RegistryUrl\":\"https://evil.invalid\",\"RegistryUrl\":"),new string('x',8193),custom.Replace(new string('A',64),"short"),custom.Replace("https://localhost:4443","http://localhost:4443")}){
 File.WriteAllText(file,bad,new UTF8Encoding(false));Check(Invoke(type,"LoadDefaultFromDirectory",configured)==null,"Invalid explicit sidecar fails closed without fallback");}
 File.WriteAllText(file,custom,new UTF8Encoding(false));using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check(Invoke(type,"LoadDefaultFromDirectory",configured)==null,"Unreadable locked sidecar fails closed without fallback");}
 string directoryInstead=Path.Combine(root,"directory-file");Directory.CreateDirectory(directoryInstead);Directory.CreateDirectory(Path.Combine(directoryInstead,"directory-bootstrap.json"));
 Check(Invoke(type,"LoadDefaultFromDirectory",directoryInstead)==null,"Directory at sidecar path cannot silently fallback");
 File.WriteAllBytes(file,new byte[]{0xff,0xfe,0xff});Check(Invoke(type,"LoadDefaultFromDirectory",configured)==null,"Invalid UTF8 fails closed");
 Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
 Type formType=assembly.GetType("PeerDesk.Windows.MainForm",true);
 var ctor=formType.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(string),type},null);
 using(var form=(Form)ctor.Invoke(new object[]{Path.Combine(root,"history.json"),Path.Combine(root,"settings-test.json"),embedded})){
 form.Show();Application.DoEvents();
 var tabs=(TabControl)form.Controls.Find("ServerSettingsTabs",true).Single();tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(x=>x.Name=="ServerSettingsPage");
 Check(form.Visible&&form.IsHandleCreated,"Real product WinForms window shown from EXE-only folder");
 Check(form.Controls.Find("GuestServerAlias",true).Single().Visible&&form.Controls.Find("GuestJoinPassword",true).Single().Visible&&form.Controls.Find("GuestJoin",true).Single().Enabled,"Server ID/PW join UI enabled with embedded anchor");
 Check(!File.Exists(Path.Combine(root,"settings-test.json")),"UI probe did not create registration credentials");
 form.Close();Application.DoEvents();
 }
 Console.WriteLine("BOOTSTRAP_SMOKE_PASS "+count);return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
 }
}
