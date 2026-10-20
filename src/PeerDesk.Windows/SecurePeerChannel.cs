using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PeerDesk.Windows
{
    // Authenticated encryption primitive only: transport liveness never grants screen/input/file permission.
    // A live AuthorityLease and endpoint permission revision are still required by the caller.
    internal sealed class SecurePeerChannel : IDisposable
    {
        private const int MaxHello=4096;
        private readonly object gate=new object();
        private readonly SecurePeerIdentity identity;
        private readonly ECDiffieHellmanCng ephemeral;
        private readonly byte[] contextHash;
        private readonly byte role;
        private readonly byte[] localHello;
        private byte[] encOut,macOut,encIn,macIn;
        private readonly long[] sendSequence=new long[6],receiveSequence=new long[6];
        private bool established,disposed,sentFinished,receivedFinished;
        private byte[] finishedBinding;
        internal string PeerFingerprint {get;private set;}
        internal bool Established {get {lock(gate)return established&&!disposed;}}
        internal bool KeyConfirmed {get {lock(gate)return established&&sentFinished&&receivedFinished&&!disposed;}}
        internal byte[] CreateFinished() { lock(gate) { EnsureEstablished();if(sentFinished)throw Failure();var packet=Seal(5,FinishedPayload(role));sentFinished=true;return packet; } }
        internal void ReceiveFinished(byte[] packet) { lock(gate) { EnsureEstablished();if(receivedFinished)throw Failure();byte[] value=Open(5,packet);try {if(!Equal(value,FinishedPayload((byte)(role==1?2:1))))throw Failure();receivedFinished=true;}finally{Clear(value);} } }
        private byte[] FinishedPayload(byte senderRole) { return Concat(Encoding.ASCII.GetBytes("RemoteBridge-Finished/1"),new[]{senderRole},contextHash,finishedBinding); }
        internal byte[] Hello {get {lock(gate){EnsureAlive();return (byte[])localHello.Clone();}}}
        internal SecurePeerChannel(SecurePeerIdentity local,byte[] exactSessionContext,bool initiator) {
            if(local==null)throw new ArgumentNullException("local");
            if(exactSessionContext==null||exactSessionContext.Length<32||exactSessionContext.Length>4096)throw new ArgumentException("Bound session context required.");
            identity=local;role=(byte)(initiator?1:2);contextHash=SecurePeerIdentity.Hash(exactSessionContext);
            ephemeral=new ECDiffieHellmanCng(256);ephemeral.KeyDerivationFunction=ECDiffieHellmanKeyDerivationFunction.Hash;ephemeral.HashAlgorithm=CngAlgorithm.Sha256;
            byte[] unsigned=UnsignedHello(role,contextHash,identity.PublicKey,ephemeral.Key.Export(CngKeyBlobFormat.EccPublicBlob));
            byte[] signature=identity.Sign(unsigned);
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){w.Write(unsigned.Length);w.Write(unsigned);w.Write(signature.Length);w.Write(signature);localHello=s.ToArray();}
        }
        private static byte[] UnsignedHello(byte senderRole,byte[] context,byte[] publicKey,byte[] exchange) {
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){
                w.Write(new byte[]{82,66,72,49});w.Write(senderRole);w.Write(context);
                w.Write(publicKey.Length);w.Write(publicKey);w.Write(exchange.Length);w.Write(exchange);return s.ToArray();
            }
        }
        // Expected pin must be independently approved (known peer pin or explicit first-pairing confirmation).
        // Never silently turn a server-supplied fingerprint into trusted identity.
        internal void Establish(byte[] peerHello,string expectedPeerFingerprint) {
            lock(gate) {
                EnsureAlive();if(established)throw new InvalidOperationException("Channel already established.");
                if(!ValidPin(expectedPeerFingerprint))throw Failure();
                try {
                    if(peerHello==null||peerHello.Length>MaxHello)throw Failure();
                    byte[] unsigned,signature,key,exchange;
                    using(var s=new MemoryStream(peerHello,false))using(var r=new BinaryReader(s)){
                        int n=r.ReadInt32();if(n<80||n>2048)throw Failure();unsigned=ReadExactly(r,n);
                        n=r.ReadInt32();if(n<256||n>512)throw Failure();signature=ReadExactly(r,n);if(s.Position!=s.Length)throw Failure();
                    }
                    using(var s=new MemoryStream(unsigned,false))using(var r=new BinaryReader(s)){
                        if(!Equal(ReadExactly(r,4),new byte[]{82,66,72,49})||r.ReadByte()!=(role==1?2:1)||!Equal(ReadExactly(r,32),contextHash))throw Failure();
                        int n=r.ReadInt32();if(n<1||n>600)throw Failure();key=ReadExactly(r,n);
                        n=r.ReadInt32();if(n!=72)throw Failure();exchange=ReadExactly(r,n);if(s.Position!=s.Length)throw Failure();
                    }
                    string fingerprint=SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(key));
                    if(!String.Equals(expectedPeerFingerprint,fingerprint,StringComparison.OrdinalIgnoreCase)||!SecurePeerIdentity.Verify(key,unsigned,signature))throw Failure();
                    byte[] secret=null,material=null;
                    try {
                        using(var peerKey=CngKey.Import(exchange,CngKeyBlobFormat.EccPublicBlob)){
                            if(peerKey.KeySize!=256)throw Failure();
                            using(var peer=new ECDiffieHellmanCng(peerKey))secret=ephemeral.DeriveKeyMaterial(peer.PublicKey);
                        }
                        byte[] transcript=role==1?Concat(localHello,peerHello):Concat(peerHello,localHello);
                        finishedBinding=SecurePeerIdentity.Hash(Concat(contextHash,transcript));
                        material=Hkdf(secret,finishedBinding,Encoding.ASCII.GetBytes("RemoteBridge/e2ee/v1/AES256-CBC-HMACSHA256"),128);
                        encOut=Slice(material,role==1?0:64,32);macOut=Slice(material,role==1?32:96,32);
                        encIn=Slice(material,role==1?64:0,32);macIn=Slice(material,role==1?96:32,32);
                        PeerFingerprint=fingerprint;established=true;
                    } finally {Clear(secret);Clear(material);}
                } catch(Exception ex) when(ex is CryptographicException||ex is IOException||ex is ArgumentException) {throw Failure();}
            }
        }
        internal byte[] Seal(byte channel,byte[] plaintext) {
            lock(gate) {
                EnsureEstablished();int limit=Limit(channel);
                if(plaintext==null||plaintext.Length>limit)throw Failure();
                if(!KeyConfirmed&&(channel!=5||!Equal(plaintext,FinishedPayload(role))))throw Failure();
                if(sendSequence[channel]==Int64.MaxValue)throw Failure();long sequence=sendSequence[channel]+1;
                byte[] iv=new byte[16],cipher;
                using(var random=RandomNumberGenerator.Create())random.GetBytes(iv);
                using(var aes=Aes.Create()){aes.KeySize=256;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;aes.Key=encOut;aes.IV=iv;
                    using(var encrypt=aes.CreateEncryptor())cipher=encrypt.TransformFinalBlock(plaintext,0,plaintext.Length);}
                byte[] body;
                using(var s=new MemoryStream())using(var w=new BinaryWriter(s)){
                    w.Write(new byte[]{82,66,69,49});w.Write(channel);w.Write(role);w.Write((ushort)0);w.Write(sequence);w.Write(cipher.Length);w.Write(iv);w.Write(cipher);body=s.ToArray();
                }
                byte[] tag;using(var h=new HMACSHA256(macOut))tag=h.ComputeHash(Concat(contextHash,body));
                sendSequence[channel]=sequence;return Concat(body,tag);
            }
        }
        internal byte[] Open(byte channel,byte[] packet) {
            lock(gate) {
                EnsureEstablished();int limit=Limit(channel);
                try {
                    if(packet==null||packet.Length<84||packet.Length>limit+84)throw Failure();
                    int bodyLength=packet.Length-32;
                    byte[] tag;using(var h=new HMACSHA256(macIn))tag=h.ComputeHash(Concat(contextHash,Slice(packet,0,bodyLength)));
                    if(!Equal(tag,Slice(packet,bodyLength,32)))throw Failure(); // MAC before parsing CBC or exposing padding errors
                    long sequence;byte[] iv,cipher;
                    using(var s=new MemoryStream(packet,0,bodyLength,false))using(var r=new BinaryReader(s)){
                        if(!Equal(ReadExactly(r,4),new byte[]{82,66,69,49})||r.ReadByte()!=channel||r.ReadByte()!=(role==1?2:1)||r.ReadUInt16()!=0)throw Failure();
                        sequence=r.ReadInt64();if(sequence<=0||receiveSequence[channel]==Int64.MaxValue||sequence!=receiveSequence[channel]+1)throw Failure();
                        int n=r.ReadInt32();if(n<16||n%16!=0||n>limit+16||s.Length-s.Position!=16L+n)throw Failure();
                        iv=ReadExactly(r,16);cipher=ReadExactly(r,n);if(s.Position!=s.Length)throw Failure();
                    }
                    byte[] plaintext;
                    using(var aes=Aes.Create()){aes.KeySize=256;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;aes.Key=encIn;aes.IV=iv;
                        using(var decrypt=aes.CreateDecryptor())plaintext=decrypt.TransformFinalBlock(cipher,0,cipher.Length);}
                    if(plaintext.Length>limit){Clear(plaintext);throw Failure();}
                    if(!KeyConfirmed&&(channel!=5||!Equal(plaintext,FinishedPayload((byte)(role==1?2:1))))){Clear(plaintext);throw Failure();}
                    receiveSequence[channel]=sequence;return plaintext;
                }catch(Exception ex) when(ex is CryptographicException||ex is IOException||ex is ArgumentException){throw Failure();}
            }
        }
        // Inspect a candidate's public identity to DISPLAY it, not to approve it automatically.
        internal static string CandidateFingerprint(byte[] hello) {
            try {
                if(hello==null||hello.Length>MaxHello)throw Failure();
                using(var s=new MemoryStream(hello,false))using(var r=new BinaryReader(s)){
                    int n=r.ReadInt32();if(n<80||n>2048)throw Failure();byte[] unsigned=ReadExactly(r,n);
                    n=r.ReadInt32();if(n<256||n>512)throw Failure();byte[] signature=ReadExactly(r,n);if(s.Position!=s.Length)throw Failure();
                    using(var u=new MemoryStream(unsigned,false))using(var b=new BinaryReader(u)){
                        if(!Equal(ReadExactly(b,4),new byte[]{82,66,72,49}))throw Failure();byte role=b.ReadByte();if(role!=1&&role!=2)throw Failure();
                        ReadExactly(b,32);n=b.ReadInt32();if(n<1||n>600)throw Failure();byte[] key=ReadExactly(b,n);
                        n=b.ReadInt32();if(n!=72)throw Failure();ReadExactly(b,n);if(u.Position!=u.Length||!SecurePeerIdentity.Verify(key,unsigned,signature))throw Failure();
                        return SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(key));
                    }
                }
            }catch(Exception ex) when(ex is CryptographicException||ex is IOException||ex is ArgumentException){throw Failure();}
        }
        private static bool ValidPin(string s){if(s==null||s.Length!=64)return false;foreach(char c in s)if(!Uri.IsHexDigit(c))return false;return true;}
        private static int Limit(byte c){switch(c){case 1:return 524288;case 2:return 16384;case 3:return 65536;case 4:return 262144;case 5:return 16384;default:throw Failure();}}
        private static byte[] ReadExactly(BinaryReader r,int n){byte[] b=r.ReadBytes(n);if(b.Length!=n)throw Failure();return b;}
        private static byte[] Slice(byte[] b,int offset,int n){var r=new byte[n];Buffer.BlockCopy(b,offset,r,0,n);return r;}
        private static byte[] Concat(params byte[][] list){int n=0;foreach(var b in list)n=checked(n+b.Length);var r=new byte[n];int o=0;foreach(var b in list){Buffer.BlockCopy(b,0,r,o,b.Length);o+=b.Length;}return r;}
        private static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0;}
        internal static byte[] Hkdf(byte[] ikm,byte[] salt,byte[] info,int count){
            if(ikm==null||salt==null||info==null||count<1||count>255*32)throw new ArgumentException("Invalid KDF input.");
            byte[] prk,previous=new byte[0],output=new byte[count];using(var extract=new HMACSHA256(salt))prk=extract.ComputeHash(ikm);
            try {int offset=0;byte round=1;while(offset<count){byte[] next;using(var expand=new HMACSHA256(prk))next=expand.ComputeHash(Concat(previous,info,new[]{round}));
                Clear(previous);previous=next;int n=Math.Min(next.Length,count-offset);Buffer.BlockCopy(next,0,output,offset,n);offset+=n;if(offset<count)round++;}return output;
            }finally{Clear(prk);Clear(previous);}
        }
        private void EnsureAlive(){if(disposed)throw new ObjectDisposedException("SecurePeerChannel");}
        private void EnsureEstablished(){EnsureAlive();if(!established)throw Failure();}
        private static CryptographicException Failure(){return new CryptographicException("Secure channel authentication failed.");}
        private static void Clear(byte[] b){if(b!=null)Array.Clear(b,0,b.Length);}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;established=false;Clear(encOut);Clear(encIn);Clear(macOut);Clear(macIn);Clear(finishedBinding);ephemeral.Dispose();}}
    }
}
