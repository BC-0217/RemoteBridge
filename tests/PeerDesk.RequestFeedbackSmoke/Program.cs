using System;
using System.Net;
using PeerDesk.Windows;
internal static class Program {
 static int count;
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);count++;}
 static Exception Error(Action action){try{action();throw new Exception("expected rejection");}catch(Exception ex){return ex;}}
 static bool CredentialHint(Exception ex){return ex.Message.Contains("상대")&&ex.Message.Contains("비밀번호");}
 static int Status(Exception ex){for(var e=ex;e!=null;e=e.InnerException){var w=e as WebException;var r=w==null?null:w.Response as HttpWebResponse;if(r!=null)return (int)r.StatusCode;}return 0;}
 static int Main(){
 try{
 var profile=ServerProfile.Parse(Console.ReadLine());string invite=Console.ReadLine();
 var a=new ControlPlaneClient(profile);var b=new ControlPlaneClient(profile);a.Register(invite);b.Register(invite);
 var wrong=Error(()=>a.Request(b.DeviceId,"WrongAbCd"));
 Check(CredentialHint(wrong)&&Status(wrong)==401,"wrong target password explicit hint with real401 preserved");
 var unknown=Error(()=>a.Request("99999999999999999999","WrongAbCd"));
 Check(CredentialHint(unknown)&&Status(unknown)==401,"unknown target not falsely distinguished from password");
 var invalid=new ControlPlaneClient(profile);invalid.RestoreCredentials(a.DeviceId,Convert.ToBase64String(new byte[32]),a.TemporaryPassword,a.PasswordExpiresAt);
 var unauth=Error(()=>invalid.Request(b.DeviceId,b.TemporaryPassword));
 Check(!CredentialHint(unauth)&&Status(unauth)==401&&unauth.Message.Contains("등록"),"invalid own bearer distinguished from target password");
 var s=a.Request(b.DeviceId,b.TemporaryPassword);Check(s.Status=="Pending","valid password still requests approval");
 b.Decide(s.SessionId,true);a.Connect(s.SessionId);
 var stale=Error(()=>a.Request(b.DeviceId,b.TemporaryPassword));
 Check(CredentialHint(stale)&&Status(stale)==401,"connect-invalidated target password explicit refresh hint");
 a.Revoke(s.SessionId);
 Console.WriteLine("REQUEST_SERVER_STOP");if(Console.ReadLine()!="SERVER_STOPPED")throw new Exception("stop receipt");
 var offline=Error(()=>a.Request(b.DeviceId,"WrongAbCd"));
 Check(!CredentialHint(offline),"offline transport not mislabeled wrong target password");
 Console.WriteLine("REQUEST_FEEDBACK_PASS count="+count);
 Console.WriteLine("MEDIA_ACCEPTANCE_PASS count="+count+" launcher receipt only; no media/native assertions");
 return 0;
 }catch(Exception ex){Console.Error.WriteLine("REQUEST_FEEDBACK_FAIL "+ex.GetType().Name+": "+ex.Message);return 1;}
 }
}
