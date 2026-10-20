using System;using System.IO;using System.Text;using System.Security.Cryptography;
namespace PeerDesk.Windows {
 internal sealed class CandidateUpdateReceipt {
  internal byte[] Manifest,Signature;internal string Backup;
 }
 internal sealed class CandidateUpdateReceipts {
  readonly string directory;readonly byte[] entropy;
  internal CandidateUpdateReceipts(string dir) {directory=Path.GetFullPath(dir);using(var s=SHA256.Create())entropy=s.ComputeHash(Encoding.UTF8.GetBytes("RemoteBridge.CandidateUpdateReceipt.v1\n"+directory.ToUpperInvariant()+"\n"+CandidateReleaseAnchor.KeyId));}
  internal CandidateUpdateReceipt Load(bool rollback) {
   string p=Path.Combine(directory,rollback?".remotebridge-rollback.dpapi":".remotebridge-installed.dpapi");
   Safe(p);byte[] cipher;
   try {using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){cipher=new byte[24577];int n=0,k;while(n<cipher.Length&&(k=f.Read(cipher,n,cipher.Length-n))>0)n+=k;if(n>24576)throw new InvalidDataException();Array.Resize(ref cipher,n);}}
   catch(FileNotFoundException){return null;}
   byte[] raw=ProtectedData.Unprotect(cipher,entropy,DataProtectionScope.CurrentUser);
   try {using(var m=new MemoryStream(raw,false))using(var r=new BinaryReader(m,new UTF8Encoding(false,true))){if(r.ReadInt32()!=1)throw new InvalidDataException();var x=new CandidateUpdateReceipt{Backup=r.ReadString(),Manifest=Field(r,16384),Signature=Field(r,1024)};if(m.Position!=m.Length)throw new InvalidDataException();if(rollback&&(x.Backup!=Path.GetFileName(x.Backup)||!x.Backup.StartsWith(".remotebridge-known-good-",StringComparison.Ordinal)))throw new InvalidDataException();return x;}}
   finally{Array.Clear(raw,0,raw.Length);}
  }
  internal void Save(bool rollback,CandidateUpdateReceipt value) {
   byte[] raw;using(var m=new MemoryStream())using(var w=new BinaryWriter(m,new UTF8Encoding(false,true))){w.Write(1);w.Write(value.Backup??"");w.Write(value.Manifest.Length);w.Write(value.Manifest);w.Write(value.Signature.Length);w.Write(value.Signature);w.Flush();raw=m.ToArray();}
   byte[] cipher;try{cipher=ProtectedData.Protect(raw,entropy,DataProtectionScope.CurrentUser);}finally{Array.Clear(raw,0,raw.Length);}
   string p=Path.Combine(directory,rollback?".remotebridge-rollback.dpapi":".remotebridge-installed.dpapi"),temp=p+"."+Guid.NewGuid().ToString("N")+".tmp";
   Safe(p);Safe(temp);
   using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(cipher,0,cipher.Length);f.Flush(true);}
   Safe(p);if(File.Exists(p))File.Replace(temp,p,null);else File.Move(temp,p);
  }
  static void Safe(string p){for(var d=new DirectoryInfo(Path.GetDirectoryName(p));d!=null;d=d.Parent)if(!d.Exists||(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe receipt path.");try{if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe receipt file.");}catch(FileNotFoundException){}}
  static byte[] Field(BinaryReader r,int max){int n=r.ReadInt32();if(n<1||n>max)throw new InvalidDataException();byte[] b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return b;}
 }
}
