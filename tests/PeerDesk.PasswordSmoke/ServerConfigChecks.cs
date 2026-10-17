using System.Net;
using PeerDesk.Server;
internal static class ServerConfigChecks {
 public static void Run(Action<bool,string> check) {
  ServerOptions Parse(Dictionary<string,string> values)=>ServerOptions.FromEnvironment(k=>values.TryGetValue(k,out var v)?v:null);
  void Deny(Dictionary<string,string> values,string name){bool denied=false;try{Parse(values);}catch(ArgumentException){denied=true;}catch(InvalidOperationException){denied=true;}check(denied,name);}
  var defaults=Parse(new());check(defaults.BindAddress.Equals(IPAddress.Loopback)&&defaults.Port==0&&!defaults.AllowPublicBind,"config defaults loopback ephemeral");
  var pub=Parse(new(){{"PEERDESK_BIND_ADDRESS","0.0.0.0"},{"PEERDESK_PORT","8443"},{"PEERDESK_ALLOW_PUBLIC_BIND","true"},{"PEERDESK_MAX_SESSIONS","2"},{"PEERDESK_MAX_DEVICES","10"},{"PEERDESK_SESSION_TTL_SECONDS","3600"}});
  check(pub.Port==8443&&pub.BindAddress.Equals(IPAddress.Any)&&pub.MaxSessions==2&&pub.MaxDevices==10&&pub.SessionTtl==TimeSpan.FromHours(1),"config explicit public parsed without opening socket");
  Deny(new(){{"PEERDESK_BIND_ADDRESS","0.0.0.0"},{"PEERDESK_PORT","8443"}},"config public requires optin");
  Deny(new(){{"PEERDESK_BIND_ADDRESS","0.0.0.0"},{"PEERDESK_ALLOW_PUBLIC_BIND","true"}},"config public requires fixed port");
  foreach(var address in new[]{"localhost","127.1","0x7f000001","224.0.0.1","255.255.255.255"," "})
   Deny(new(){{"PEERDESK_BIND_ADDRESS",address}},"config invalid bind "+address);
  foreach(var port in new[]{"-1","65536"," 8443","+8443","8443.0",""})
   Deny(new(){{"PEERDESK_PORT",port}},"config invalid port");
  foreach(var pair in new[]{("PEERDESK_MAX_SESSIONS","0"),("PEERDESK_MAX_SESSIONS","501"),("PEERDESK_MAX_DEVICES","1001"),("PEERDESK_SESSION_TTL_SECONDS","0"),("PEERDESK_SESSION_TTL_SECONDS","86401"),("PEERDESK_ALLOW_PUBLIC_BIND","yes")})
   Deny(new(){{pair.Item1,pair.Item2}},"config invalid bound "+pair.Item1);
  bool invalid=false;try{new ServerOptions{Port=-1}.Validate();}catch(ArgumentException){invalid=true;}catch(InvalidOperationException){invalid=true;}
  check(invalid,"config explicit options validated");
 }
}
