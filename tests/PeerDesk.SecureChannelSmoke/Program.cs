using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PeerDesk.Windows;
internal static class Program {
    static int count;
    static void Check(bool b,string s){if(!b)throw new Exception(s);count++;Console.WriteLine("PASS "+s);}
    static bool Reject(Action a){try{a();return false;}catch(CryptographicException){return true;}}
    static byte[] Hex(string s){return Enumerable.Range(0,s.Length/2).Select(i=>Convert.ToByte(s.Substring(i*2,2),16)).ToArray();}
    static readonly byte[] Context=Encoding.UTF8.GetBytes("session-0123456789abcdef|initiator-1234567890|target-9876543210|epoch-1|authority-2|permissions-3|geometry-1|server-pin-example");
    static int Main(){
        try {
            var v=SecurePeerChannel.Hkdf(Enumerable.Repeat((byte)11,22).ToArray(),Hex("000102030405060708090a0b0c"),Hex("f0f1f2f3f4f5f6f7f8f9"),42);
            Check(SecurePeerIdentity.Hex(v)=="3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865","RFC5869 test vector 1");
            using(var alice=new SecurePeerIdentity())using(var bob=new SecurePeerIdentity())
            using(var a=new SecurePeerChannel(alice,Context,true))using(var b=new SecurePeerChannel(bob,Context,false)) {
                Check(alice.Fingerprint.Length==64&&alice.Fingerprint!=bob.Fingerprint,"independent device RSA identity");
                Check(SecurePeerChannel.CandidateFingerprint(b.Hello)==bob.Fingerprint,"candidate identity parsed and self signature checked");
                Check(Reject(()=>a.Seal(1,new byte[1])),"no data before authentication");
                Check(Reject(()=>a.Establish(b.Hello,null)),"first pairing cannot auto trust a missing pin");
                Check(Reject(()=>a.Establish(b.Hello,new string('0',64))),"wrong approved peer pin rejected");
                var tampered=b.Hello;tampered[tampered.Length-1]^=1;
                Check(Reject(()=>a.Establish(tampered,bob.Fingerprint)),"tampered handshake signature rejected");
                Check(Reject(()=>SecurePeerChannel.CandidateFingerprint(tampered)),"tampered candidate display rejected");
                using(var wrongContext=new SecurePeerChannel(bob,Encoding.UTF8.GetBytes(new string('x',40)),false))
                    Check(Reject(()=>a.Establish(wrongContext.Hello,bob.Fingerprint)),"cross session handshake rejected");
                using(var wrongRole=new SecurePeerChannel(bob,Context,true))
                    Check(Reject(()=>a.Establish(wrongRole.Hello,bob.Fingerprint)),"same role reflection rejected");
                a.Establish(b.Hello,bob.Fingerprint);b.Establish(a.Hello,alice.Fingerprint);
                Check(a.Established&&b.Established,"authenticated P256 handshake both directions");
                Check(!a.KeyConfirmed&&!b.KeyConfirmed,"Hello alone is not a usable data channel");
                Check(Reject(()=>a.Seal(1,new byte[]{1})),"screen denied before encrypted Finished");
                var af=a.CreateFinished();var bf=b.CreateFinished();
                b.ReceiveFinished(af);a.ReceiveFinished(bf);
                Check(a.KeyConfirmed&&b.KeyConfirmed,"bidirectional Finished confirms session keys");
                Check(Reject(()=>b.ReceiveFinished(af)),"repeated Finished rejected");
                for(byte c=1;c<=5;c++){
                    byte[] data=Encoding.UTF8.GetBytes("channel "+c+" remote test");
                    var packet=a.Seal(c,data);
                    Check(!Contains(packet,data),"relay does not contain plaintext channel "+c);
                    var corrupt=(byte[])packet.Clone();corrupt[corrupt.Length-1]^=1;
                    Check(Reject(()=>b.Open(c,corrupt)),"tampered MAC rejected channel "+c);
                    Check(Encoding.UTF8.GetString(b.Open(c,packet))==Encoding.UTF8.GetString(data),"authenticated exact payload channel "+c);
                    Check(Reject(()=>b.Open(c,packet)),"replayed packet rejected channel "+c);
                    var reply=b.Seal(c,data);Check(a.Open(c,reply).SequenceEqual(data),"independent reverse keys channel "+c);
                }
                var framed=a.Seal(1,new byte[]{1,2,3});
                Check(Reject(()=>b.Open(2,framed)),"cross channel substitution rejected");
                Check(b.Open(1,framed).SequenceEqual(new byte[]{1,2,3}),"failed wrong-channel attempt cannot advance replay state");
                var reflect=a.Seal(2,new byte[]{4});
                Check(Reject(()=>a.Open(2,reflect)),"direction reflection rejected");
                Check(b.Open(2,reflect).SequenceEqual(new byte[]{4}),"correct direction remains valid");
                var max=new byte[524288];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(max);
                Check(b.Open(1,a.Seal(1,max)).SequenceEqual(max),"maximum frame round trip bounded");
                Check(Reject(()=>a.Seal(1,new byte[524289])),"oversize screen rejected");
                Check(Reject(()=>a.Seal(3,new byte[65537])),"oversize clipboard rejected");
                Check(Reject(()=>a.Seal(0,new byte[1])),"unknown channel rejected");
                var empty=a.Seal(5,new byte[0]);Check(b.Open(5,empty).Length==0,"empty control envelope round trip");
                using(var nextA=new SecurePeerChannel(alice,Context,true))using(var nextB=new SecurePeerChannel(bob,Context,false)){
                    nextA.Establish(nextB.Hello,bob.Fingerprint);nextB.Establish(nextA.Hello,alice.Fingerprint);
                    var na=nextA.CreateFinished();var nb=nextB.CreateFinished();nextB.ReceiveFinished(na);nextA.ReceiveFinished(nb);
                    Check(Reject(()=>nextB.Open(1,framed)),"new ephemeral keys cannot replay previous transport");
                }
                b.Dispose();bool dead=false;try{b.Open(1,framed);}catch(ObjectDisposedException){dead=true;}Check(dead,"disposed channel never revives");
                string path=Path.Combine(Path.GetTempPath(),"RemoteBridge.identity."+Guid.NewGuid().ToString("N"));
                string fingerprint;
                using(var saved=SecurePeerIdentity.LoadOrCreate(path))fingerprint=saved.Fingerprint;
                using(var loaded=SecurePeerIdentity.LoadOrCreate(path))Check(fingerprint==loaded.Fingerprint,"DPAPI identity reload retains approved identity");
                File.WriteAllBytes(path,new byte[]{1,2,3});Check(Reject(()=>SecurePeerIdentity.LoadOrCreate(path)),"corrupt identity fails closed without replacement");
                // This exact random fixture file is generated here and contains only DPAPI test material.
                File.Delete(path);
            }
            Console.WriteLine("SECURE_CHANNEL_SMOKE_PASS count="+count);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static bool Contains(byte[] haystack,byte[] needle){for(int i=0;i<=haystack.Length-needle.Length;i++){bool same=true;for(int j=0;j<needle.Length;j++)if(haystack[i+j]!=needle[j]){same=false;break;}if(same)return true;}return false;}
}