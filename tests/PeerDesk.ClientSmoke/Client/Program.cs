using System;
using System.Linq;
using System.Net;
using PeerDesk.Windows;
class ClientSmoke
{
 static int count;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
 static void Denied(Action call,string name){try{call();}catch(Exception){Check(true,name);return;}throw new Exception("Unexpected acceptance: "+name);}
 static void DeniedHttp(Action call,int expected,string name){try{call();}catch(InvalidOperationException ex){var web=ex.InnerException as WebException;var response=web==null?null:web.Response as HttpWebResponse;Check(response!=null && (int)response.StatusCode==expected,name);return;}throw new Exception("Unexpected HTTP acceptance: "+name);}
 static int Main()
 {
  try {
   string raw=Console.ReadLine(), invitation=Console.ReadLine();
   if(raw==null||invitation==null)throw new Exception("Missing fixture input");
   var profile=ServerProfile.Parse(raw);
   Check(!CredentialFormat.IsNewDeviceId("1234567890\n")&&!CredentialFormat.IsLegacyDeviceId(new string('a',32)+"\n")&&!CredentialFormat.IsNewPassword("Abcdefghi\n")&&!CredentialFormat.IsLegacyPassword("0123456789ABCDEF\n"),"credential exact lengths reject trailing newline");
   Check(!CredentialFormat.IsNewPassword("abcdefghi")&&!CredentialFormat.IsNewPassword("ABCDEFGHI")&&!CredentialFormat.IsNewPassword("Abcdefgh1"),"new password requires ASCII mixed letters");
   Check(profile.Version==1,"net48 version1 profile");
   Denied(()=>ServerProfile.Parse(raw.Replace("\"Version\":1","\"Version\":2")),"unknown version");
   Denied(()=>ServerProfile.Parse(raw.Replace("\"Name\":\"Fixture\"","\"Name\":\"   \"")),"blank name");
   Denied(()=>ServerProfile.Parse(raw.TrimEnd().TrimEnd('}')+",\"Na\\u006de\":\"duplicate\"}"),"escaped duplicate property");
   Denied(()=>ServerProfile.Parse(raw.TrimEnd().TrimEnd('}')+",\"BearerToken\":\"not-a-real-secret\"}"),"secret export property rejected");
   Denied(()=>ServerProfile.Parse(raw.Replace("https://","http://")),"HTTP rejected");
   Denied(()=>ServerProfile.Parse(raw.Replace("\"Name\":\"Fixture\"","\"Name\":\""+new string('n',65)+"\"")),"name bound");
   var a=new ControlPlaneClient(profile);var b=new ControlPlaneClient(profile);var c=new ControlPlaneClient(profile);
   a.Register(invitation); b.Register(invitation); c.Register(invitation);
   Check(a.DeviceId.Length==10&&a.DeviceId[0]!='0'&&a.DeviceId.All(x=>x>='0'&&x<='9'),"net48 server assigned ten digit ID");
   Check(a.TemporaryPassword.Length==9&&a.TemporaryPassword.All(x=>(x>='A'&&x<='Z')||(x>='a'&&x<='z'))&&a.TemporaryPassword.Any(x=>x>='A'&&x<='Z')&&a.TemporaryPassword.Any(x=>x>='a'&&x<='z'),"net48 mixed case nine letter password");
   Check(a.DeviceId!=b.DeviceId && a.Token!=b.Token,"independent net48 device identities");
   var s=a.Request(b.DeviceId, b.TemporaryPassword);
   Check(s.Status=="Pending","net48 request pending");
   DeniedHttp(()=>a.Connect(s.SessionId),400,"net48 cannot connect before target consent");
   DeniedHttp(()=>c.Decide(s.SessionId,true),403,"net48 stranger cannot approve");
   Check(b.Pending().Any(x=>x.SessionId==s.SessionId),"net48 pending wire array");
   Check(b.Decide(s.SessionId,true).Status=="Approved","net48 target approval");
   Check(a.Connect(s.SessionId).Status=="Connected","net48 initiator connects");
   DeniedHttp(()=>a.Connect(s.SessionId),400,"net48 replay rejected");
   Check(b.Revoke(s.SessionId).Status=="Revoked","net48 target disconnect");
   DeniedHttp(()=>a.Connect(s.SessionId),400,"net48 revoked cannot reconnect");
   var badProfile=ServerProfile.Parse(raw.Replace(profile.CertificateSha256,new string('0',64)));
   Denied(()=>new ControlPlaneClient(badProfile).Register(invitation),"net48 wrong pin rejected");
   HistorySmoke.Run(Check);
   SettingsSmoke.Run(Check,raw,a);
   Console.WriteLine("CLIENT_SMOKE_PASS count="+count);
   return 0;
  } catch(Exception ex){Console.Error.WriteLine("CLIENT_SMOKE_FAIL "+ex.GetType().Name+": "+ex.Message);return 1;}
 }
}
