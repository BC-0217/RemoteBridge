using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PeerDesk.ReleaseSecurity
{
    // Windows-user integrity protection, not anti-rollback against an administrator or
    // restoration of an old, otherwise valid DPAPI blob. Single writer per installation.
    public sealed class UpdateFloorStore
    {
        readonly string path;
        readonly byte[] entropy;
        readonly object gate=new object();
        public UpdateFloorStore(string exactPath,string installationPath,string releaseKeyFingerprint)
        {
            if(String.IsNullOrEmpty(exactPath)||String.IsNullOrEmpty(installationPath)||
                releaseKeyFingerprint==null||releaseKeyFingerprint.Length!=64)throw new ArgumentException("Invalid update floor binding.");
            foreach(char c in releaseKeyFingerprint)if(!Uri.IsHexDigit(c))throw new ArgumentException("Invalid release key fingerprint.");
            path=Path.GetFullPath(exactPath);
            using(var sha=SHA256.Create())entropy=sha.ComputeHash(Encoding.UTF8.GetBytes(
                "RemoteBridge.UpdateFloor.v1\n"+Path.GetFullPath(installationPath).ToUpperInvariant()+"\n"+releaseKeyFingerprint.ToUpperInvariant()));
        }
        public void InitializeExplicit()
        {
            lock(gate)Write(new UpdateFloor(0,null,null),true);
        }
        public UpdateFloor LoadRequired()
        {
            lock(gate)return Load();
        }
        public void Commit(VerifiedRelease candidate)
        {
            lock(gate){var current=Load();Write(current.Advance(candidate),false);}
        }
        UpdateFloor Load()
        {
            SafePath();
            byte[] encoded;
            using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                encoded=new byte[8193];int used=0,n;
                while(used<encoded.Length&&(n=f.Read(encoded,used,encoded.Length-used))>0)used+=n;
                if(used>8192)throw new InvalidDataException("Update floor exceeds bound.");
                Array.Resize(ref encoded,used);
            }
            byte[] clear;
            try{clear=Unprotect(encoded);}catch(CryptographicException ex){throw new InvalidDataException("Update floor authentication failed.",ex);}
            try{
                using(var m=new MemoryStream(clear,false))using(var r=new BinaryReader(m,new UTF8Encoding(false,true))){
                    if(r.ReadInt32()!=0x31465552)throw new InvalidDataException("Update floor version.");
                    long seq=r.ReadInt64();int size=r.ReadInt32();
                    if(seq<0||size<0||size>64||size>m.Length-m.Position)throw new InvalidDataException("Update floor bounds.");
                    string id=new UTF8Encoding(false,true).GetString(r.ReadBytes(size));
                    int hashSize=r.ReadInt32();if(hashSize!=(seq==0?0:32))throw new InvalidDataException("Update floor hash bounds.");
                    byte[] hash=r.ReadBytes(hashSize);
                    if(hash.Length!=hashSize||m.Position!=m.Length||(seq==0&&size!=0))throw new InvalidDataException("Update floor data.");
                    return new UpdateFloor(seq,seq==0?null:id,hash);
                }
            }finally{Array.Clear(clear,0,clear.Length);}
        }
        void Write(UpdateFloor value,bool initialize)
        {
            SafePath();byte[] clear;
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,new UTF8Encoding(false,true))){
                w.Write(0x31465552);w.Write(value.Sequence);
                byte[] id=Encoding.UTF8.GetBytes(value.ReleaseId??"");if(id.Length>64)throw new InvalidDataException("Update floor release ID bound.");
                w.Write(id.Length);w.Write(id);byte[] hash=value.ArtifactHash;w.Write(hash.Length);w.Write(hash);w.Flush();clear=m.ToArray();
            }
            byte[] cipher;try{cipher=Protect(clear);}finally{Array.Clear(clear,0,clear.Length);}
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(cipher,0,cipher.Length);f.Flush(true);}
                SafePath();
                if(initialize)File.Move(temp,path);else File.Replace(temp,path,null);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        void SafePath()
        {
            for(var d=new DirectoryInfo(Path.GetDirectoryName(path));d!=null;d=d.Parent)
                if(!d.Exists||(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe update floor directory.");
            try{if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe update floor file.");}
            catch(FileNotFoundException){}
        }
        byte[] Protect(byte[] bytes){
#if NET48
            return ProtectedData.Protect(bytes,entropy,DataProtectionScope.CurrentUser);
#else
            throw new PlatformNotSupportedException("Update floor storage requires Windows .NET Framework DPAPI.");
#endif
        }
        byte[] Unprotect(byte[] bytes){
#if NET48
            return ProtectedData.Unprotect(bytes,entropy,DataProtectionScope.CurrentUser);
#else
            throw new PlatformNotSupportedException("Update floor storage requires Windows .NET Framework DPAPI.");
#endif
        }
    }
}
