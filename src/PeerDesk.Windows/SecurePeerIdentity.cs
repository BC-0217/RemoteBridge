using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PeerDesk.Windows
{
    // Device identity is separate from the server bearer and TLS certificate. Private material never goes to the relay.
    internal sealed class SecurePeerIdentity : IDisposable
    {
        private readonly RSACryptoServiceProvider rsa;
        private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("RemoteBridge/device-identity/v1");
        internal SecurePeerIdentity() { rsa=CreateRsa(); }
        private SecurePeerIdentity(byte[] blob) { rsa=CreateRsa(); rsa.ImportCspBlob(blob); }
        private static RSACryptoServiceProvider CreateRsa() {
            var r=new RSACryptoServiceProvider(3072,new CspParameters(24) { Flags=CspProviderFlags.CreateEphemeralKey });
            r.PersistKeyInCsp=false; return r;
        }
        internal byte[] PublicKey {
            get { var p=rsa.ExportParameters(false); using(var s=new MemoryStream())using(var w=new BinaryWriter(s)) {
                w.Write(p.Modulus.Length);w.Write(p.Modulus);w.Write(p.Exponent.Length);w.Write(p.Exponent);return s.ToArray();
            } }
        }
        internal string Fingerprint { get { return Hex(Hash(PublicKey)); } }
        internal byte[] Sign(byte[] value) { return rsa.SignData(value,CryptoConfig.MapNameToOID("SHA256")); }
        internal static bool Verify(byte[] key,byte[] value,byte[] signature) {
            try {
                if(key==null||key.Length>600||signature==null||signature.Length<256||signature.Length>512)return false;
                using(var ms=new MemoryStream(key,false))using(var r=new BinaryReader(ms)) {
                    int n=r.ReadInt32();if(n!=256&&n!=384&&n!=512)return false;
                    byte[] m=r.ReadBytes(n);int e=r.ReadInt32();if(m.Length!=n||e<1||e>4)return false;
                    byte[] exponent=r.ReadBytes(e);if(exponent.Length!=e||ms.Position!=ms.Length)return false;
                    using(var pub=CreateRsa()){pub.ImportParameters(new RSAParameters{Modulus=m,Exponent=exponent});
                        return pub.VerifyData(value,CryptoConfig.MapNameToOID("SHA256"),signature);}
                }
            } catch(CryptographicException) { return false; } catch(IOException) { return false; } catch(ArgumentException) { return false; }
        }
        internal static SecurePeerIdentity LoadOrCreate(string path) {
            if(String.IsNullOrEmpty(path))throw new ArgumentException("Identity path required.");
            if(File.Exists(path))return Load(path);
            var identity=new SecurePeerIdentity();byte[] raw=null,protectedBytes=null;
            try {
                raw=identity.rsa.ExportCspBlob(true);
                protectedBytes=ProtectedData.Protect(raw,Entropy,DataProtectionScope.CurrentUser);
                string directory=Path.GetDirectoryName(Path.GetFullPath(path));Directory.CreateDirectory(directory);
                // Exclusive initial creation; another process's identity must never be silently overwritten.
                try { using(var s=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    s.Write(protectedBytes,0,protectedBytes.Length);s.Flush(true);
                } } catch(IOException) { identity.Dispose();if(File.Exists(path))return Load(path);throw; }
                return identity;
            } catch { identity.Dispose();throw; }
            finally { if(raw!=null)Array.Clear(raw,0,raw.Length);if(protectedBytes!=null)Array.Clear(protectedBytes,0,protectedBytes.Length); }
        }
        private static SecurePeerIdentity Load(string path) {
            byte[] bytes=null,raw=null;
            try {
                using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(s.Length<1||s.Length>16384)throw new CryptographicException("Invalid device identity.");
                    bytes=new byte[(int)s.Length];int offset=0;
                    while(offset<bytes.Length){int n=s.Read(bytes,offset,bytes.Length-offset);if(n==0)throw new EndOfStreamException();offset+=n;}
                }
                raw=ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser);return new SecurePeerIdentity(raw);
            } finally { if(raw!=null)Array.Clear(raw,0,raw.Length);if(bytes!=null)Array.Clear(bytes,0,bytes.Length); }
        }
        internal static byte[] Hash(byte[] value){using(var h=SHA256.Create())return h.ComputeHash(value);}
        internal static string Hex(byte[] value){return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
        public void Dispose(){rsa.Dispose();}
    }
}
