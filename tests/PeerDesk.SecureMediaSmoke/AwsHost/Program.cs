using System;using System.Diagnostics;using System.IO;using System.Linq;using System.Text;using System.Threading.Tasks;using System.Web.Script.Serialization;using PeerDesk.Windows;
internal static class Program {
 static int Main(string[] args){Console.OutputEncoding=new UTF8Encoding(false,true);Console.SetError(new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false,true)){AutoFlush=true});try{return Run(args).GetAwaiter().GetResult();}catch(Exception e){Console.Error.WriteLine("AWS_SECURE_MEDIA_FAILED "+e.GetType().Name+": "+e.Message);return 1;}}
 static async Task<int> Run(string[] args){
  if(Environment.GetEnvironmentVariable("PEERDESK_TEST_STDIN_ONLY")=="1")return await RunStdinOnly(args);
  if(args.Length!=2)throw new ArgumentException("Expected child executable and private owner-key file path.");
  string settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","settings.json");
  var state=new SettingsStore(settings).Load();var profile=ServerProfile.Parse(state.ProfileJson);
  if(profile.RendezvousUrl.TrimEnd('/')!="https://3.112.217.41:8443")throw new InvalidOperationException("Saved profile does not match approved Tokyo endpoint.");
  var guest=state.SavedInputs.FirstOrDefault(x=>x.ServerUrl==profile.RendezvousUrl&&String.Equals(x.CertificateSha256,profile.CertificateSha256,StringComparison.OrdinalIgnoreCase)&&!String.IsNullOrEmpty(x.VerifiedJoinPassword));
  if(guest==null)throw new InvalidOperationException("No exact endpoint/pin-bound verified join password in protected settings.");
  string owner=File.ReadAllText(args[1]).Trim();if(owner.Length<32)throw new InvalidDataException("Private owner-key file shape.");
  var a=new ControlPlaneClient(profile);var b=new ControlPlaneClient(profile);bool cleaned=true;int result=1;
  try{
   await a.OwnerGetSettingsAsync(owner);Console.WriteLine("AWS_OWNER_AUTH_AND_PIN_OK");
   a.Register(guest.VerifiedJoinPassword);b.Register(guest.VerifiedJoinPassword);
   var pending=a.Request(b.DeviceId,"");b.Decide(pending.SessionId,true);a.Connect(pending.SessionId);
   Console.WriteLine("AWS_OWNED_PROBES_CONNECTED");
   // Long fixed-duration stream mode only (PEERDESK_SECURE_LONG_STREAM=1) gets 60s; general limit stays 30s.
   int limitSeconds=Environment.GetEnvironmentVariable("PEERDESK_SECURE_LONG_STREAM")=="1"?60:30;
   result=await RunChild(args[0],new JavaScriptSerializer().Serialize(new{url=profile.RendezvousUrl,pin=profile.CertificateSha256,sid=pending.SessionId,aId=a.DeviceId,aToken=a.Token,bId=b.DeviceId,bToken=b.Token}),"Owned AWS fixture exceeded "+limitSeconds+" seconds.",limitSeconds*1000);
  }finally{
   foreach(var c in new[]{a,b})if(!String.IsNullOrEmpty(c.DeviceId))try{await c.OwnerRevokeDeviceAsync(owner,c.DeviceId);}catch(Exception e){cleaned=false;Console.Error.WriteLine("OWNED_PROBE_CLEANUP_FAILED "+e.GetType().Name);}
   owner=null;if(cleaned)Console.WriteLine("AWS_OWNED_PROBE_DEVICES_REVOKED");if(!cleaned)throw new InvalidOperationException("Owned probe cleanup incomplete.");
  }
  return result;
 }
 // Synthetic local gate: no settings, owner key, or network; sends literal {} through the same stdin path.
 static Task<int> RunStdinOnly(string[] args){
  if(args.Length<1)throw new ArgumentException("Expected child executable.");
  Console.WriteLine("STDIN_ONLY_GATE parent");
  return RunChild(args[0],"{}","Synthetic stdin-only fixture exceeded 30 seconds.",30000);
 }
 static void WriteChildStdin(Process child,string payload){
  // Explicit BOM-free strict UTF-8; leaveOpen so StandardInput.Close() closes the pipe exactly once.
  using(var w=new StreamWriter(child.StandardInput.BaseStream,new UTF8Encoding(false,true),1024,true)){w.Write(payload);w.Write('\n');w.Flush();}
  child.StandardInput.Close();
 }
 static async Task<int> RunChild(string exe,string payload,string timeoutMessage,int timeoutMs){
  using(var child=new Process{StartInfo=new ProcessStartInfo(Path.GetFullPath(exe)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=new UTF8Encoding(false,true)}}){
   child.Start();var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
   try{WriteChildStdin(child,payload);}finally{payload=null;}
   bool done=await Task.Run(()=>child.WaitForExit(timeoutMs));
   if(!done){child.Kill();child.WaitForExit();Console.Write(await output);Console.Error.Write(await error);throw new TimeoutException(timeoutMessage);}
   Console.Write(await output);Console.Error.Write(await error);return child.ExitCode;
  }
 }
}