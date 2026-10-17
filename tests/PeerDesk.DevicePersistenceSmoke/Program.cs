using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;

int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);checks++;}
void Reject(Action action,string name){bool rejected=false;try{action();}catch(Exception ex) when(ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){rejected=true;}Check(rejected,name);}
string dir=Path.Combine(Path.GetTempPath(),"RemoteBridge-device-test-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
if(!OperatingSystem.IsWindows())File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
string state=Path.Combine(dir,"devices.json");
var store=new DeviceRegistryStateStore(state,100);
Reject(()=>store.LoadRequired(),"missing store rejected");
DeviceRegistryStateStore.Initialize(state,100);
Check(store.LoadRequired().Count==0,"explicit empty initialization");
Reject(()=>DeviceRegistryStateStore.Initialize(state,100),"initializer refuses overwrite");
Reject(()=>ServerOptions.FromEnvironment(k=>k==ServerOptions.DeviceStatePathVariable?"":null),"empty env rejected");
Reject(()=>new ServerOptions().RequireDeviceStatePath(),"production missing path rejected");
using var rsa=RSA.Create(2048);
var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
using var raw=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
const string invite="synthetic-persistence-invitation";
string ownerKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var ownerOptions=new OwnerFeatureOptions{Version=1,OwnerSecret=ownerKey,StatePath=Path.Combine(dir,"owner.json"),RegistryIdentity="persistence-test",
PublicProfileJson=JsonSerializer.Serialize(new ServerProfile{Version=1,Name="Persistence test",RendezvousUrl="https://localhost:443",RelayUrl="https://localhost:443",CertificateSha256=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256))})};
WebApplication? app=null; string url="";
using var http=PinnedHttpClientFactory.CreateClient(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)));
async Task Start(){app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions{DeviceStatePath=state,OwnerFeature=ownerOptions,InvitationCode=invite,EphemeralCertificate=cert,SessionTtl=TimeSpan.FromHours(3)});await app.StartAsync();url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();}
async Task Stop(){if(app!=null){await app.StopAsync();await app.DisposeAsync();app=null;}}
async Task<HttpResponseMessage> Send(string method,string path,string? token=null,object? body=null){using var q=new HttpRequestMessage(new HttpMethod(method),url+path);if(token!=null)q.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);if(body!=null)q.Content=JsonContent.Create(body);return await http.SendAsync(q);}
async Task<HttpResponseMessage> Owner(string path,object body){using var q=new HttpRequestMessage(HttpMethod.Post,url+path);q.Headers.Add(OwnerFeature.OwnerHeader,ownerKey);q.Content=JsonContent.Create(body);return await http.SendAsync(q);}
async Task<T> Post<T>(string path,string? token,object body){using var r=await Send("POST",path,token,body);r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<T>())!;}
async Task<JsonElement> Me(string token){using var r=await Send("GET","/api/v1/devices/me",token);r.EnsureSuccessStatusCode();return await r.Content.ReadFromJsonAsync<JsonElement>();}
async Task Status(string method,string path,string? token,object? body,int code,string name){using var r=await Send(method,path,token,body);Check((int)r.StatusCode==code,name+" HTTP="+(int)r.StatusCode);}
try{
await Start();
using(var setup=await Owner("/api/v1/owner/settings",new{displayName="Test",joinPassword=invite})){setup.EnsureSuccessStatusCode();}
var a=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
var b=await Post<RegisterResponse>("/api/v1/register",null,new RegisterRequest(invite));
Check(store.LoadRequired().Count==2,"registration durable before success");
string disk=File.ReadAllText(state);
Check(!disk.Contains(a.BearerToken)&&!disk.Contains(b.BearerToken)&&!disk.Contains(a.TemporaryPassword)&&!disk.Contains(b.TemporaryPassword)&&!disk.Contains("Salt")&&!disk.Contains("PasswordHash"),"no raw bearer password or password verifier on disk");
await Status("GET","/api/v1/devices/me",new string('X',300),null,401,"oversize bearer denied");
await Status("GET","/api/v1/devices/me",new string('A',44),null,401,"non-32-byte bearer denied");
var pending=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword));
await Stop();await Start();
var ma=await Me(a.BearerToken);var mb=await Me(b.BearerToken);
Check(ma.GetProperty("deviceId").GetString()==a.DeviceId&&mb.GetProperty("deviceId").GetString()==b.DeviceId,"restart preserves both IDs and bearer authentication");
Check(mb.GetProperty("rotationRequired").GetBoolean()&&mb.GetProperty("passwordGeneration").GetInt64()>b.PasswordGeneration,"restart invalidates password and advances generation");
using(var r=await Send("GET","/api/v1/sessions/snapshot",a.BearerToken)){r.EnsureSuccessStatusCode();var j=await r.Content.ReadFromJsonAsync<JsonElement>();Check(j.GetProperty("sessions").GetArrayLength()==0,"sessions not restored");}
await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword),401,"pre-restart password rejected");
var pw=await Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{});
var s=await Post<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,pw.TemporaryPassword));
Check(s.TargetDeviceId==b.DeviceId,"new rotated password accepts original target ID");
await Post<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(s.SessionId,true));
var before=await Me(b.BearerToken);byte[] original=File.ReadAllBytes(state);
string held=state+".held";File.Move(state,held);Directory.CreateDirectory(state);
try{
await Status("POST","/api/v1/register",null,new RegisterRequest(invite),503,"register write failure");
await Status("POST","/api/v1/devices/password/rotate",b.BearerToken,new{},503,"rotate write failure");
await Status("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(s.SessionId),503,"connect invalidation write failure");
using(var failedRevoke=await Owner("/api/v1/owner/devices/revoke",new{deviceId=b.DeviceId})) Check((int)failedRevoke.StatusCode==503,"revoke write failure HTTP503");
var after=await Me(b.BearerToken);Check(after.GetProperty("passwordGeneration").GetInt64()==before.GetProperty("passwordGeneration").GetInt64()&&!after.GetProperty("rotationRequired").GetBoolean(),"failed mutations preserve password state");
}finally{Directory.Move(state,state+".blocked");File.Move(held,state);}
Check(File.ReadAllBytes(state).SequenceEqual(original),"failed commits preserve original file");
var connected=await Post<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(s.SessionId));
Check(connected.Status==SessionStatus.Connected,"connect succeeds after writable state restored");
await Stop();await Start();
Check((await Me(a.BearerToken)).GetProperty("deviceId").GetString()==a.DeviceId,"third server instance retains ID");
var beforeBoth=store.LoadRequired().ToDictionary(x=>x.DeviceId,x=>x.PasswordGenerationFloor);
var rotations=await Task.WhenAll(Post<PasswordResponse>("/api/v1/devices/password/rotate",a.BearerToken,new{}),Post<PasswordResponse>("/api/v1/devices/password/rotate",b.BearerToken,new{}));
Check(store.LoadRequired().All(x=>x.PasswordGenerationFloor==beforeBoth[x.DeviceId]+1),"concurrent device rotations preserve both commits");
long sameGeneration=(await Me(a.BearerToken)).GetProperty("passwordGeneration").GetInt64();
var sameResults=await Task.WhenAll(Send("POST","/api/v1/devices/password/rotate",a.BearerToken,new{expectedGeneration=sameGeneration}),Send("POST","/api/v1/devices/password/rotate",a.BearerToken,new{expectedGeneration=sameGeneration}));
Check(sameResults.Select(x=>(int)x.StatusCode).OrderBy(x=>x).SequenceEqual(new[]{200,409}),"same generation concurrent rotate yields one200 one409");
foreach(var response in sameResults)response.Dispose();
Check(store.LoadRequired().Single(x=>x.DeviceId==a.DeviceId).PasswordGenerationFloor==sameGeneration+1,"conditional race commits exactly one floor");
var rotateRace=Send("POST","/api/v1/devices/password/rotate",b.BearerToken,new{});
var revokeRace=Owner("/api/v1/owner/devices/revoke",new{deviceId=b.DeviceId});
await Task.WhenAll(rotateRace,revokeRace);
using(var rr=await rotateRace)using(var rv=await revokeRace){Check((int)rv.StatusCode==200&&((int)rr.StatusCode==200||(int)rr.StatusCode==401),"concurrent revoke rotate linearized");}
Check(store.LoadRequired().Count==1&&store.LoadRequired()[0].DeviceId==a.DeviceId,"revoke leaves no lost update or resurrection");
await Stop();await Start();
await Status("GET","/api/v1/devices/me",b.BearerToken,null,401,"revoked token remains denied after fourth startup");
Check((await Me(a.BearerToken)).GetProperty("deviceId").GetString()==a.DeviceId,"fourth startup surviving ID unchanged");
await Stop();
string valid=File.ReadAllText(state);
File.WriteAllText(state,"{\"Version\":1,\"Version\":1,\"Devices\":[]}");
Reject(()=>store.LoadRequired(),"duplicate JSON keys rejected");
File.WriteAllText(state,valid);
var entries=store.LoadRequired().ToList();entries.Add(entries[0]);
Reject(()=>store.Save(entries),"duplicate device rejected");
Reject(()=>store.Save(new[]{store.LoadRequired()[0] with{PasswordGenerationFloor=long.MaxValue}}),"generation overflow rejected");
Check(File.ReadAllText(state)==valid,"invalid state writes preserve file");
// Each failure must occur in BuildApp, before a listener can be started.
using var reservation=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);reservation.Start();int refusedPort=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
void RejectStartup(string name){Reject(()=>PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions{DeviceStatePath=state,InvitationCode=invite,EphemeralCertificate=cert,Port=refusedPort}),name);using var probe=new System.Net.Sockets.TcpClient();bool opened=false;try{probe.Connect(IPAddress.Loopback,refusedPort);opened=true;}catch(System.Net.Sockets.SocketException){}Check(!opened,name+" no listener");}
File.WriteAllText(state,"not-json");RejectStartup("corrupt startup rejected");File.WriteAllText(state,valid);
File.Move(state,state+".missing-test");RejectStartup("missing production state rejected");File.Move(state+".missing-test",state);
var survivor=store.LoadRequired()[0];
Reject(()=>store.Save(new[]{survivor,survivor with{DeviceId=survivor.DeviceId=="1234567890"?"1234567891":"1234567890"}}),"duplicate credential hash rejected");
File.WriteAllBytes(state,new byte[DeviceRegistryStateStore.MaxFileBytes+1]);RejectStartup("oversize startup rejected");File.WriteAllText(state,valid);
// Readable valid state, but replacement/create forbidden: startup bump cannot listen.
if(OperatingSystem.IsWindows()){
using var readOnlyShare=new FileStream(state,FileMode.Open,FileAccess.Read,FileShare.Read);
RejectStartup("readable state startup commit failure");
}else{
var mode=File.GetUnixFileMode(dir);
try{File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserExecute);RejectStartup("readable state startup commit failure");}
finally{File.SetUnixFileMode(dir,mode);}
}
Check(File.ReadAllText(state)==valid,"startup commit failure preserves original");
store.Save(new[]{survivor with{PasswordGenerationFloor=long.MaxValue-1}});
RejectStartup("startup floor bump overflow rejected");File.WriteAllText(state,valid);
// Simulated crash image: disk has future committed floor, memory was never installed.
long futureFloor=survivor.PasswordGenerationFloor+7;
store.Save(new[]{survivor with{PasswordGenerationFloor=futureFloor}});
await Start();
Check((await Me(a.BearerToken)).GetProperty("passwordGeneration").GetInt64()==futureFloor+1,"precommitted crash-image floor advances on restart simulation");
await Status("GET","/api/v1/devices/me",b.BearerToken,null,401,"deleted-device crash image stays revoked simulation");
await Stop();
Console.WriteLine("DEVICE_PERSISTENCE_PASS "+checks);
}finally{await Stop();}
