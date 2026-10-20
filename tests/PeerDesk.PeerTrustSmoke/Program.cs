using System;using System.IO;using System.Linq;using System.Text;using System.Windows.Forms;using PeerDesk.Windows;
internal static class Program {
 static int n;static void Check(bool b,string s){if(!b)throw new Exception(s);Console.WriteLine("PASS "+s);n++;}
 static bool Reject(Action a){try{a();return false;}catch(InvalidDataException){return true;}catch(InvalidOperationException){return true;}}
 [STAThread]static int Main(){string path=Path.Combine(Path.GetTempPath(),"RemoteBridge.trust."+Guid.NewGuid().ToString("N"));try{
 string url="https://localhost:29999/",pin=new string('A',64),id="1234567890",key=new string('b',64);var store=new PeerTrustStore(path);
 Check(store.Find(url,pin,id)==null,"unknown peer has no automatic trust");store.Trust(url,pin,id,key);
 Check(new PeerTrustStore(path).Find(url,pin,id)==key,"DPAPI trust retains independent identity");
 Check(!File.ReadAllText(path).Contains(key),"trusted pin not stored as editable plaintext");
 Check(store.Find(url,new string('C',64),id)==null,"trust isolated by full server pin");
 Check(store.Find("https://other/",pin,id)==null,"trust isolated by server URL");
 Check(Reject(()=>store.Trust(url,pin,id,new string('d',64))),"changed peer key never silently replaced");
 Check(store.Find(url,pin,id)==key,"failed replacement preserves original");
 var list=store.List();list[0].IdentityPin=new string('e',64);Check(store.Find(url,pin,id)==key,"trusted list defensive copy");
 store.Forget(url,pin,id);Check(new PeerTrustStore(path).Find(url,pin,id)==null,"explicit forget persists");
 byte[] original=File.ReadAllBytes(path);original[original.Length/2]^=1;File.WriteAllBytes(path,original);var corrupt=new PeerTrustStore(path);
 Check(corrupt.Warning!=null&&Reject(()=>corrupt.Find(url,pin,id)),"corrupt trust blocks secure connection");
 Check(Reject(()=>corrupt.Trust(url,pin,id,key))&&File.ReadAllBytes(path).SequenceEqual(original),"corrupt trust is not overwritten");
 using(var a=new SecurePeerIdentity())using(var b=new SecurePeerIdentity())
 using(var x=new SecurePeerChannel(a,Encoding.UTF8.GetBytes(new string('x',40)),true))using(var y=new SecurePeerChannel(b,Encoding.UTF8.GetBytes(new string('x',40)),false)){
 string first=PeerTrustDialog.ComparisonCode(x.Hello,y.Hello),second=PeerTrustDialog.ComparisonCode(y.Hello,x.Hello);Check(first==second,"human comparison code identical both roles");
 using(var dialog=new PeerTrustDialog(id,b.Fingerprint,first)){
 var button=(Button)dialog.Controls.Find("TrustPeer",true).Single();Check(!button.Enabled,"trust requires explicit comparison checkbox");
 ((CheckBox)dialog.Controls.Find("PeerCodeCompared",true).Single()).Checked=true;Check(button.Enabled,"explicit comparison enables trust");
 }
 }
 Console.WriteLine("PEER_TRUST_SMOKE_PASS count="+n);return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(File.Exists(path))File.Delete(path);}
 }
}