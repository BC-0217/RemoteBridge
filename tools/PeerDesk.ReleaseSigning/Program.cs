using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using PeerDesk.ReleaseSecurity;
internal static class Program {
 static readonly byte[] Purpose=Encoding.UTF8.GetBytes("RemoteBridge.CandidateReleaseSigning.v1");
 static int Main(string[] a) {
  try {
   if(a.Length==3&&a[0]=="initialize") { Initialize(a[1],a[2]); Console.WriteLine("CANDIDATE_KEY_INITIALIZED");return 0; }
   if(a.Length==7&&a[0]=="sign") { Sign(a);Console.WriteLine("CANDIDATE_BUNDLE_SIGNED");return 0; }
   Console.Error.WriteLine("initialize <private.dpapi> <public-anchor.cs> | sign <private.dpapi> <artifact> <output-dir> <sequence> <release-id> <windows-x86|windows-x64>");return 2;
  } catch(Exception e) {Console.Error.WriteLine(e.GetType().Name+": signing operation failed.");return 1;}
 }
 static void Initialize(string secret,string anchor) {
  if(File.Exists(secret)||File.Exists(anchor))throw new IOException("Refusing overwrite");
  using(var rsa=new RSACryptoServiceProvider(3072)) {
   rsa.PersistKeyInCsp=false;byte[] raw=Encoding.UTF8.GetBytes(rsa.ToXmlString(true));
   byte[] blob;try{blob=ProtectedData.Protect(raw,Purpose,DataProtectionScope.CurrentUser);}finally{Array.Clear(raw,0,raw.Length);}
   Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(secret)));
   using(var f=new FileStream(secret,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(blob,0,blob.Length);f.Flush(true);}
   var acl=new System.Security.AccessControl.FileSecurity();
   var account=System.Security.Principal.WindowsIdentity.GetCurrent().User;
   acl.SetOwner(account);acl.SetAccessRuleProtection(true,false);
   acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(account,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.AccessControlType.Allow));
   File.SetAccessControl(secret,acl);
   var p=rsa.ExportParameters(false);
   string code="using System;using System.Security.Cryptography;\nnamespace PeerDesk.Windows { internal static class CandidateReleaseAnchor { internal const string KeyId=\"candidate-local-v1\"; internal static RSAParameters PublicKey { get { return new RSAParameters { Modulus=Convert.FromBase64String(\""+Convert.ToBase64String(p.Modulus)+"\"),Exponent=Convert.FromBase64String(\""+Convert.ToBase64String(p.Exponent)+"\") }; } } } }\n";
   using(var f=new FileStream(anchor,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var w=new StreamWriter(f,new UTF8Encoding(false)))w.Write(code);
  }
 }
 static void Sign(string[] a) {
  byte[] b=Read(a[1],32768),raw=ProtectedData.Unprotect(b,Purpose,DataProtectionScope.CurrentUser);
  using(var rsa=new RSACryptoServiceProvider()) {
   rsa.PersistKeyInCsp=false;try{rsa.FromXmlString(new UTF8Encoding(false,true).GetString(raw));}finally{Array.Clear(raw,0,raw.Length);}
   byte[] hash;long size;using(var f=File.OpenRead(a[2]))using(var sha=SHA256.Create()){size=f.Length;hash=sha.ComputeHash(f);}
   var now=DateTimeOffset.UtcNow;
   var m=new ReleaseManifest{Product="RemoteBridge",Channel="preview",Platform=a[6],ReleaseId=a[5],ArtifactName="RemoteBridge.exe",KeyId="candidate-local-v1",MinimumOS="6.1",Sequence=long.Parse(a[4],System.Globalization.CultureInfo.InvariantCulture),IssuedAt=now.ToUnixTimeSeconds(),ExpiresAt=now.AddDays(30).ToUnixTimeSeconds(),ArtifactLength=size,Sha256=hash,MinimumSchema=1,MaximumSchema=2};
   byte[] canonical=m.Encode(),sig=rsa.SignData(canonical,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
   Directory.CreateDirectory(a[3]);
   WriteNew(Path.Combine(a[3],"release.manifest"),canonical);WriteNew(Path.Combine(a[3],"release.signature"),sig);
   using(var source=File.OpenRead(a[2]))using(var dest=new FileStream(Path.Combine(a[3],"RemoteBridge.exe"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){source.CopyTo(dest);dest.Flush(true);}
  }
 }
 static byte[] Read(string p,int max){using(var f=File.OpenRead(p)){var b=new byte[max+1];int n=0,k;while(n<b.Length&&(k=f.Read(b,n,b.Length-n))>0)n+=k;if(n>max)throw new InvalidDataException();Array.Resize(ref b,n);return b;}}
 static void WriteNew(string p,byte[] b){using(var f=new FileStream(p,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(b,0,b.Length);f.Flush(true);}}
}
