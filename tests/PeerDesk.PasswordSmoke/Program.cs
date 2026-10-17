using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;

int count=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
ServerConfigChecks.Run(Check);
using var rsa=RSA.Create(2048);
var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());
using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var clock=new TestClock(); const string invite="MEDIA_FIXTURE_PRIVATE_INVITE";
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions{InvitationCode=invite,EphemeralCertificate=cert,Clock=clock,Port=0,SessionTtl=TimeSpan.FromHours(3)});
await app.StartAsync();
try{
 string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 using var http=PinnedHttpClientFactory.CreateClient(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)));
 async Task<HttpResponseMessage> Send(string method,string path,string? token=null,object? body=null){
   using var req=new HttpRequestMessage(new HttpMethod(method),url+path);
   if(token!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
   if(body!=null)req.Content=JsonContent.Create(body);
   return await http.SendAsync(req);
 }
 async Task<HttpResponseMessage> SendRaw(string path,string token,string json){
   using var req=new HttpRequestMessage(HttpMethod.Post,url+path);
   req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
   req.Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json");
   return await http.SendAsync(req);
 }
 async Task<JsonElement> Me(string token){
   using var r=await Send("GET","/api/v1/devices/me",token);r.EnsureSuccessStatusCode();
   return await r.Content.ReadFromJsonAsync<JsonElement>();
 }
 async Task<T> Post<T>(string path,string? token,object body){
   using var r=await Send("POST",path,token,body);r.EnsureSuccessStatusCode();
   return (await r.Content.ReadFromJsonAsync<T>())!;
 }
 async Task Status(string method,string path,string token,object? body,int status,string name){
   using var r=await Send(method,path,token,body);Check((int)r.StatusCode==status,name+" status="+(int)r.StatusCode);
 }
 using(var health=await Send("GET","/healthz")) { var body=await health.Content.ReadFromJsonAsync<JsonElement>(); Check(health.StatusCode==HttpStatusCode.OK&&body.EnumerateObject().Count()==1&&body.GetProperty("status").GetString()=="ok","HTTPS healthz exposes only status"); }
 var a=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var b=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var c=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));

 await Status("GET","/api/v1/devices/me","BAD",null,401,"me rejects invalid credential");
 using(var r=await Send("GET","/api/v1/devices/me",b.BearerToken)){
  Check((int)r.StatusCode==200&&r.Headers.CacheControl?.NoStore==true,"me authenticated no-store");
  var obj=await r.Content.ReadFromJsonAsync<JsonElement>();
  Check(obj.EnumerateObject().Count()==5&&obj.GetProperty("deviceId").GetString()==b.DeviceId&&obj.GetProperty("passwordExpiresAt").GetDateTimeOffset()==b.PasswordExpiresAt
   &&obj.GetProperty("passwordGeneration").GetInt64()==b.PasswordGeneration&&obj.GetProperty("rotationRequired").ValueKind==JsonValueKind.False
   &&obj.GetProperty("serverTimeUtc").GetDateTimeOffset()==clock.UtcNow,"me exact own public fields only");
 }
 using(var r=await Send("POST","/api/v1/register",null,new RegisterRequest(invite))) Check(r.Headers.CacheControl?.NoStore==true,"registration no-store");
 bool IsDeviceId(string s)=>s.Length==10&&s[0]>='1'&&s[0]<='9'&&s.All(ch=>ch>='0'&&ch<='9');
 bool IsPassword(string s)=>s.Length==9&&s.All(ch=>(ch>='A'&&ch<='Z')||(ch>='a'&&ch<='z'))&&s.Any(char.IsUpper)&&s.Any(char.IsLower);
 string FlipCase(string s)=>new string(s.Select(ch=>char.IsUpper(ch)?char.ToLowerInvariant(ch):char.ToUpperInvariant(ch)).ToArray());
 Check(IsDeviceId(a.DeviceId)&&IsDeviceId(b.DeviceId)&&IsDeviceId(c.DeviceId)&&new[]{a.DeviceId,b.DeviceId,c.DeviceId}.Distinct().Count()==3,"device ID exactly10 digits first1-9 unique");
 Check(IsPassword(a.TemporaryPassword)&&IsPassword(b.TemporaryPassword)&&IsPassword(c.TemporaryPassword) && b.TemporaryPassword!=a.TemporaryPassword && b.PasswordExpiresAt==clock.UtcNow.AddHours(1),"password 9 letters mixed case and exact 1h expiry");
 Check(a.PasswordGeneration==1&&b.PasswordGeneration==1&&c.PasswordGeneration==1,"registration returns password generation 1");
 {
  var ids=new HashSet<string>(StringComparer.Ordinal);var pws=new HashSet<string>(StringComparer.Ordinal);var cred=new PasswordCredential();
  for(int i=0;i<500;i++){string id=DeviceIdGenerator.NewId();if(!IsDeviceId(id))throw new Exception("device ID sample format");ids.Add(id);string pw=cred.Rotate(clock.UtcNow).PlainPassword;if(!IsPassword(pw))throw new Exception("password sample format");pws.Add(pw);}
  Check(ids.Count>=499&&pws.Count>=499,"device ID and password uniqueness sample");
 }
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest("UNKNOWN","0000000000000000"),401,"unknown target denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId),401,"missing password denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"wrong"),401,"malformed password denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,a.TemporaryPassword),401,"other device password denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,""),401,"empty password denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"not hex"),401,"fifth failed password denied");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword),429,"target sixth attempt locked even correct");
 clock.Advance(TimeSpan.FromSeconds(61));
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,FlipCase(b.TemporaryPassword)),401,"flip-case password denied (case-sensitive)");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword.ToLowerInvariant()),401,"lowercased password denied (case-sensitive)");
 // Connect by c invalidates b's password immediately (target-only), with no new plaintext.
 var other=await Post<SessionResponse>("/api/v1/sessions/request",c.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword));
 Check(other.Status=="Pending","valid exact-case password still requires consent");
 await Status("POST","/api/v1/sessions/connect",c.BearerToken,new ConnectRequest(other.SessionId),400,"password never bypasses consent");
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(other.SessionId,true));
 var otherConnected=await Post<SessionResponse>("/api/v1/sessions/connect",c.BearerToken,new ConnectRequest(other.SessionId));
 Check(otherConnected.Status=="Connected","approved session connects");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword),401,"connect invalidates target old password immediately");
 var meB=await Me(b.BearerToken);
 Check(meB.GetProperty("passwordGeneration").GetInt64()==2&&meB.GetProperty("rotationRequired").GetBoolean(),"connect advances target generation and marks rotation required");
 var meC=await Me(c.BearerToken);
 Check(meC.GetProperty("passwordGeneration").GetInt64()==1&&!meC.GetProperty("rotationRequired").GetBoolean()&&meC.GetProperty("passwordExpiresAt").GetDateTimeOffset()==c.PasswordExpiresAt,"connect leaves initiator password metadata unchanged");
 await Status("POST","/api/v1/sessions/connect",c.BearerToken,new ConnectRequest(other.SessionId),400,"duplicate connect rejected");
 Check((await Me(b.BearerToken)).GetProperty("passwordGeneration").GetInt64()==2,"duplicate connect does not invalidate again");

 // Conditional rotate: strict body validation and 409 mismatch without state change.
 foreach(var (json,label) in new[]{("{\"expectedGeneration\":\"2\"}","string"),("{\"expectedGeneration\":2.5}","fraction"),("{\"expectedGeneration\":2.0}","decimal point"),("{\"expectedGeneration\":2e0}","exponent"),
   ("{\"expectedGeneration\":-1}","negative"),("{\"expectedGeneration\":2,\"x\":1}","unknown field"),("{\"ExpectedGeneration\":2}","wrong case field"),
   ("{\"expectedGeneration\":2,\"expectedGeneration\":2}","duplicate field"),("{\"expectedGeneration\":null}","null"),("[]","array"),("null","null body"),("{","malformed"),("{\"expectedGeneration\":99999999999999999999}","out of range")}){
  using var r=await SendRaw("/api/v1/devices/password/rotate",b.BearerToken,json);
  Check((int)r.StatusCode==400,"conditional rotate rejects "+label+" status="+(int)r.StatusCode);
 }
 using(var r=await SendRaw("/api/v1/devices/password/rotate",b.BearerToken,"{\"expectedGeneration\":1}")) Check((int)r.StatusCode==409,"conditional rotate stale generation 409 status="+(int)r.StatusCode);
 meB=await Me(b.BearerToken);
 Check(meB.GetProperty("passwordGeneration").GetInt64()==2&&meB.GetProperty("rotationRequired").GetBoolean()&&meB.GetProperty("passwordExpiresAt").GetDateTimeOffset()==DateTimeOffset.MinValue,"rejected rotates leave state unchanged");
 PasswordResponse refreshed;
 using(var r=await SendRaw("/api/v1/devices/password/rotate",b.BearerToken,"{\"expectedGeneration\":2}")){
  Check((int)r.StatusCode==200&&r.Headers.CacheControl?.NoStore==true,"conditional rotate matching generation");refreshed=(await r.Content.ReadFromJsonAsync<PasswordResponse>())!;
 }
 meB=await Me(b.BearerToken);
 Check(IsPassword(refreshed.TemporaryPassword)&&refreshed.PasswordGeneration==3&&meB.GetProperty("passwordGeneration").GetInt64()==3&&!meB.GetProperty("rotationRequired").GetBoolean(),"rotate increments generation and clears required");

 // Connect by a revokes other inbound Pending/Approved; preserves just-connected and other Connected.
 clock.Advance(TimeSpan.FromSeconds(61));
 await Status("POST","/api/v1/sessions/"+other.SessionId+"/sharing",b.BearerToken,new{enabled=true},200,"other Connected sharing enabled");
 var pending=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,refreshed.TemporaryPassword));
 var approved=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,refreshed.TemporaryPassword));
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(approved.SessionId,true));
 var connected=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,refreshed.TemporaryPassword));
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(connected.SessionId,true));
 await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(connected.SessionId));
 await Status("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(pending.SessionId,true),400,"connect revokes other inbound Pending");
 await Status("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(approved.SessionId),400,"connect revokes other inbound Approved");
 await Status("POST","/api/v1/sessions/"+connected.SessionId+"/sharing",b.BearerToken,new{enabled=true},200,"connect preserves just-connected session");
 await Status("POST","/api/v1/sessions/"+other.SessionId+"/sharing",b.BearerToken,new{enabled=false},200,"connect preserves other Connected session");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,refreshed.TemporaryPassword),401,"refreshed password invalidated by next connect");
 Check((await Me(b.BearerToken)).GetProperty("passwordGeneration").GetInt64()==4,"second connect advances generation once");

 // Manual rotate (empty object and no body) keeps legacy unconditional behavior.
 clock.Advance(TimeSpan.FromSeconds(61));
 await Status("POST","/api/v1/devices/password/rotate","BAD",new{},401,"rotation requires device credential");
 PasswordResponse rotated;
 using(var r=await Send("POST","/api/v1/devices/password/rotate",b.BearerToken,new{})){
  r.EnsureSuccessStatusCode(); Check(r.Headers.CacheControl?.NoStore==true,"rotation no-store");rotated=(await r.Content.ReadFromJsonAsync<PasswordResponse>())!;
 }
 Check(IsPassword(rotated.TemporaryPassword)&&rotated.TemporaryPassword!=refreshed.TemporaryPassword&&rotated.PasswordGeneration==5,"rotation returns actual new password");
 var pending2=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,rotated.TemporaryPassword));
 var approved2=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,rotated.TemporaryPassword));
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(approved2.SessionId,true));
 PasswordResponse rotated2;
 using(var r=await Send("POST","/api/v1/devices/password/rotate",b.BearerToken)){
  r.EnsureSuccessStatusCode();rotated2=(await r.Content.ReadFromJsonAsync<PasswordResponse>())!;
 }
 Check(rotated2.PasswordGeneration==6,"no-body rotate is unconditional");
 await Status("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(pending2.SessionId,true),400,"rotation revokes inbound Pending");
 await Status("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(approved2.SessionId),400,"rotation revokes inbound Approved");
 await Status("POST","/api/v1/sessions/"+connected.SessionId+"/sharing",b.BearerToken,new{enabled=false},200,"rotation preserves Connected");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,rotated.TemporaryPassword),401,"old password denied after rotation");
 var fresh=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,rotated2.TemporaryPassword));
 Check(fresh.Status=="Pending","rotated password accepted");

 // Password TTL is exactly 1 hour, independent of the (3h fixture) session TTL.
 clock.Advance(TimeSpan.FromSeconds(61));
 var rc=await Post<PasswordResponse>("/api/v1/devices/password/rotate",c.BearerToken,new{});
 Check(rc.PasswordExpiresAt==clock.UtcNow.AddHours(1),"rotated password exact 1h expiry");
 clock.Advance(TimeSpan.FromSeconds(3599));
 var late=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(c.DeviceId,rc.TemporaryPassword));
 Check(late.Status=="Pending","password valid at 3599 seconds");
 clock.Advance(TimeSpan.FromSeconds(1));
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(c.DeviceId,rc.TemporaryPassword),401,"password expires at exactly 3600 seconds");
 await Status("POST","/api/v1/sessions/decision",c.BearerToken,new DecisionRequest(late.SessionId,true),200,"session created at 3599s outlives password expiry");
 clock.Advance(TimeSpan.FromSeconds(61));
 long beforeRace=(await Me(b.BearerToken)).GetProperty("passwordGeneration").GetInt64();
 var race=await Task.WhenAll(Send("POST","/api/v1/devices/password/rotate",b.BearerToken,new{expectedGeneration=beforeRace}),Send("POST","/api/v1/devices/password/rotate",b.BearerToken,new{expectedGeneration=beforeRace}));
 Check(race.Count(r=>(int)r.StatusCode==200)==1&&race.Count(r=>(int)r.StatusCode==409)==1,"concurrent conditional rotates exactly one commit");
 foreach(var r in race)r.Dispose();
 Check((await Me(b.BearerToken)).GetProperty("passwordGeneration").GetInt64()==beforeRace+1,"concurrent rotation advances generation once");
 Console.WriteLine("PASSWORD_SMOKE_PASS count="+count);
}finally{await app.StopAsync();}
