using System.Drawing;
using System.Drawing.Imaging;
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
using var rsa=RSA.Create(2048);
var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());
using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var clock=new TestClock(); const string invite="MEDIA_FIXTURE_PRIVATE_INVITE";
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions{InvitationCode=invite,EphemeralCertificate=cert,Clock=clock,Port=0});
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
 async Task<T> Post<T>(string path,string? token,object body){
   using var r=await Send("POST",path,token,body);r.EnsureSuccessStatusCode();
   return (await r.Content.ReadFromJsonAsync<T>())!;
 }
 async Task Status(string method,string path,string token,object? body,int status,string name){
   using var r=await Send(method,path,token,body);Check((int)r.StatusCode==status,name+" status="+(int)r.StatusCode);
 }
 var a=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var b=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var c=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var sr=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId, b.TemporaryPassword));
 string id=sr.SessionId,route="/api/v1/sessions/"+id;
 await Status("POST",route+"/sharing",b.BearerToken,new{enabled=true},409,"sharing denied before Connected");
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(id,true));
 await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(id));
 await Status("POST",route+"/sharing",a.BearerToken,new{enabled=true},403,"initiator cannot start target sharing");
 var start=await Post<JsonElement>(route+"/sharing",b.BearerToken,new{enabled=true});long gen=start.GetProperty("generation").GetInt64();
 Check(start.GetProperty("minFrameIntervalMs").GetInt32()==100&&start.GetProperty("inputLeasePollSupported").GetBoolean(),"sharing response advertises additive 100ms interval and input-lease");
 await Status("GET",route+"/frame",a.BearerToken,null,204,"no frame initially");
 byte[] jpeg;using(var bmp=new Bitmap(8,8)){using(var g=Graphics.FromImage(bmp))g.Clear(Color.Blue);using var ms=new MemoryStream();bmp.Save(ms,ImageFormat.Jpeg);jpeg=ms.ToArray();}
 async Task Put(string token,long generation,long seq,byte[] bytes,int expected,string name,bool chunked=false,int width=8){
   using var req=new HttpRequestMessage(HttpMethod.Put,url+route+"/frame");req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
   req.Headers.Add("X-Share-Generation",generation.ToString());req.Headers.Add("X-Frame-Sequence",seq.ToString());req.Headers.Add("X-Frame-Width",width.ToString());req.Headers.Add("X-Frame-Height","8");
   req.Content=chunked?new ChunkedBytes(bytes):new ByteArrayContent(bytes);req.Content.Headers.ContentType=new MediaTypeHeaderValue("image/jpeg");
   using var r=await http.SendAsync(req);Check((int)r.StatusCode==expected,name+" status="+(int)r.StatusCode);
 }
 await Put(a.BearerToken,gen,1,jpeg,403,"initiator cannot publish");
 await Put(b.BearerToken,gen,1,jpeg,204,"target publishes actual JPEG");
 using(var r=await Send("GET",route+"/frame",a.BearerToken)){Check(r.IsSuccessStatusCode&&r.Headers.GetValues("X-Share-Generation").Single()==gen.ToString()&&(await r.Content.ReadAsByteArrayAsync()).SequenceEqual(jpeg),"latest frame exact bytes and generation");}
 await Status("GET",route+"/frame",b.BearerToken,null,403,"target cannot consume requester stream");
 await Put(b.BearerToken,gen,2,jpeg,429,"10fps cap");
 clock.Advance(TimeSpan.FromMilliseconds(99));
 await Put(b.BearerToken,gen,2,jpeg,429,"99ms interval denied");
 clock.Advance(TimeSpan.FromMilliseconds(1));
 await Put(b.BearerToken,gen,2,jpeg,204,"exact100ms interval accepted");
 clock.Advance(TimeSpan.FromMilliseconds(201));
 await Put(b.BearerToken,gen,2,jpeg,409,"frame replay denied");
 await Put(b.BearerToken,gen,2,new byte[]{255,216,255,217},400,"missing SOF rejected");
 await Put(b.BearerToken,gen,2,jpeg,400,"JPEG dimension mismatch",width:9);
 await Put(b.BearerToken,gen,2,new byte[524289],413,"known length frame cap");
 await Put(b.BearerToken,gen,2,new byte[524289],413,"chunked frame cap",chunked:true);
 clock.Advance(TimeSpan.FromSeconds(1));
 object Ev(long seq,string kind="text")=>new{generation=gen,sequence=seq,kind,x=0,y=0,button=0,keyCode=65,text="q"};
 await Status("POST",route+"/input",b.BearerToken,Ev(1),403,"target cannot enqueue remote input");
 await Status("POST",route+"/input",a.BearerToken,Ev(1,"bad"),400,"unknown input rejected");
 await Status("POST",route+"/input",a.BearerToken,new{generation=gen,sequence=1,kind="text",text="\u007f"},400,"nonprintable text rejected");
 for(int i=1;i<=32;i++){using var r=await Send("POST",route+"/input",a.BearerToken,Ev(i));if((int)r.StatusCode!=204)throw new Exception("queue fill "+i);}
 await Status("POST",route+"/input",a.BearerToken,Ev(33),429,"input queue bounded32");
 await Status("POST",route+"/input",a.BearerToken,Ev(32),409,"input replay denied");
 await Status("GET",route+"/input",a.BearerToken,null,403,"initiator cannot drain host queue");
 using(var r=await Send("GET",route+"/input",b.BearerToken)){var j=(await r.Content.ReadFromJsonAsync<JsonElement>());Check(j.GetProperty("events").GetArrayLength()==32,"target drains exactly32");}
 await Status("GET",route+"/lease",c.BearerToken,null,403,"stranger lease denied");
 await Status("GET",route+"/input-lease",a.BearerToken,null,403,"initiator input-lease denied");
 await Status("GET",route+"/input-lease",c.BearerToken,null,403,"outsider input-lease denied");
 await Status("GET",route+"/input-lease","invalid-token",null,401,"wrong token input-lease denied");
 await Status("POST",route+"/input",a.BearerToken,Ev(33),204,"input queued for input-lease");
 using(var r=await Send("GET",route+"/input-lease",b.BearerToken)){var j=await r.Content.ReadFromJsonAsync<JsonElement>();Check(r.StatusCode==HttpStatusCode.OK&&r.Headers.CacheControl?.NoStore==true&&j.GetProperty("generation").GetInt64()==gen&&j.GetProperty("leaseMs").GetInt32()==100&&j.GetProperty("events").GetArrayLength()==1,"input-lease returns generation lease100 and drained event");}
 using(var r=await Send("GET",route+"/input-lease",b.BearerToken)){var j=await r.Content.ReadFromJsonAsync<JsonElement>();Check(j.GetProperty("events").GetArrayLength()==0,"input-lease drain is consumed once");}
 var stop=await Post<JsonElement>(route+"/sharing",b.BearerToken,new{enabled=false});
 var stopAgain=await Post<JsonElement>(route+"/sharing",b.BearerToken,new{enabled=false});
 Check(stopAgain.GetProperty("generation").GetInt64()>stop.GetProperty("generation").GetInt64(),"every explicit stop increments generation");
 await Status("GET",route+"/lease",b.BearerToken,null,409,"stop removes authority");
 await Status("GET",route+"/input-lease",b.BearerToken,null,409,"inactive input-lease denied");
 var restart=await Post<JsonElement>(route+"/sharing",b.BearerToken,new{enabled=true});
 long newGen=restart.GetProperty("generation").GetInt64();Check(newGen>gen,"restart advances generation");
 await Put(b.BearerToken,gen,3,jpeg,409,"old generation denied");
 await Status("GET",route+"/frame",a.BearerToken,null,204,"restart clears latest frame");
 using(var r=await Send("GET",route+"/input",b.BearerToken)){var j=await r.Content.ReadFromJsonAsync<JsonElement>();Check(j.GetProperty("events").GetArrayLength()==0,"restart clears inputqueue");}
 await Post<SessionResponse>("/api/v1/sessions/revoke",a.BearerToken,new RevokeRequest(id));
 await Status("GET",route+"/lease",b.BearerToken,null,409,"revoked lease denied");
 await Status("GET",route+"/input-lease",b.BearerToken,null,409,"revoked input-lease denied");
 await Status("GET",route+"/frame",a.BearerToken,null,409,"revoked frame denied");
 var fresh=await Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{});
 var sr2=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId, fresh.TemporaryPassword));
 await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(sr2.SessionId,true));
 await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(sr2.SessionId));
 await Post<JsonElement>("/api/v1/sessions/"+sr2.SessionId+"/sharing",b.BearerToken,new{enabled=true});
 clock.Advance(TimeSpan.FromSeconds(61));
 await Status("GET","/api/v1/sessions/"+sr2.SessionId+"/lease",b.BearerToken,null,409,"expired lease denied");

 async Task OpenFresh(){
  var pw=await Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{});
  var session=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,pw.TemporaryPassword));
  id=session.SessionId;route="/api/v1/sessions/"+id;
  await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(id,true));
  await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(id));
  gen=(await Post<JsonElement>(route+"/sharing",b.BearerToken,new{enabled=true})).GetProperty("generation").GetInt64();
 }
 await OpenFresh();
 clock.Advance(TimeSpan.FromSeconds(50));
 await Put(b.BearerToken,gen,1,jpeg,204,"idle test fresh frame upload");
 await Status("GET",route+"/frame",a.BearerToken,null,200,"fresh frame consumption renews idle");
 clock.Advance(TimeSpan.FromSeconds(20));
 await Status("GET",route+"/frame",a.BearerToken,null,200,"connected survives original absolute TTL");
 await Status("GET","/api/v1/devices/me",b.BearerToken,null,200,"password metadata polling during connected");
 await Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{});
 await Status("GET",route+"/input",b.BearerToken,null,200,"empty input poll does not renew idle");
 await Status("GET",route+"/input-lease",b.BearerToken,null,200,"empty input-lease poll does not renew idle");
 clock.Advance(TimeSpan.FromSeconds(39));
 await Status("GET",route+"/lease",b.BearerToken,null,200,"idle still valid at59 seconds");
 clock.Advance(TimeSpan.FromSeconds(1));
 await Status("GET",route+"/frame",a.BearerToken,null,409,"same frame me password rotate empty input lease cannot prevent exact idle expiry");
 await Put(b.BearerToken,gen,2,jpeg,409,"terminal idle session cannot revive by fresh upload");

 await OpenFresh();
 clock.Advance(TimeSpan.FromSeconds(50));
 await Status("POST",route+"/input",a.BearerToken,Ev(1),204,"valid input queued for idle test");
 await Status("GET",route+"/input",b.BearerToken,null,200,"target consumes actual input and renews idle");
 clock.Advance(TimeSpan.FromSeconds(20));
 await Status("GET",route+"/lease",b.BearerToken,null,200,"input consumption survives original absolute TTL");
 await Status("GET",route+"/input",a.BearerToken,null,403,"wrong role cannot renew idle");
 clock.Advance(TimeSpan.FromSeconds(40));
 await Status("GET",route+"/lease",b.BearerToken,null,409,"consumed input still has bounded idle expiry");

 await OpenFresh();
 clock.Advance(TimeSpan.FromSeconds(50));
 await Status("POST",route+"/input",a.BearerToken,Ev(1),204,"valid input queued for input-lease idle test");
 await Status("GET",route+"/input-lease",b.BearerToken,null,200,"input-lease consumes actual input and renews idle");
 clock.Advance(TimeSpan.FromSeconds(20));
 await Status("GET",route+"/lease",b.BearerToken,null,200,"input-lease consumption survives original absolute TTL");
 await Status("GET",route+"/input-lease",a.BearerToken,null,403,"wrong role input-lease cannot renew idle");
 clock.Advance(TimeSpan.FromSeconds(40));
 await Status("GET",route+"/lease",b.BearerToken,null,409,"input-lease consumed input still has bounded idle expiry");

 await OpenFresh();
 clock.Advance(TimeSpan.FromSeconds(50));
 await Put(b.BearerToken,gen,1,jpeg,204,"unconsumed upload succeeds before idle expiry");
 clock.Advance(TimeSpan.FromSeconds(10));
 await Status("GET",route+"/frame",a.BearerToken,null,409,"target upload alone cannot keep abandoned viewer alive");
 await OpenFresh();clock.Advance(TimeSpan.FromSeconds(1));
 for(int i=0;i<60;i++){using var response=await Send("GET",route+"/input-lease",b.BearerToken);if((int)response.StatusCode!=200)throw new Exception("input-lease rate before60 "+i);}
 Check(true,"input-lease permits exact60 per authenticated device window");
 await Status("GET",route+"/input-lease",b.BearerToken,null,429,"input-lease61 obeys shared media rate");
 Console.WriteLine("MEDIA_SMOKE_PASS count="+count);
}finally{await app.StopAsync();}

sealed class ChunkedBytes(byte[] bytes):HttpContent{
 protected override bool TryComputeLength(out long length){length=0;return false;}
 protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes,0,bytes.Length);
}
