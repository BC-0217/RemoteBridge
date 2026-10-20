using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PeerDesk.ReleaseSecurity;

internal static class HelperFixture
{
    static string Self { get { return Assembly.GetExecutingAssembly().Location; } }
    static Process Start(string mode,bool stdin)
    {
        return Process.Start(new ProcessStartInfo(Self,mode) { UseShellExecute=false,CreateNoWindow=true,
            WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=stdin,RedirectStandardOutput=true,RedirectStandardError=true });
    }
    static byte[] Bytes(BinaryReader r) { int n=r.ReadInt32();if(n<0||n>16384)throw new InvalidDataException();var b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return b; }
    static void Bytes(BinaryWriter w,byte[] b) { w.Write(b.Length);w.Write(b); }
    public static void Child()
    {
        // Test-only bootstrap: the production helper must compile its trusted anchor;
        // this fixture explicitly supplies its own throwaway public key through stdin.
        Console.InputEncoding=new UTF8Encoding(false);
        string input=Console.ReadLine();if(input!=null)input=input.TrimStart('\uFEFF');if(input==null||input.Length>65536)throw new InvalidDataException("Missing fixture input.");
        using(var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(input)),Encoding.UTF8)){
            string dir=r.ReadString(),floor=r.ReadString(),stage=r.ReadString();int pid=r.ReadInt32();long ticks=r.ReadInt64();
            byte[] manifest=Bytes(r),signature=Bytes(r),modulus=Bytes(r),exponent=Bytes(r),approved=Bytes(r);
            var p=new UpdateApplyPolicy(dir,"RemoteBridge.exe",floor,new RSAParameters{Modulus=modulus,Exponent=exponent},
                "fixture","preview","windows-x64",approved,new Version(10,0),2);
            string backup=UpdateApplyHelper.Apply(p,new UpdateApplyJob{Manifest=manifest,Signature=signature,
                StagedBasename=stage,ParentProcessId=pid,ParentStartUtcTicks=ticks},CancellationToken.None);
            Console.WriteLine(Path.GetFileName(backup));
        }
    }
    public static void Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeHelper-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string dest=Path.Combine(dir,"RemoteBridge.exe"),floorPath=Path.Combine(dir,"floor.bin");
        byte[] old={1,2,3},next={4,5,6,7};File.WriteAllBytes(dest,old);
        using(var rsa=new RSACryptoServiceProvider(2048))using(var sha=SHA256.Create()){
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var m=new ReleaseManifest{Product="RemoteBridge",Channel="preview",Platform="windows-x64",ReleaseId="fixture-r2",
                ArtifactName="RemoteBridge.exe",KeyId="fixture",MinimumOS="6.1",Sequence=2,IssuedAt=now-5,ExpiresAt=now+300,
                ArtifactLength=next.Length,Sha256=sha.ComputeHash(next),MinimumSchema=1,MaximumSchema=2};
            byte[] canonical=m.Encode(),sig=rsa.SignData(canonical,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var key=rsa.ExportParameters(false);byte[] approved=sha.ComputeHash(canonical);
            var verified=SignedReleaseVerifier.Verify(canonical,sig,key,"fixture","preview","windows-x64",DateTimeOffset.UtcNow,new Version(10,0),2);
            var policy=new UpdateApplyPolicy(dir,"RemoteBridge.exe",floorPath,key,"fixture","preview","windows-x64",approved,new Version(10,0),2);
            policy.OpenFloor().InitializeExplicit();string stage=UpdateFiles.Stage(verified,new MemoryStream(next),dir);
            using(var parent=Start("--helper-parent",false))using(var helper=Start("--helper-child",true)){
                using(var message=new MemoryStream()){
                using(var w=new BinaryWriter(message,Encoding.UTF8,true)){
                    w.Write(dir);w.Write(floorPath);w.Write(Path.GetFileName(stage));w.Write(parent.Id);w.Write(parent.StartTime.ToUniversalTime().Ticks);
                    Bytes(w,canonical);Bytes(w,sig);Bytes(w,key.Modulus);Bytes(w,key.Exponent);Bytes(w,approved);w.Flush();
                }
                helper.StandardInput.WriteLine(Convert.ToBase64String(message.ToArray()));helper.StandardInput.Close();
                }
                if(!helper.WaitForExit(10000))throw new Exception("Helper finite gate timed out.");
                string result=helper.StandardOutput.ReadToEnd().Trim(),error=helper.StandardError.ReadToEnd();
                if(helper.ExitCode!=0)throw new Exception("Helper failed: "+error);
                if(!parent.HasExited)throw new Exception("Apply occurred while parent still running.");
                if(File.ReadAllBytes(dest)[0]!=4||File.ReadAllBytes(Path.Combine(dir,result))[0]!=1||
                    policy.OpenFloor().LoadRequired().Sequence!=2)throw new Exception("Helper actual replacement/floor failed.");
                Console.WriteLine("UPDATE_HELPER_PASS 4: owned parent exit, child apply, backup, durable floor");
            }
        }
    }
}
