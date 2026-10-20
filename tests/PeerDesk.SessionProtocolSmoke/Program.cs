using System;
using PeerDesk.SessionProtocol;
class Program {
 static int n;
 static void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
 static void Reject(Action a,string s){bool rejected=false;try{a();}catch(ArgumentException){rejected=true;}Check(rejected,s);}
 static SessionBinding Binding(string origin="https://localhost:8443",string own="A",string peer="B",SessionRole role=SessionRole.Initiator,byte pin=0,long rev=1,long perm=1,long geometry=1,long epoch=1){var p=new byte[32];p[0]=pin;return new SessionBinding(new byte[16],own,peer,p,new byte[32],origin,role,epoch,rev,perm,geometry);}
 static void Main(){
 var s=Binding();Check(s.ContextMatches(Binding()),"context equal");
 s.SessionId[0]=1;s.ServerFullPin[0]=1;s.PeerIdentityPin[0]=1;Check(s.ContextMatches(Binding()),"returned arrays immutable");
 foreach(var t in new[]{Binding(own:"X"),Binding(peer:"X"),Binding(role:SessionRole.Target),Binding(origin:"https://localhost:8444"),Binding(pin:1),Binding(rev:2),Binding(perm:2),Binding(geometry:2),Binding(epoch:2)})Check(!s.ContextMatches(t),"context mismatch");
 foreach(var u in new[]{"https://bad host","https://user:pw@host","https://host:bad","http://host","https://host/path","https://host?x","https://host#x"})Reject(()=>Binding(origin:u),"invalid origin");
 var id=new byte[16];var data=new byte[]{1,2,3};var e=new WireEnvelope(WireChannel.Input,0,id,1,1,data);
 id[0]=4;data[0]=5;e.SessionId[0]=6;e.Payload[0]=7;
 int used;var encoded=WireCodec.Encode(e);var d=WireCodec.Decode(encoded,0,out used);Check(used==47&&d.Payload[0]==1&&d.SessionId[0]==0,"roundtrip defensive");
 for(int i=0;i<44;i++){var cut=new byte[i];Array.Copy(encoded,cut,i);Reject(()=>WireCodec.Decode(cut,0,out used),"truncated header");}
 var trailing=new byte[encoded.Length+1];Array.Copy(encoded,trailing,encoded.Length);Reject(()=>WireCodec.Decode(trailing,0,out used),"trailing rejected");
 foreach(int pos in new[]{0,4,5,6}){var bad=(byte[])encoded.Clone();bad[pos]=255;Reject(()=>WireCodec.Decode(bad,0,out used),"header rejected");}
 var over=(byte[])encoded.Clone();over[40]=255;Reject(()=>WireCodec.Decode(over,0,out used),"overflow length");
 Reject(()=>new WireEnvelope(WireChannel.Input,0,new byte[16],1,1,new byte[16385]),"channel bound");
 var gate=new ReceiveReplayGate(s,SessionPermission.Screen|SessionPermission.Clipboard,SessionPermission.None);
 var screen=new WireEnvelope(WireChannel.Screen,0,new byte[16],1,1,new byte[0]);
 Check(!gate.TryAccept(Binding(peer:"wrong"),SessionRole.Target,screen),"wrong context no advance");
 Check(!gate.TryAccept(s,SessionRole.Initiator,screen),"wrong direction no advance");
 Check(gate.TryAccept(s,SessionRole.Target,screen),"accept once");
 Check(!gate.TryAccept(s,SessionRole.Target,screen),"replay denied");
 Check(!gate.TryAccept(s,SessionRole.Target,new WireEnvelope(WireChannel.Clipboard,0,new byte[16],1,1,new byte[0])),"directional grant required");
 var concurrent=new ReceiveReplayGate(s,SessionPermission.Screen,SessionPermission.None);int wins=0;
 System.Threading.Tasks.Parallel.For(0,16,i=>{if(concurrent.TryAccept(s,SessionRole.Target,screen))System.Threading.Interlocked.Increment(ref wins);});
 Check(wins==1,"concurrent duplicate exactly once");
 concurrent.Stop();Check(!concurrent.TryAccept(s,SessionRole.Target,new WireEnvelope(WireChannel.Screen,0,new byte[16],1,2,new byte[0])),"stop tombstone");
 var caps=new ProtocolCapabilities(524288,(SecurityCapability)7);
 Check(caps.Negotiate(new ProtocolCapabilities(1024,(SecurityCapability)7),(SecurityCapability)7).MaxScreenBytes==1024,"bounded negotiate");
 bool downgrade=false;try{caps.Negotiate(new ProtocolCapabilities(1024,SecurityCapability.None),SecurityCapability.EndToEndEncryption);}catch(InvalidOperationException){downgrade=true;}Check(downgrade,"security downgrade denied");
 Console.WriteLine("SESSION_PROTOCOL_PASS "+n);
 }
}
