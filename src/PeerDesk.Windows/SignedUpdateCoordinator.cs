using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PeerDesk.ReleaseSecurity;
namespace PeerDesk.Windows {
 internal sealed class SignedUpdateCoordinator {
  readonly string directory; byte[] manifest,signature; string staged;
  internal VerifiedRelease Candidate {get;private set;}
  internal SignedUpdateCoordinator() { directory=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
  internal string FloorPath {get{return Path.Combine(directory,".remotebridge-update-floor.dpapi");}}
  UpdateApplyPolicy Policy(byte[] bytes) { using(var sha=SHA256.Create())return new UpdateApplyPolicy(directory,"RemoteBridge.exe",FloorPath,CandidateReleaseAnchor.PublicKey,CandidateReleaseAnchor.KeyId,"preview",Environment.Is64BitProcess?"windows-x64":"windows-x86",sha.ComputeHash(bytes),Environment.OSVersion.Version,2); }
  // Explicit user action only. Existing/corrupt floors are never replaced.
  internal void InitializeTrustFloorExplicit() { Policy(new byte[0]).OpenFloor().InitializeExplicit(); }
  internal VerifiedRelease StageBundle(string bundleDirectory) {
   Candidate=null;staged=null;
   var m=ReadBounded(Path.Combine(bundleDirectory,"release.manifest"),16384);
   var s=ReadBounded(Path.Combine(bundleDirectory,"release.signature"),1024);
   var v=SignedReleaseVerifier.Verify(m,s,CandidateReleaseAnchor.PublicKey,CandidateReleaseAnchor.KeyId,"preview",Environment.Is64BitProcess?"windows-x64":"windows-x86",DateTimeOffset.UtcNow,Environment.OSVersion.Version,2);
   Policy(m).OpenFloor().LoadRequired().DemandCandidate(v);
   using(var f=new FileStream(Path.Combine(bundleDirectory,v.ArtifactName),FileMode.Open,FileAccess.Read,FileShare.Read))staged=UpdateFiles.Stage(v,f,directory);
   manifest=m;signature=s;Candidate=v;return v;
  }
  // Caller must explicitly ask the user to close this application after launching helper.
  // No kill, elevation, automatic restart or automatic download.
  internal int StartApplyHelper() { return StartHelper(false); }
  internal int StartRollbackHelper() { return StartHelper(true); }
  int StartHelper(bool rollback) {
   if(rollback){var receipt=new CandidateUpdateReceipts(directory).Load(true);if(receipt==null)throw new InvalidOperationException("검증된 이전 서명 버전이 없습니다.");manifest=receipt.Manifest;signature=receipt.Signature;staged=Path.Combine(directory,receipt.Backup);Candidate=VerifyHistorical(manifest,signature);}
   if(Candidate==null||staged==null)throw new InvalidOperationException("먼저 서명된 업데이트 묶음을 검증하세요.");
   string helper=Path.Combine(directory,".remotebridge-update-helper-"+Guid.NewGuid().ToString("N")+".exe");
   File.Copy(typeof(SignedUpdateCoordinator).Assembly.Location,helper,false);
   var parent=Process.GetCurrentProcess();
   string request;
   using(var m=new MemoryStream())using(var w=new BinaryWriter(m,new UTF8Encoding(false,true))) {
    w.Write(2);w.Write(rollback);w.Write(parent.Id);w.Write(parent.StartTime.ToUniversalTime().Ticks);
    w.Write(Path.GetFileName(staged));w.Write(manifest.Length);w.Write(manifest);w.Write(signature.Length);w.Write(signature);w.Flush();request=Convert.ToBase64String(m.ToArray());
   }
   var start=new ProcessStartInfo(helper,"--apply-signed-update"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true};
   using(var child=Process.Start(start)){child.StandardInput.WriteLine(request);child.StandardInput.Close();return child.Id;}
  }
  internal static int RunHelper() {
   int step=0;try {
    var input=new StringBuilder();int ch;
    using(var reader=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false,true),true,1024))
    while((ch=reader.Read())!=-1&&ch!=10){if(input.Length>=32768)throw new InvalidDataException();if(ch!=13)input.Append((char)ch);}
    string line=input.ToString();if(line.Length==0)throw new InvalidDataException();
    step=1;var raw=Convert.FromBase64String(line.TrimStart('\uFEFF'));var self=new SignedUpdateCoordinator();
    UpdateApplyJob job;bool rollback;
    using(var m=new MemoryStream(raw,false))using(var r=new BinaryReader(m,new UTF8Encoding(false,true))) {
     if(r.ReadInt32()!=2)throw new InvalidDataException();rollback=r.ReadBoolean();
     job=new UpdateApplyJob{ParentProcessId=r.ReadInt32(),ParentStartUtcTicks=r.ReadInt64(),StagedBasename=r.ReadString()};
     job.Manifest=ReadField(r,16384);job.Signature=ReadField(r,1024);if(m.Position!=m.Length)throw new InvalidDataException();
    }
    // Exact parent executable and installed path must match this copied helper directory.
    step=2;using(var parent=Process.GetProcessById(job.ParentProcessId)) {
     if(parent.StartTime.ToUniversalTime().Ticks!=job.ParentStartUtcTicks||
       !String.Equals(parent.MainModule.FileName,Path.Combine(self.directory,"RemoteBridge.exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Parent binding mismatch.");
    }
    step=3;var receipts=new CandidateUpdateReceipts(self.directory);
    if(rollback) {
     var known=receipts.Load(true);if(known==null||known.Backup!=job.StagedBasename||!Equal(known.Manifest,job.Manifest)||!Equal(known.Signature,job.Signature))throw new InvalidDataException("Recovery receipt mismatch.");
     var release=self.VerifyHistorical(known.Manifest,known.Signature);
     WaitParent(job);
     var floor=self.Policy(job.Manifest).OpenFloor().LoadRequired();
     UpdateFiles.Rollback(release,Path.Combine(self.directory,known.Backup),Path.Combine(self.directory,"RemoteBridge.exe"),floor,release.ReleaseId,release.Hash,release.Platform,2);
     receipts.Save(false,new CandidateUpdateReceipt{Manifest=known.Manifest,Signature=known.Signature});return 0;
    }
    var previous=receipts.Load(false);
    if(previous!=null){var previousRelease=self.VerifyHistorical(previous.Manifest,previous.Signature);using(var f=File.OpenRead(Path.Combine(self.directory,"RemoteBridge.exe")))previousRelease.VerifyArtifact(f);}
    step=4;string backup=UpdateApplyHelper.Apply(self.Policy(job.Manifest),job,CancellationToken.None);
    if(previous!=null){previous.Backup=Path.GetFileName(backup);receipts.Save(true,previous);}
    receipts.Save(false,new CandidateUpdateReceipt{Manifest=job.Manifest,Signature=job.Signature});return 0;
   } catch(Exception ex) {Console.Error.WriteLine("Signed update failed at stage "+step+": "+ex.GetType().Name);return 1;}
  }
  VerifiedRelease VerifyHistorical(byte[] m,byte[] s){var issued=ReleaseManifest.Decode(m).IssuedAt;return SignedReleaseVerifier.Verify(m,s,CandidateReleaseAnchor.PublicKey,CandidateReleaseAnchor.KeyId,"preview",Environment.Is64BitProcess?"windows-x64":"windows-x86",DateTimeOffset.FromUnixTimeSeconds(issued),Environment.OSVersion.Version,2);}
  static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;int n=0;for(int i=0;i<a.Length;i++)n|=a[i]^b[i];return n==0;}
  static void WaitParent(UpdateApplyJob job){try{using(var p=Process.GetProcessById(job.ParentProcessId)){if(p.StartTime.ToUniversalTime().Ticks!=job.ParentStartUtcTicks)throw new InvalidDataException();if(!p.WaitForExit(30000))throw new TimeoutException();}}catch(ArgumentException){}}
  static byte[] ReadField(BinaryReader r,int max){int n=r.ReadInt32();if(n<1||n>max)throw new InvalidDataException();byte[] b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return b;}
  static byte[] ReadBounded(string path,int max) {using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){byte[] b=new byte[max+1];int used=0,n;while(used<b.Length&&(n=f.Read(b,used,b.Length-used))>0)used+=n;if(used>max)throw new InvalidDataException();Array.Resize(ref b,used);return b;}}
 }
}
