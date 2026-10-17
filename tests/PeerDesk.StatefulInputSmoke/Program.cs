using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;

// Real pinned-HTTPS ephemeral server smoke for the stateful input wire (statefulInputVersion 1).
int count=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
using var rsa=RSA.Create(2048);
var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());
using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var clock=new TestClock(); const string invite="STATEFUL_FIXTURE_PRIVATE_INVITE";
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions{InvitationCode=invite,EphemeralCertificate=cert,Clock=clock,Port=0});
await app.StartAsync();
try{
 string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 using var http=PinnedHttpClientFactory.CreateClient(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)));
 async Task<HttpResponseMessage> Send(string method,string path,string? token=null,object? body=null,string? rawJson=null){
   using var req=new HttpRequestMessage(new HttpMethod(method),url+path);
   if(token!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
   if(rawJson!=null)req.Content=new StringContent(rawJson,Encoding.UTF8,"application/json");
   else if(body!=null)req.Content=JsonContent.Create(body);
   return await http.SendAsync(req);
 }
 async Task<T> Post<T>(string path,string? token,object body){
   using var r=await Send("POST",path,token,body);r.EnsureSuccessStatusCode();
   return (await r.Content.ReadFromJsonAsync<T>())!;
 }
 async Task Status(string method,string path,string token,object? body,int status,string name){
   using var r=await Send(method,path,token,body);Check((int)r.StatusCode==status,name+" status="+(int)r.StatusCode);
 }
 async Task RawStatus(string method,string path,string token,string json,int status,string name){
   using var r=await Send(method,path,token,null,json);Check((int)r.StatusCode==status,name+" status="+(int)r.StatusCode);
 }
 void Tick()=>clock.Advance(TimeSpan.FromMilliseconds(1001)); // fresh shared 60/s media window; quota itself unchanged
 var a=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var b=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 var c=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
 string id="",route="";long gen=0,seq=0;
 async Task<JsonElement> Share(object body){
   var j=await Post<JsonElement>(route+"/sharing",b.BearerToken,body);gen=j.GetProperty("generation").GetInt64();seq=0;return j;
 }
 async Task<JsonElement> OpenSession(object sharingBody){
   if(id.Length>0){using var _=await Send("POST","/api/v1/sessions/revoke",a.BearerToken,new RevokeRequest(id));} // previous may already be idle-expired
   var pw=await Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{});
   var s=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,pw.TemporaryPassword));
   id=s.SessionId;route="/api/v1/sessions/"+id;
   await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(id,true));
   await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(id));
   return await Share(sharingBody);
 }
 object Ev(string kind,int x=0,int y=0,int button=0,int keyCode=0,string? text=null,int wheelDelta=0,long? sequence=null,long? generation=null)
   =>new{generation=generation??gen,sequence=sequence??++seq,kind,x,y,button,keyCode,text,wheelDelta};
 async Task Input(object ev,int status,string name)=>await Status("POST",route+"/input",a.BearerToken,ev,status,name);
 async Task<JsonElement> DrainLease(){
   using var r=await Send("GET",route+"/input-lease",b.BearerToken);Check(r.StatusCode==HttpStatusCode.OK,"input-lease drain ok");
   return await r.Content.ReadFromJsonAsync<JsonElement>();
 }
 byte[] jpeg;using(var bmp=new Bitmap(8,8)){using(var g=Graphics.FromImage(bmp))g.Clear(Color.Green);using var ms=new MemoryStream();bmp.Save(ms,ImageFormat.Jpeg);jpeg=ms.ToArray();}
 async Task PutFrame(long frameSeq){
   using var req=new HttpRequestMessage(HttpMethod.Put,url+route+"/frame");req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",b.BearerToken);
   req.Headers.Add("X-Share-Generation",gen.ToString());req.Headers.Add("X-Frame-Sequence",frameSeq.ToString());req.Headers.Add("X-Frame-Width","8");req.Headers.Add("X-Frame-Height","8");
   req.Content=new ByteArrayContent(jpeg);req.Content.Headers.ContentType=new MediaTypeHeaderValue("image/jpeg");
   using var r=await http.SendAsync(req);Check((int)r.StatusCode==204,"frame published seq="+frameSeq);
 }
 async Task<(string? version,long generation)> GetFrameCapability(){
   using var r=await Send("GET",route+"/frame",a.BearerToken);Check(r.StatusCode==HttpStatusCode.OK,"frame fetched");
   string? v=r.Headers.TryGetValues("X-Stateful-Input-Version",out var vs)?vs.Single():null;
   return (v,long.Parse(r.Headers.GetValues("X-Share-Generation").Single()));
 }

 // 1. Minimal opt-in + down/up roundtrip.
 var opted=await OpenSession(new{enabled=true,statefulInputVersion=1});
 Check(opted.GetProperty("statefulInputVersion").GetInt32()==1&&opted.GetProperty("inputPollIntervalMs").GetInt32()==50&&opted.GetProperty("minFrameIntervalMs").GetInt32()==100,"opted sharing advertises version1 poll50 frame100");
 await PutFrame(1);
 var cap=await GetFrameCapability();
 Check(cap.version=="1"&&cap.generation==gen,"frame header carries stateful capability with same generation");
 await Input(Ev("mouseDown",x:100,y:200,button:0),204,"opted mouseDown accepted");
 await Input(Ev("mouseUp",x:110,y:210,button:0),204,"opted mouseUp accepted");
 var lease=await DrainLease();var evs=lease.GetProperty("events");
 Check(lease.GetProperty("leaseMs").GetInt32()==100,"stateful actuation lease stays 100ms");
 Check(evs.GetArrayLength()==2&&evs[0].GetProperty("kind").GetString()=="mouseDown"&&evs[0].GetProperty("x").GetInt32()==100&&evs[0].GetProperty("y").GetInt32()==200
   &&evs[1].GetProperty("kind").GetString()=="mouseUp"&&evs[0].GetProperty("generation").GetInt64()==gen&&evs[1].GetProperty("sequence").GetInt64()>evs[0].GetProperty("sequence").GetInt64(),"down/up roundtrip ordered with gen and sequence");
 Tick();
 await Input(Ev("keyDown",keyCode:16),204,"opted keyDown accepted");
 await Input(Ev("keyUp",keyCode:16),204,"opted keyUp accepted");
 await Input(Ev("wheel",x:5,y:5,wheelDelta:-120),204,"opted signed wheel accepted");
 await Input(Ev("holdHeartbeat"),204,"opted holdHeartbeat accepted");
 await Input(Ev("click",x:1,y:1,button:1),204,"opted session still accepts legacy click");
 evs=(await DrainLease()).GetProperty("events");
 Check(evs.GetArrayLength()==5&&evs[2].GetProperty("wheelDelta").GetInt32()==-120&&evs[0].GetProperty("keyCode").GetInt32()==16,"wheelDelta and keyCode round trip");

 // 2. Role, generation, replay.
 Tick();
 await Status("POST",route+"/input",b.BearerToken,Ev("mouseDown",x:1,y:1),403,"target cannot post stateful input");
 await Status("POST",route+"/input",c.BearerToken,Ev("mouseDown",x:1,y:1),403,"outsider cannot post stateful input");
 await Status("POST",route+"/input","invalid-token",Ev("mouseDown",x:1,y:1),401,"bad token stateful input denied");
 await Input(Ev("mouseDown",x:1,y:1,generation:gen-1),409,"stale generation mouseDown denied");
 long used=seq;
 await Input(Ev("keyDown",keyCode:65),204,"keyDown accepted before replay");
 await Input(Ev("keyDown",keyCode:65,sequence:used+1),409,"replayed sequence denied");
 await Input(Ev("keyUp",keyCode:65,sequence:used),409,"lower sequence denied");
 await Input(Ev("keyUp",keyCode:65,sequence:0),400,"zero sequence denied");

 // 3. Signed wheel bounds and unused fields.
 Tick();
 await Input(Ev("wheel",x:1,y:1,wheelDelta:1200),204,"wheel +1200 accepted");
 await Input(Ev("wheel",x:1,y:1,wheelDelta:-1200),204,"wheel -1200 accepted");
 await Input(Ev("wheel",x:1,y:1,wheelDelta:0),400,"zero wheel rejected");
 await Input(Ev("wheel",x:1,y:1,wheelDelta:1201),400,"wheel 1201 rejected");
 await Input(Ev("wheel",x:1,y:1,wheelDelta:-1201),400,"wheel -1201 rejected");
 await Input(Ev("wheel",x:1,y:1,wheelDelta:120,button:1),400,"wheel with button rejected");
 await Input(Ev("move",x:1,y:1,wheelDelta:120),400,"wheelDelta on move rejected");
 await Input(Ev("mouseDown",x:1,y:1,button:3),400,"mouseDown button3 rejected");
 await Input(Ev("mouseDown",x:-1,y:1),400,"mouseDown negative x rejected");
 await Input(Ev("mouseDown",x:1,y:1,keyCode:65),400,"mouseDown with keyCode rejected");
 await Input(Ev("keyDown",keyCode:0),400,"keyDown keyCode0 rejected");
 await Input(Ev("keyDown",keyCode:255),400,"keyDown keyCode255 rejected");
 await Input(Ev("keyDown",keyCode:65,x:3),400,"keyDown with coordinates rejected");
 await Input(Ev("keyUp",keyCode:65,text:"a"),400,"keyUp with text rejected");
 await Input(Ev("releaseAll",button:1),400,"releaseAll with button rejected");
 await Input(Ev("holdHeartbeat",keyCode:1),400,"holdHeartbeat with keyCode rejected");
 await Input(Ev("MouseDown",x:1,y:1),400,"kind is case sensitive");
 Tick();
 long s1=++seq;
 await RawStatus("POST",route+"/input",a.BearerToken,"{\"generation\":"+gen+",\"sequence\":"+s1+",\"kind\":\"wheel\",\"x\":1,\"y\":1,\"wheelDelta\":1.5}",400,"fractional wheelDelta rejected");
 await RawStatus("POST",route+"/input",a.BearerToken,"{\"generation\":"+gen+",\"sequence\":"+s1+",\"kind\":\"wheel\",\"x\":1,\"y\":1,\"wheelDelta\":\"120\"}",400,"string wheelDelta rejected");
 await RawStatus("POST",route+"/input",a.BearerToken,"{\"generation\":"+gen+",\"sequence\":\""+s1+"\",\"kind\":\"keyUp\",\"keyCode\":65}",400,"string sequence rejected");
 await RawStatus("POST",route+"/input",a.BearerToken,"{\"generation\":"+gen+",\"sequence\":"+s1+",\"kind\":\"keyUp\",\"kind\":\"mouseDown\",\"keyCode\":65}",400,"duplicate kind rejected");
 await RawStatus("POST",route+"/input",a.BearerToken,"{\"generation\":"+gen+",\"sequence\":"+s1+",\"kind\":\"wheel\",\"x\":1,\"y\":1,\"wheelDelta\":120}",204,"raw strict wheel accepted after rejections without sequence burn");
 await DrainLease();

 // 4. Whole queue bound and releaseAll barrier.
 Tick();
 for(int i=0;i<32;i++){using var r=await Send("POST",route+"/input",a.BearerToken,Ev("keyDown",keyCode:65));if((int)r.StatusCode!=204)throw new Exception("stateful queue fill "+i+" status="+(int)r.StatusCode);}
 Check(true,"stateful queue fills exactly32");
 await Input(Ev("keyUp",keyCode:65),429,"stateful queue bounded32");
 await Input(Ev("holdHeartbeat"),429,"heartbeat does not bypass queue bound");
 await Input(Ev("releaseAll"),204,"releaseAll accepted as barrier on full queue");
 Tick();
 await Input(Ev("releaseAll",sequence:seq),409,"releaseAll replay denied");
 await Input(Ev("releaseAll",generation:gen+1),409,"releaseAll wrong generation denied");
 evs=(await DrainLease()).GetProperty("events");
 Check(evs.GetArrayLength()==1&&evs[0].GetProperty("kind").GetString()=="releaseAll","releaseAll cleared all prior queued downs");
 await Input(Ev("mouseDown",x:2,y:2),204,"input accepted after barrier");
 await Input(Ev("releaseAll"),204,"releaseAll after pending down");
 evs=(await DrainLease()).GetProperty("events");
 Check(evs.GetArrayLength()==1&&evs[0].GetProperty("kind").GetString()=="releaseAll","pending down cannot survive later releaseAll");

 // 5. Malformed sharing capability bodies (target role) never change state.
 Tick();
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":\"1\"}",400,"string version rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":2}",400,"version2 rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":1.0}",400,"fractional-form version rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":null}",400,"null version rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":-1}",400,"negative version rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":1,\"statefulInputVersion\":0}",400,"duplicate version rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"enabled\":false}",400,"duplicate enabled rejected");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":\"true\"}",400,"string enabled rejected");
 await Status("POST",route+"/sharing",a.BearerToken,new{enabled=true,statefulInputVersion=1},403,"initiator cannot opt target in");
 await Input(Ev("mouseDown",x:3,y:3),204,"malformed sharing bodies left opted generation intact");
 await DrainLease();

 // 6. Legacy sharing on same session: capability is per generation and not sticky.
 Tick();
 var legacy=await Share(new{enabled=true});
 Check(!legacy.TryGetProperty("statefulInputVersion",out _)&&!legacy.TryGetProperty("inputPollIntervalMs",out _)&&legacy.GetProperty("minFrameIntervalMs").GetInt32()==100,"legacy restart response unchanged");
 await PutFrame(1);
 cap=await GetFrameCapability();
 Check(cap.version==null&&cap.generation==gen,"legacy frame has no stateful header");
 foreach(var k in new[]{"mouseDown","mouseUp"})await Input(Ev(k,x:1,y:1),400,"legacy target rejects "+k);
 foreach(var k in new[]{"keyDown","keyUp"})await Input(Ev(k,keyCode:65),400,"legacy target rejects "+k);
 await Input(Ev("wheel",x:1,y:1,wheelDelta:120),400,"legacy target rejects wheel");
 await Input(Ev("releaseAll"),400,"legacy target rejects releaseAll");
 await Input(Ev("holdHeartbeat"),400,"legacy target rejects holdHeartbeat");
 await Input(Ev("click",x:4,y:4,button:0),204,"legacy tap click accepted");
 await Input(Ev("key",keyCode:65),204,"legacy key accepted");
 await Input(Ev("move",x:5,y:5),204,"legacy move accepted");
 await Input(Ev("text",text:"q"),204,"legacy text accepted");
 evs=(await DrainLease()).GetProperty("events");
 Check(evs.GetArrayLength()==4&&evs[0].GetProperty("kind").GetString()=="click","legacy events delivered unchanged");
 Tick();
 var zero=await Share(new{enabled=true,statefulInputVersion=0});
 Check(!zero.TryGetProperty("statefulInputVersion",out _),"explicit version0 is legacy");
 await Input(Ev("mouseDown",x:1,y:1),400,"explicit version0 rejects mouseDown");
 var off=await Share(new{enabled=false,statefulInputVersion=1});
 Check(!off.TryGetProperty("statefulInputVersion",out _),"stop never advertises stateful");
 await Status("POST",route+"/input",a.BearerToken,Ev("mouseDown",x:1,y:1),409,"stopped sharing rejects input");

 // Optimized media is an explicit target opt-in; legacy stays at100ms, quotas/auth unchanged.
 Tick();await OpenSession(new{enabled=true,statefulInputVersion=1});
 using(var r=await Send("POST",route+"/sharing",b.BearerToken,new{enabled=true,mediaOptimizationVersion=1})){Check((int)r.StatusCode==400,"fast mode requires stateful opt-in");}
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":1,\"mediaOptimizationVersion\":\"1\"}",400,"fast mode rejects string version");
 await RawStatus("POST",route+"/sharing",b.BearerToken,"{\"enabled\":true,\"statefulInputVersion\":1,\"mediaOptimizationVersion\":2}",400,"fast mode rejects future version");
 var fast=await Share(new{enabled=true,statefulInputVersion=1,mediaOptimizationVersion=1});
 Check(fast.GetProperty("minFrameIntervalMs").GetInt32()==50&&fast.GetProperty("mediaOptimizationVersion").GetInt32()==1,"explicit optimized50ms negotiated");
 await PutFrame(1);
 using(var r=await Send("GET",route+"/frame",a.BearerToken)){Check(r.Headers.GetValues("X-Media-Optimization-Version").Single()=="1","optimized frame announces exact version");}
 clock.Advance(TimeSpan.FromMilliseconds(50));await PutFrame(2);
 await Share(new{enabled=true,statefulInputVersion=1});
 await PutFrame(1);
 using(var r=await Send("GET",route+"/frame",a.BearerToken)){Check(!r.Headers.Contains("X-Media-Optimization-Version"),"legacy sharing has no fast capability");}
 Tick();

 // 7. Heartbeat/releaseAll drains and empty polls do not extend idle TTL.
 await OpenSession(new{enabled=true,statefulInputVersion=1});
 clock.Advance(TimeSpan.FromSeconds(50));
 await Input(Ev("holdHeartbeat"),204,"heartbeat queued for idle test");
 await Input(Ev("releaseAll"),204,"releaseAll queued for idle test");
 evs=(await DrainLease()).GetProperty("events");
 Check(evs.GetArrayLength()==1&&evs[0].GetProperty("kind").GetString()=="releaseAll","idle test drained control events");
 await Input(Ev("holdHeartbeat"),204,"second heartbeat queued");
 using(var r=await Send("GET",route+"/input",b.BearerToken)){Check(r.StatusCode==HttpStatusCode.OK,"legacy drain of heartbeat ok");}
 await Status("GET",route+"/input-lease",b.BearerToken,null,200,"empty poll ok");
 clock.Advance(TimeSpan.FromSeconds(10));
 await Status("GET",route+"/lease",b.BearerToken,null,409,"heartbeat releaseAll and empty drains cannot prevent idle expiry");

 await OpenSession(new{enabled=true,statefulInputVersion=1});
 clock.Advance(TimeSpan.FromSeconds(50));
 await Input(Ev("holdHeartbeat"),204,"heartbeat with real input");
 await Input(Ev("keyDown",keyCode:65),204,"real input mixed with heartbeat");
 evs=(await DrainLease()).GetProperty("events");Check(evs.GetArrayLength()==2,"mixed batch drained");
 clock.Advance(TimeSpan.FromSeconds(20));
 await Status("GET",route+"/lease",b.BearerToken,null,200,"real stateful input still renews idle");

 // 8. Authenticated termination reasons: normal user stop is not a generic media conflict.
 Tick();await OpenSession(new{enabled=true,statefulInputVersion=1});
 await Status("POST",route+"/sharing",b.BearerToken,new{enabled=false,stopReason="invalid"},400,"unknown stop reason rejected");
 await Status("POST",route+"/sharing",b.BearerToken,new{enabled=true,stopReason="user"},400,"stop reason on start rejected");
 async Task EndHeader(string suffix,string token,int status,string? expected,string name){
   using var r=await Send("GET",route+suffix,token);Check((int)r.StatusCode==status,name+" status");
   var value=r.Headers.TryGetValues("X-PeerDesk-Media-End",out var vals)?vals.Single():null;
   Check(value==expected,name+" exact authenticated end reason");
 }
 await Share(new{enabled=false,stopReason="user"});
 await EndHeader("/frame",a.BearerToken,409,"user-stopped","normal share stop viewer");
 await EndHeader("/input-lease",b.BearerToken,409,"user-stopped","normal share stop target");
 await EndHeader("/frame",c.BearerToken,403,null,"outsider receives no reason");
 await Share(new{enabled=true,statefulInputVersion=1});
 await EndHeader("/frame",a.BearerToken,204,null,"new active empty share no end reason");
 await Share(new{enabled=false});
 await EndHeader("/frame",a.BearerToken,409,"error-stopped","unspecified stop never called normal");
 await Share(new{enabled=true,statefulInputVersion=1});
 await Status("POST","/api/v1/sessions/revoke",a.BearerToken,new RevokeRequest(id),200,"explicit session end");
 await EndHeader("/frame",a.BearerToken,409,"session-ended","revoke viewer");
 await EndHeader("/input-lease",b.BearerToken,409,"session-ended","revoke target");
 Tick();await OpenSession(new{enabled=true,statefulInputVersion=1});clock.Advance(TimeSpan.FromSeconds(61));
 await EndHeader("/frame",a.BearerToken,409,"expired","expiry never called normal");

 Console.WriteLine("STATEFUL_INPUT_SMOKE_PASS count="+count);
}finally{await app.StopAsync();}
