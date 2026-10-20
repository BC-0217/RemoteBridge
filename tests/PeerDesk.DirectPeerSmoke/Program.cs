using System;using System.IO;using System.Net;using System.Text;using System.Threading;using System.Threading.Tasks;using System.Security.Cryptography;using PeerDesk.Windows;
internal static class Program {
 static int n;static void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
 static bool Reject(Action a){try{a();return false;}catch(CryptographicException){return true;}}
 static int Main(){try{Run().GetAwaiter().GetResult();Console.WriteLine("DIRECT_PEER_PASS count="+n);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static async Task Run(){
 byte[] context=Encoding.ASCII.GetBytes("signed-direct/session123/initiator123/target987/epoch1/serverpin");
 using(var alice=new SecurePeerIdentity())using(var bob=new SecurePeerIdentity()){
 bool disabled=false;try{using(var denied=new DirectPeerListener(bob,context,false,IPAddress.Loopback,false,DateTimeOffset.UtcNow)){} }catch(InvalidOperationException){disabled=true;}Check(disabled,"no listener without explicit optin");
 using(var listener=new DirectPeerListener(bob,context,false,IPAddress.Loopback,true,DateTimeOffset.UtcNow))
 using(var ca=new SecurePeerChannel(alice,context,true))using(var cb=new SecurePeerChannel(bob,context,false)){
 byte[] offer=listener.Offer;var verified=DirectPeerOffer.Verify(offer,bob.Fingerprint,context,false,DateTimeOffset.UtcNow);
 Check(verified.Address.Equals(IPAddress.Loopback)&&verified.Port>=1024,"signed literal IP candidate verified");
 Check(Reject(()=>DirectPeerOffer.Verify(offer,new string('0',64),context,false,DateTimeOffset.UtcNow)),"wrong peer cannot cause outbound dial");
 Check(Reject(()=>DirectPeerOffer.Verify(offer,bob.Fingerprint,new byte[32],false,DateTimeOffset.UtcNow)),"candidate bound to exact session");
 Check(Reject(()=>DirectPeerOffer.Verify(offer,bob.Fingerprint,context,true,DateTimeOffset.UtcNow)),"candidate role reflection denied");
 Check(Reject(()=>DirectPeerOffer.Verify(offer,bob.Fingerprint,context,false,DateTimeOffset.UtcNow.AddSeconds(90))),"expired candidate denied");
 var corrupt=(byte[])offer.Clone();corrupt[corrupt.Length-1]^=1;Check(Reject(()=>DirectPeerOffer.Verify(corrupt,bob.Fingerprint,context,false,DateTimeOffset.UtcNow)),"tampered signed candidate denied");
 var accept=listener.AcceptAsync(cb,alice.Fingerprint,CancellationToken.None);
 using(var outgoing=await DirectPeerOffer.TryConnectAsync(offer,bob.Fingerprint,context,false,DateTimeOffset.UtcNow,ca,CancellationToken.None))
 using(var incoming=await accept){
 Check(outgoing!=null&&ca.KeyConfirmed&&cb.KeyConfirmed,"actual direct TCP requires bidirectional encrypted Finished");
 byte[] data=Encoding.UTF8.GetBytes("Direct encrypted test");
 await DirectPeerOffer.WritePacket(outgoing.Stream,ca.Seal(1,data),524372,CancellationToken.None);
 byte[] received=await DirectPeerOffer.ReadPacket(incoming.Stream,524372,CancellationToken.None);
 Check(Encoding.UTF8.GetString(cb.Open(1,received))=="Direct encrypted test","actual direct encrypted data roundtrip");
 }
 }
 byte[] unreachable;using(var listener=new DirectPeerListener(bob,context,false,IPAddress.Loopback,true,DateTimeOffset.UtcNow))unreachable=listener.Offer;
 using(var next=new SecurePeerChannel(alice,context,true)){
 var result=await DirectPeerOffer.TryConnectAsync(unreachable,bob.Fingerprint,context,false,DateTimeOffset.UtcNow,next,CancellationToken.None);
 Check(result==null&&!next.Established,"unreachable before handshake permits fresh relay fallback");
 }
 }
 }
}