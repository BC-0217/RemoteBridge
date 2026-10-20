using System;using System.IO;using System.Net;using System.Net.Sockets;using System.Reflection;using System.Threading;using System.Threading.Tasks;using System.Windows.Forms;using PeerDesk.Windows;
static class Program {
 static int count;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
 static string Send(ControlPlaneClient c,string path,int timeout,CancellationToken ct){
  try{return (string)typeof(ControlPlaneClient).GetMethod("Send",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,new object[]{"GET",path,null,false,timeout,ct,null,65536});}
  catch(TargetInvocationException e){throw e.InnerException;}
 }
 [STAThread] static int Main(){
  string dir=Path.Combine(Path.GetTempPath(),"remote-connection-"+Guid.NewGuid().ToString("N"));DiagnosticLog.Initialize(dir,65536);
  try{
   Check(ControlPlaneClient.ConnectionFailureMessage(WebExceptionStatus.RequestCanceled,true,10000).Contains("時間")==false&&ControlPlaneClient.ConnectionFailureMessage(WebExceptionStatus.RequestCanceled,true,10000).Contains("시간 초과"),"timer abort shown as timeout");
   Check(!ControlPlaneClient.ConnectionFailureMessage(WebExceptionStatus.RequestCanceled,false,10000).Contains("사용자 취소가 아닙니다"),"unexplained cancel not assumed timeout");
   Check(ControlPlaneClient.ConnectionFailureMessage(WebExceptionStatus.TrustFailure,false,10000).Contains("인증서 검증은 생략하지"),"TLS failure remains fail closed");
   using(var f=new MainForm()){Check(f.Text=="RemoteBridge — 리모트브리지","actual form corrected title");}
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
   try{
    int port=((IPEndPoint)listener.LocalEndpoint).Port;
    // Test-only reflection permits an unencrypted local stalled socket. Production profile parser still requires HTTPS.
    var profile=new ServerProfile();
    typeof(ServerProfile).GetProperty("RendezvousUrl").GetSetMethod(true).Invoke(profile,new object[]{"http://127.0.0.1:"+port});
    var client=new ControlPlaneClient(profile);
    var accept=listener.AcceptTcpClientAsync();
    Exception timeout=null;try{Send(client,"/api/v1/owner/room",150,CancellationToken.None);}catch(Exception e){timeout=e;}
    Check(timeout is InvalidOperationException&&timeout.Message.Contains("시간 초과")&&timeout.InnerException is WebException,"real stalled transport deadline retains WebException");
    Check(accept.Wait(1000),"loopback request accepted");using(var peer=accept.Result){}
    var accept2=listener.AcceptTcpClientAsync();
    using(var cts=new CancellationTokenSource(150)){
     bool canceled=false;try{Send(client,"/healthz",2000,cts.Token);}catch(OperationCanceledException){canceled=true;}
     Check(canceled,"real external cancellation remains OperationCanceledException");
    }
    Check(accept2.Wait(1000),"cancel request accepted");using(var peer=accept2.Result){}
    var reply=Task.Run(()=>{using(var peer=listener.AcceptTcpClient()){peer.ReceiveTimeout=2000;var stream=peer.GetStream();var buf=new byte[4096];stream.Read(buf,0,buf.Length);var bytes=System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");stream.Write(bytes,0,bytes.Length);}});
    Check(Send(client,"/healthz",2000,CancellationToken.None)=="{}","normal request unchanged");Check(reply.Wait(2000),"reply task complete");
    var reject=Task.Run(()=>{using(var peer=listener.AcceptTcpClient()){peer.ReceiveTimeout=2000;var stream=peer.GetStream();var buf=new byte[4096];stream.Read(buf,0,buf.Length);var bytes=System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 503 Unavailable\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");stream.Write(bytes,0,bytes.Length);}});
    InvalidOperationException http=null;try{Send(client,"/healthz",2000,CancellationToken.None);}catch(InvalidOperationException e){http=e;}
    Check(http!=null&&ControlPlaneClient.HttpStatus(http)==503,"HTTP response status preserved");Check(reject.Wait(2000),"error reply task complete");
   }finally{listener.Stop();}
   DiagnosticLog.Shutdown(2000);
   string log=String.Join("\n",Directory.GetFiles(dir,"*.jsonl"));string contents="";foreach(var p in Directory.GetFiles(dir,"*.jsonl"))contents+=File.ReadAllText(p);
   Check(contents.Contains("\"ev\":\"ControlRequestFailure\"")&&contents.Contains("\"Reason\":\"Deadline\"")&&contents.Contains("\"Operation\":\"Owner\"")&&contents.Contains("\"BudgetMs\":150"),"deadline operation budget timing logged");
   Check(contents.Contains("\"Reason\":\"CallerCanceled\"")&&contents.Contains("\"AlreadyCanceled\":1"),"caller cancellation logged separately");
   Check(contents.Contains("\"ExHttpStatus\":503"),"HTTP failure logged");
   Check(!contents.Contains("127.0.0.1")&&!contents.Contains("/api/")&&!contents.Contains("http://"),"logs exclude target addresses and raw paths");
   Console.WriteLine("PASS total="+count);return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{DiagnosticLog.Shutdown(1000);}
 }
}
