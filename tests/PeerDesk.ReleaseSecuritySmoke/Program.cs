using System;
using System.IO;
using System.Security.Cryptography;
using PeerDesk.ReleaseSecurity;
class Program {
 static int n;static void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
 static void Reject(Action a,string s){bool bad=false;try{a();}catch(InvalidDataException){bad=true;}Check(bad,s);}
 static ReleaseManifest Make(byte[] bytes,long seq){byte[] hash;using(var h=SHA256.Create())hash=h.ComputeHash(bytes);return new ReleaseManifest{Product="RemoteBridge",Channel="preview",Platform="windows-x64",ReleaseId="r"+seq,ArtifactName="RemoteBridge.exe",KeyId="fixture",MinimumOS="6.1",Sequence=seq,IssuedAt=1000,ExpiresAt=3000,ArtifactLength=bytes.Length,Sha256=hash,MinimumSchema=1,MaximumSchema=2};}
 static VerifiedRelease Verify(ReleaseManifest m,RSA rsa){var b=m.Encode();return SignedReleaseVerifier.Verify(b,rsa.SignData(b,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),rsa.ExportParameters(false),"fixture","preview","windows-x64",DateTimeOffset.FromUnixTimeSeconds(2000),new Version(10,0),2);}
 static void Main(string[] args){try{if(args.Length==1&&args[0]=="--floor")FloorTest();else if(args.Length==1&&args[0]=="--helper")HelperFixture.Run();else if(args.Length==1&&args[0]=="--helper-child")HelperFixture.Child();else if(args.Length==1&&args[0]=="--helper-parent")System.Threading.Thread.Sleep(750);else Run();}catch(Exception ex){Console.Error.WriteLine(ex.GetType().FullName);Console.Error.WriteLine(ex.Message);Console.Error.WriteLine(ex.StackTrace);Environment.ExitCode=1;}}
 static void FloorTest(){
  using(var rsa=new RSACryptoServiceProvider(2048)){
   string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeFloor-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
   string path=Path.Combine(dir,"floor.bin");string install=Path.Combine(dir,"RemoteBridge.exe");
   var store=new UpdateFloorStore(path,install,new string('A',64));
   bool missing=false;try{store.LoadRequired();}catch(FileNotFoundException){missing=true;}Check(missing,"missing floor not automatic zero");
   store.InitializeExplicit();Check(store.LoadRequired().Sequence==0,"explicit initial floor");
   bool exists=false;try{store.InitializeExplicit();}catch(IOException){exists=true;}Check(exists,"initialize never overwrites");
   var v1=Verify(Make(new byte[]{1,2,3},1),rsa);var v2=Verify(Make(new byte[]{4,5},2),rsa);
   store.Commit(v1);Check(new UpdateFloorStore(path,install,new string('A',64)).LoadRequired().Sequence==1,"DPAPI persistent floor");
   store.Commit(v2);Reject(()=>store.Commit(v1),"persisted downgrade denied");
   Reject(()=>new UpdateFloorStore(path,install+"other",new string('A',64)).LoadRequired(),"installation binding");
   Reject(()=>new UpdateFloorStore(path,install,new string('B',64)).LoadRequired(),"release key binding");
   byte[] original=File.ReadAllBytes(path);byte[] tamper=(byte[])original.Clone();tamper[tamper.Length/2]^=1;File.WriteAllBytes(path,tamper);
   Reject(()=>store.LoadRequired(),"DPAPI tamper rejects");
   Reject(()=>store.Commit(v2),"corrupt floor never overwritten");
   Check(File.ReadAllBytes(path).Length==tamper.Length&&File.ReadAllBytes(path)[tamper.Length/2]==tamper[tamper.Length/2],"corrupt bytes preserved");
   File.WriteAllBytes(path,original);Check(store.LoadRequired().Sequence==2,"explicit fixture recovery retains floor");
   Console.WriteLine("UPDATE_FLOOR_PASS "+n);
  }
 }
 static void Run(){
 using(var rsa=new RSACryptoServiceProvider(2048)){byte[] old={1,2,3},next={4,5,6,7};var m=Make(old,1);var bytes=m.Encode();var sig=rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var key=rsa.ExportParameters(false);var v1=Verify(m,rsa);Check(v1.Sequence==1,"signed manifest");
 Action<byte[],byte[],RSAParameters,string,string,long,int> test=(b,s,k,ch,p,t,sc)=>SignedReleaseVerifier.Verify(b,s,k,"fixture",ch,p,DateTimeOffset.FromUnixTimeSeconds(t),new Version(10,0),sc);
 var tamper=(byte[])bytes.Clone();tamper[10]^=1;Reject(()=>test(tamper,sig,key,"preview","windows-x64",2000,2),"tamper");
 using(var wrong=new RSACryptoServiceProvider(2048)){Reject(()=>test(bytes,sig,wrong.ExportParameters(false),"preview","windows-x64",2000,2),"wrong key");}
 Reject(()=>test(bytes,new byte[0],key,"preview","windows-x64",2000,2),"unsigned");
 Reject(()=>test(bytes,sig,key,"stable","windows-x64",2000,2),"channel");
 Reject(()=>test(bytes,sig,key,"preview","linux-x64",2000,2),"platform");
 Reject(()=>test(bytes,sig,key,"preview","windows-x64",3000,2),"expiry");
 Reject(()=>test(bytes,sig,key,"preview","windows-x64",500,2),"future");
 Reject(()=>test(bytes,sig,key,"preview","windows-x64",2000,3),"schema");
 v1.VerifyArtifact(new MemoryStream(old));Check(true,"artifact hash");
 Reject(()=>v1.VerifyArtifact(new MemoryStream(next)),"wrong artifact");
 Reject(()=>v1.VerifyArtifact(new MemoryStream(new byte[]{1,2})),"short artifact");
 var badName=Make(old,1);badName.ArtifactName="../bad.exe";Reject(()=>badName.Encode(),"path basename");
 var trailing=new byte[bytes.Length+1];Array.Copy(bytes,trailing,bytes.Length);var trsig=rsa.SignData(trailing,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);Reject(()=>test(trailing,trsig,key,"preview","windows-x64",2000,2),"signed trailing");
 var floor=new UpdateFloor(0,null,null).Advance(v1);floor.DemandCandidate(v1);Check(true,"same exact idempotent");
 Reject(()=>floor.DemandCandidate(Verify(Make(next,1),rsa)),"same sequence changed artifact");
 var v2=Verify(Make(next,2),rsa);var floor2=floor.Advance(v2);Reject(()=>floor2.DemandCandidate(v1),"downgrade");
 string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeUpdateTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
 string dest=Path.Combine(dir,"RemoteBridge.exe"),backup=Path.Combine(dir,"known-good");File.WriteAllBytes(dest,old);
 string stage=UpdateFiles.Stage(v2,new MemoryStream(next),dir);Check(File.ReadAllBytes(stage).Length==4,"actual staging");
 bool failed=false;try{UpdateFiles.Apply(v2,stage,dest,backup,floor,f=>{throw new IOException("fixture durable failure");});}catch(IOException){failed=true;}Check(failed&&File.ReadAllBytes(dest)[0]==1&&File.Exists(stage),"durable failure preserves old");
 UpdateFloor committed=null;UpdateFiles.Apply(v2,stage,dest,backup,floor,f=>committed=f);Check(File.ReadAllBytes(dest)[0]==4&&File.ReadAllBytes(backup)[0]==1&&committed.Sequence==2,"actual apply and backup");
 UpdateFiles.Rollback(v1,backup,dest,committed,v1.ReleaseId,v1.Hash,"windows-x64",2);Check(File.ReadAllBytes(dest)[0]==1&&committed.Sequence==2,"actual rollback floor preserved");
 Reject(()=>committed.DemandCandidate(v1),"rollback does not lower floor");
 var h=v1.Hash;h[0]^=1;v1.VerifyArtifact(new MemoryStream(old));Check(true,"verified hash immutable");
 Console.WriteLine("RELEASE_SECURITY_PASS "+n);
 }}
}
