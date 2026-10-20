using System;
namespace PeerDesk.SessionProtocol {
 [Flags] public enum SessionPermission { None=0, Screen=1, Input=2, Clipboard=4, FileTransfer=8, MonitorControl=16 }
 [Flags] public enum SecurityCapability { None=0, PeerAuthenticated=1, EndToEndEncryption=2, FreshAuthorityLease=4 }
 public sealed class ProtocolCapabilities {
  public int Version {get{return 1;}} public int MaxScreenBytes {get;private set;}
  public SecurityCapability Security {get;private set;}
  public ProtocolCapabilities(int maxScreenBytes,SecurityCapability security) {
   if(maxScreenBytes<1||maxScreenBytes>524288)throw new ArgumentOutOfRangeException("maxScreenBytes");
   if(((int)security&~7)!=0)throw new ArgumentException("Unknown security flags");
   MaxScreenBytes=maxScreenBytes;Security=security;
  }
  public ProtocolCapabilities Negotiate(ProtocolCapabilities peer, SecurityCapability required) {
   if(peer==null||((int)required&~7)!=0)throw new ArgumentException("Invalid capabilities");
   var common=Security&peer.Security;
   if((common&required)!=required)throw new InvalidOperationException("Required security unavailable");
   return new ProtocolCapabilities(Math.Min(MaxScreenBytes,peer.MaxScreenBytes),common);
  }
 }
 // Invoked only AFTER cryptographic authentication and endpoint authority checks.
 // Framing and replay checks are not a substitute for the 100ms authority lease.
 public sealed class ReceiveReplayGate {
  readonly object sync=new object(); readonly SessionBinding binding;
  readonly SessionPermission inbound; readonly SessionPermission directionalGrant;
  readonly long[] sequences=new long[5]; bool stopped;
  public ReceiveReplayGate(SessionBinding binding,SessionPermission inbound,SessionPermission directionalGrant) {
   if(binding==null)throw new ArgumentNullException("binding");
   if((((int)inbound|(int)directionalGrant)&~31)!=0)throw new ArgumentException("Unknown permissions");
   this.binding=binding;this.inbound=inbound;this.directionalGrant=directionalGrant;
  }
  public void Stop(){lock(sync)stopped=true;}
  public bool TryAccept(SessionBinding current,SessionRole sender,WireEnvelope envelope) {
   if(current==null||envelope==null)return false;
   lock(sync) {
    if(stopped||!binding.ContextMatches(current)||sender!=binding.RemoteRole||envelope.Epoch!=binding.Epoch)return false;
    var x=envelope.SessionId;var y=binding.SessionId;int diff=0;for(int i=0;i<16;i++)diff|=x[i]^y[i];if(diff!=0)return false;
    SessionPermission need=SessionPermission.None;
    switch(envelope.Channel) {
     case WireChannel.Control:break;
     case WireChannel.Screen:if(sender!=SessionRole.Target)return false;need=SessionPermission.Screen;break;
     case WireChannel.Input:if(sender!=SessionRole.Initiator)return false;need=SessionPermission.Input;break;
     case WireChannel.Clipboard:need=SessionPermission.Clipboard;if((directionalGrant&need)==0)return false;break;
     case WireChannel.File:need=SessionPermission.FileTransfer;if((directionalGrant&need)==0)return false;break;
     default:return false;
    }
    if((inbound&need)!=need)return false;
    int channel=(int)envelope.Channel;
    if(envelope.Sequence<=sequences[channel])return false;
    sequences[channel]=envelope.Sequence;return true;
   }
  }
 }
}