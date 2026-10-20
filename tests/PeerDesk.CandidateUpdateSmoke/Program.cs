using System;using System.IO;using System.Text;using System.Diagnostics;using System.Security.Cryptography;using PeerDesk.ReleaseSecurity;using PeerDesk.Windows;
class Program {
 static int Main(string[] a) {try {
  if(a.Length==1&&a[0]=="parent"){System.Threading.Thread.Sleep(1800);return 0;}
  if(a.Length!=3&&a.Length!=4)throw new ArgumentException("product, signed bundle1, isolated directory, optional signed bundle2");
  string dir=Path.GetFullPath(a[2]);Directory.CreateDirectory(dir);
  string parent=Path.Combine(dir,"RemoteBridge.exe");File.Copy(typeof(Program).Assembly.Location,parent,false);
  var first=Run(a[0],a[1],dir,false,true);
  if(a.Length==4) {
   var second=Run(a[0],a[3],dir,false,false);
   var floorBefore=Floor(dir,Read(a[3],"release.manifest")).LoadRequired().Sequence;
   Run(a[0],a[1],dir,true,false);
   using(var f=File.OpenRead(parent))first.VerifyArtifact(f);
   if(Floor(dir,Read(a[1],"release.manifest")).LoadRequired().Sequence!=floorBefore||floorBefore!=second.Sequence)throw new Exception("Rollback lowered floor.");
   Console.WriteLine("CANDIDATE_PRODUCT_ROLLBACK_PASS 7 (signed first/second, exact known-good restore, floor unchanged, actual helper)");return 0;
  }
  Console.WriteLine("CANDIDATE_PRODUCT_HELPER_PASS 4");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static byte[] Read(string b,string f){return File.ReadAllBytes(Path.Combine(b,f));}
 static UpdateFloorStore Floor(string dir,byte[] m){using(var sha=SHA256.Create())return new UpdateApplyPolicy(dir,"RemoteBridge.exe",Path.Combine(dir,".remotebridge-update-floor.dpapi"),CandidateReleaseAnchor.PublicKey,CandidateReleaseAnchor.KeyId,"preview",Environment.Is64BitProcess?"windows-x64":"windows-x86",sha.ComputeHash(m),Environment.OSVersion.Version,2).OpenFloor();}
 static VerifiedRelease Run(string product,string bundle,string dir,bool rollback,bool initialize) {
  byte[] m=Read(bundle,"release.manifest"),s=Read(bundle,"release.signature");
  var release=SignedReleaseVerifier.Verify(m,s,CandidateReleaseAnchor.PublicKey,CandidateReleaseAnchor.KeyId,"preview",Environment.Is64BitProcess?"windows-x64":"windows-x86",DateTimeOffset.UtcNow,Environment.OSVersion.Version,2);
  var floor=Floor(dir,m);if(initialize)floor.InitializeExplicit();
  string stage;
  if(rollback){var receipt=new CandidateUpdateReceipts(dir).Load(true);stage=Path.Combine(dir,receipt.Backup);}
  else using(var f=File.OpenRead(Path.Combine(bundle,"RemoteBridge.exe")))stage=UpdateFiles.Stage(release,f,dir);
  string helper=Path.Combine(dir,".remotebridge-update-helper-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(product,helper,false);
  using(var parent=Process.Start(new ProcessStartInfo(Path.Combine(dir,"RemoteBridge.exe"),"parent"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
  using(var child=Process.Start(new ProcessStartInfo(helper,"--apply-signed-update"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardError=true})){
   string request;using(var b=new MemoryStream())using(var w=new BinaryWriter(b,new UTF8Encoding(false,true))){w.Write(2);w.Write(rollback);w.Write(parent.Id);w.Write(parent.StartTime.ToUniversalTime().Ticks);w.Write(Path.GetFileName(stage));w.Write(m.Length);w.Write(m);w.Write(s.Length);w.Write(s);w.Flush();request=Convert.ToBase64String(b.ToArray());}
   child.StandardInput.WriteLine(request);child.StandardInput.Close();
   if(!child.WaitForExit(12000))throw new Exception("Actual helper timed out.");
   if(child.ExitCode!=0)throw new Exception("Actual helper failed: "+child.StandardError.ReadToEnd());
   if(!parent.WaitForExit(1000))throw new Exception("Test parent did not exit.");
  }
  using(var f=File.OpenRead(Path.Combine(dir,"RemoteBridge.exe")))release.VerifyArtifact(f);
  if(!rollback&&floor.LoadRequired().Sequence!=release.Sequence)throw new Exception("Floor mismatch.");
  if(Directory.GetFiles(dir,".remotebridge-known-good-*").Length==0)throw new Exception("Backup missing.");
  return release;
 }
}