using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
namespace PeerDesk.ReleaseSecurity {
 public sealed class ReleaseManifest {
  public string Product,Channel,Platform,ReleaseId,ArtifactName,KeyId,MinimumOS;
  public long Sequence,IssuedAt,ExpiresAt,ArtifactLength;
  public int MinimumSchema,MaximumSchema;
  public byte[] Sha256;
  internal void Validate() {
   if(Product!="RemoteBridge"||(Channel!="preview"&&Channel!="stable")||(Platform!="windows-x86"&&Platform!="windows-x64"&&Platform!="linux-x64"))throw new InvalidDataException("Unsupported release identity.");
   Ascii(ReleaseId,64);Ascii(KeyId,64);Ascii(MinimumOS,32);Ascii(ArtifactName,100);
   if(ArtifactName.IndexOfAny(new[]{'/', '\\', ':', '*','?','"','<','>','|'})>=0||ArtifactName.Contains("..")||ArtifactName.EndsWith(".")||ArtifactName.EndsWith(" "))throw new InvalidDataException("Unsafe artifact name.");
   if(Sequence<=0||IssuedAt<0||ExpiresAt<=IssuedAt||ArtifactLength<1||ArtifactLength>536870912||MinimumSchema<1||MaximumSchema<MinimumSchema||Sha256==null||Sha256.Length!=32)throw new InvalidDataException("Invalid release bounds.");
   Version v;if(!Version.TryParse(MinimumOS,out v))throw new InvalidDataException("Invalid OS version.");
  }
  static void Ascii(string s,int max){if(String.IsNullOrEmpty(s)||s.Length>max)throw new InvalidDataException("Invalid field.");foreach(char c in s)if(c<33||c>126)throw new InvalidDataException("Invalid field.");}
  public byte[] Encode() {
   Validate();using(var m=new MemoryStream()){m.Write(new byte[]{82,66,85,80,49,1},0,6);
    foreach(var s in new[]{Product,Channel,Platform,ReleaseId,ArtifactName,KeyId,MinimumOS}){var b=new UTF8Encoding(false,true).GetBytes(s);Number(m,b.Length,2);m.Write(b,0,b.Length);}
    foreach(long n in new[]{Sequence,IssuedAt,ExpiresAt,ArtifactLength})Number(m,n,8);
    m.Write(Sha256,0,32);Number(m,MinimumSchema,4);Number(m,MaximumSchema,4);return m.ToArray();}
  }
  internal static ReleaseManifest Decode(byte[] bytes) {
   if(bytes==null||bytes.Length>16384)throw new InvalidDataException("Manifest bound.");
   using(var m=new MemoryStream(bytes,false)){foreach(byte b in new byte[]{82,66,85,80,49,1})if(m.ReadByte()!=b)throw new InvalidDataException("Manifest version.");
    var s=new string[7];for(int i=0;i<s.Length;i++){long len=ReadNumber(m,2);if(len>128||len>m.Length-m.Position)throw new InvalidDataException("Field bound.");var b=new byte[(int)len];ReadExact(m,b);s[i]=new UTF8Encoding(false,true).GetString(b);}
    var x=new ReleaseManifest{Product=s[0],Channel=s[1],Platform=s[2],ReleaseId=s[3],ArtifactName=s[4],KeyId=s[5],MinimumOS=s[6],Sequence=ReadNumber(m,8),IssuedAt=ReadNumber(m,8),ExpiresAt=ReadNumber(m,8),ArtifactLength=ReadNumber(m,8),Sha256=new byte[32]};
    ReadExact(m,x.Sha256);x.MinimumSchema=checked((int)ReadNumber(m,4));x.MaximumSchema=checked((int)ReadNumber(m,4));if(m.Position!=m.Length)throw new InvalidDataException("Trailing manifest data.");x.Validate();if(!Equal(x.Encode(),bytes))throw new InvalidDataException("Noncanonical manifest.");return x;}
  }
  internal static bool Equal(byte[] a,byte[] b){if(a==null||b==null||a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0;}
  static void Number(Stream s,long n,int size){for(int i=size-1;i>=0;i--)s.WriteByte((byte)((ulong)n>>(8*i)));}
  static long ReadNumber(Stream s,int size){ulong n=0;for(int i=0;i<size;i++){int b=s.ReadByte();if(b<0)throw new InvalidDataException("Truncated manifest.");n=(n<<8)|(byte)b;}if(n>long.MaxValue)throw new InvalidDataException("Integer overflow.");return(long)n;}
  static void ReadExact(Stream s,byte[] b){int o=0;while(o<b.Length){int n=s.Read(b,o,b.Length-o);if(n==0)throw new InvalidDataException("Truncated manifest.");o+=n;}}
 }
 public sealed class VerifiedRelease {
  readonly ReleaseManifest data;
  internal VerifiedRelease(ReleaseManifest x){data=ReleaseManifest.Decode(x.Encode());}
  public string ReleaseId {get{return data.ReleaseId;}} public string ArtifactName {get{return data.ArtifactName;}}
  public string Platform {get{return data.Platform;}} public long Sequence {get{return data.Sequence;}}
  public long ArtifactLength {get{return data.ArtifactLength;}} public byte[] Hash {get{return(byte[])data.Sha256.Clone();}}
  public bool SupportsSchema(int schema){return schema>=data.MinimumSchema&&schema<=data.MaximumSchema;}
  public void VerifyArtifact(Stream source) {
   using(var sha=SHA256.Create()){var b=new byte[65536];long total=0;int n;
    while((n=source.Read(b,0,(int)Math.Min(b.Length,ArtifactLength-total+1)))>0){total+=n;if(total>ArtifactLength)throw new InvalidDataException("Artifact integrity failed.");sha.TransformBlock(b,0,n,b,0);}
    sha.TransformFinalBlock(new byte[0],0,0);if(total!=ArtifactLength||!ReleaseManifest.Equal(sha.Hash,data.Sha256))throw new InvalidDataException("Artifact integrity failed.");}
  }
 }
 public static class SignedReleaseVerifier {
  public static VerifiedRelease Verify(byte[] canonical,byte[] signature,RSAParameters pinnedKey,string expectedKeyId,string channel,string platform,DateTimeOffset now,Version os,int schema) {
   if(canonical==null||canonical.Length>16384||signature==null||signature.Length>1024||pinnedKey.Modulus==null||pinnedKey.Modulus.Length<256||pinnedKey.Modulus.Length>1024)throw new InvalidDataException("Release authentication failed.");
   bool valid=false;try{using(var rsa=RSA.Create()){rsa.ImportParameters(pinnedKey);valid=rsa.VerifyData(canonical,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);}}catch(CryptographicException){}
   if(!valid)throw new InvalidDataException("Release authentication failed.");
   var m=ReleaseManifest.Decode(canonical);long utc=now.ToUnixTimeSeconds();
   if(m.KeyId!=expectedKeyId||m.Channel!=channel||m.Platform!=platform||m.IssuedAt>utc+300||m.ExpiresAt<=utc||os==null||new Version(m.MinimumOS)>os||schema<m.MinimumSchema||schema>m.MaximumSchema)throw new InvalidDataException("Release policy rejected.");
   return new VerifiedRelease(m);
  }
 }
 public sealed class UpdateFloor {
  public long Sequence {get;private set;} public string ReleaseId {get;private set;} readonly byte[] hash;
  public byte[] ArtifactHash {get{return (byte[])hash.Clone();}}
  public UpdateFloor(long sequence,string releaseId,byte[] artifactHash){if(sequence<0||(sequence>0&&(String.IsNullOrEmpty(releaseId)||artifactHash==null||artifactHash.Length!=32)))throw new ArgumentException("Invalid floor.");Sequence=sequence;ReleaseId=releaseId;hash=artifactHash==null?new byte[0]:(byte[])artifactHash.Clone();}
  public void DemandCandidate(VerifiedRelease release){if(release==null||release.Sequence<Sequence||(release.Sequence==Sequence&&(release.ReleaseId!=ReleaseId||!ReleaseManifest.Equal(release.Hash,hash))))throw new InvalidDataException("Release rollback denied.");}
  public UpdateFloor Advance(VerifiedRelease release){DemandCandidate(release);return new UpdateFloor(release.Sequence,release.ReleaseId,release.Hash);}
  public void DemandKnownGoodRecovery(VerifiedRelease knownGood,string installedReleaseId,byte[] installedHash,string platform,int schema){if(knownGood==null||knownGood.ReleaseId!=installedReleaseId||!ReleaseManifest.Equal(knownGood.Hash,installedHash)||knownGood.Platform!=platform||!knownGood.SupportsSchema(schema))throw new InvalidDataException("Untrusted recovery.");}
 }
 // A verifier/stager, not an automatic trust bootstrap. Caller pins the release key independently of TLS.
 public static class UpdateFiles {
  static string SafePath(string path){var p=Path.GetFullPath(path);for(var d=new DirectoryInfo(Path.GetDirectoryName(p));d!=null;d=d.Parent)if(!d.Exists||(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe update directory.");if(File.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe update target.");return p;}
  public static string Stage(VerifiedRelease release,Stream source,string installDirectory) {
   if(release==null)throw new ArgumentNullException("release");
   string p=SafePath(Path.Combine(installDirectory,".remotebridge-stage-"+Guid.NewGuid().ToString("N")));
   using(var f=new FileStream(p,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)){var b=new byte[65536];long count=0;int n;while((n=source.Read(b,0,(int)Math.Min(b.Length,release.ArtifactLength-count+1)))>0){count+=n;if(count>release.ArtifactLength)throw new InvalidDataException("Artifact too large.");f.Write(b,0,n);}f.Flush(true);f.Position=0;release.VerifyArtifact(f);}
   return p;
  }
  public static void Apply(VerifiedRelease release,string stagedPath,string destination,string backupPath,UpdateFloor current,Action<UpdateFloor> durableFloorCommit) {
   current.DemandCandidate(release);string stage=SafePath(stagedPath),dest=SafePath(destination),backup=SafePath(backupPath);
   if(stage==dest||stage==backup||dest==backup||Path.GetDirectoryName(stage)!=Path.GetDirectoryName(dest)||Path.GetDirectoryName(backup)!=Path.GetDirectoryName(dest)||File.Exists(backup))throw new IOException("Unsafe update paths.");
   using(var f=new FileStream(stage,FileMode.Open,FileAccess.Read,FileShare.Read)){release.VerifyArtifact(f);}
   // Commit highest accepted identity BEFORE replacing executable; same exact candidate may retry after failure.
   durableFloorCommit(current.Advance(release));
   if(File.Exists(dest))File.Replace(stage,dest,backup,true);else File.Move(stage,dest);
  }
  public static void Rollback(VerifiedRelease knownGood,string backupPath,string destination,UpdateFloor unchangedFloor,string installedReleaseId,byte[] installedHash,string platform,int schema) {
   unchangedFloor.DemandKnownGoodRecovery(knownGood,installedReleaseId,installedHash,platform,schema);
   string backup=SafePath(backupPath),dest=SafePath(destination);if(backup==dest||Path.GetDirectoryName(backup)!=Path.GetDirectoryName(dest))throw new IOException("Unsafe recovery path.");
   using(var f=new FileStream(backup,FileMode.Open,FileAccess.Read,FileShare.Read)){knownGood.VerifyArtifact(f);}
   string failed=SafePath(Path.Combine(Path.GetDirectoryName(dest),".remotebridge-failed-"+Guid.NewGuid().ToString("N")));
   if(File.Exists(dest))File.Replace(backup,dest,failed,true);else File.Move(backup,dest);
   // Highest accepted floor intentionally remains unchanged.
  }
 }
}
