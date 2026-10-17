using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using PeerDesk.Core;
using PeerDesk.Server;
int n=0;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);n++;}
using var rsa=RSA.Create(2048);
var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());
using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
string url="https://127.0.0.1:"+port;
string pin=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
string profile=JsonSerializer.Serialize(new{Version=1,Name="Room test",RendezvousUrl=url+"/",RelayUrl=url+"/",CertificateSha256=pin});
string dir=Path.Combine(Path.GetTempPath(),"peerdesk-room-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
const string pw="RoomPw8!";
var clock=new RoomClock();
string grant=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
string foreignProfile=profile.Replace("Room test","Foreign test");
var grants=new[]{new DirectoryGrantOptions(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant))),"pending-room",profile,0),new DirectoryGrantOptions(new string('a',64),"foreign-room",foreignProfile,0)};
var options=new ServerOptions{Clock=clock,InvitationCode="legacy-test-invitation",Port=port,EphemeralCertificate=cert,
OwnerFeature=new OwnerFeatureOptions{Version=1,OwnerSecret=key,StatePath=Path.Combine(dir,"owner.json"),PublicProfileJson=profile,RegistryIdentity="room-test",AllowSelfPublish=true},
DirectoryFeature=new DirectoryFeatureOptions(1,"room-test",Path.Combine(dir,"directory.json"),grants)};
new DirectoryStateStore(options.DirectoryFeature!).Save(DirectoryState.Empty.With(new DirectoryStateEntry("foreign-entry",foreignProfile,new string('b',64),clock.UtcNow.ToUnixTimeSeconds())));
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},options);
await app.StartAsync();
using var http=PinnedHttpClientFactory.CreateClient(pin);
async Task<HttpResponseMessage> Send(string path,object? body=null,bool owner=true){using var req=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,url+path);if(owner)req.Headers.Add(OwnerFeature.OwnerHeader,key);if(body!=null)req.Content=JsonContent.Create(body);return await http.SendAsync(req);}
try{
Check((await Send("/api/v1/owner/room",owner:false)).StatusCode==HttpStatusCode.Unauthorized,"room owner required");
Check((await Send("/api/v1/owner/room",new{serverId="ChanServer-1",joinPassword=(string?)null})).StatusCode==HttpStatusCode.BadRequest,"new room requires password");
using(var r=await Send("/api/v1/owner/room",new{serverId="ChanServer-1",joinPassword=pw})){
Check(r.StatusCode==HttpStatusCode.OK,"room atomic save");
var json=await r.Content.ReadAsStringAsync();using var d=JsonDocument.Parse(json);
Check(d.RootElement.GetProperty("serverId").GetString()=="chanserver-1","canonical room ID");
Check(!json.Contains(pw)&&!json.Contains(key),"response excludes secrets");}
using(var r=await Send("/api/v1/owner/room")){Check(r.StatusCode==HttpStatusCode.OK,"room authenticated get");using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Check(d.RootElement.GetProperty("joinPasswordConfigured").GetBoolean(),"room password configured");}
using(var r=await Send("/api/v1/directory/ChanServer-1",owner:false)){Check(r.StatusCode==HttpStatusCode.OK,"uppercase lookup normalized");using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Check(d.RootElement.GetProperty("profileJson").GetString()==profile,"overlay exact public profile");}
Check((await Send("/api/v1/register",new{invitationCode=pw},false)).StatusCode==HttpStatusCode.OK,"room password actual registration");
Check((await Send("/api/v1/owner/room",new{serverId="second-room",joinPassword=(string?)null})).StatusCode==HttpStatusCode.OK,"new ID retains configured password");
Check((await Send("/api/v1/directory/chanserver-1",owner:false)).StatusCode==HttpStatusCode.OK,"previous ID retained");
foreach(int len in new[]{7,129})Check((await Send("/api/v1/owner/room",new{serverId="invalid-room",joinPassword=new string('x',len)})).StatusCode==HttpStatusCode.BadRequest,"invalid password "+len+" rejected");
Check((await Send("/api/v1/directory/invalid-room",owner:false)).StatusCode==HttpStatusCode.NotFound,"failed save does not publish ID");
Check((await Send("/api/v1/register",new{invitationCode=pw},false)).StatusCode==HttpStatusCode.OK,"failed save preserves password");

using(var req=new HttpRequestMessage(HttpMethod.Get,url+"/api/v1/owner/room")){req.Headers.Add(OwnerFeature.OwnerHeader,new string('X',43));Check((await http.SendAsync(req)).StatusCode==HttpStatusCode.Unauthorized,"wrong owner actual 401");}
Check((await Send("/api/v1/owner/room",new{serverId="foreign-room",joinPassword="Changed8!"})).StatusCode==HttpStatusCode.Conflict,"foreign grant ID rejected");
Check((await Send("/api/v1/owner/room",new{serverId="foreign-entry",joinPassword="Changed8!"})).StatusCode==HttpStatusCode.Conflict,"foreign stored entry ID rejected");
Check((await Send("/api/v1/owner/room",new{serverId="bad-fields",joinPassword=pw,url="https://foreign.invalid"})).StatusCode==HttpStatusCode.BadRequest,"unknown URL field rejected");
using(var r=await Send("/api/v1/owner/room")){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Check(d.RootElement.GetProperty("serverId").GetString()=="second-room","collision preserves primary ID");}
clock.Advance(61);
Check((await Send("/api/v1/register",new{invitationCode=pw},false)).StatusCode==HttpStatusCode.OK,"collision preserves actual password");
// Actual destination-file lock prevents replacement, without changing application or bypassing storage.
using(var locked=new FileStream(options.OwnerFeature!.StatePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
Check((await Send("/api/v1/owner/room",new{serverId="write-failure",joinPassword="Changed8!"})).StatusCode==HttpStatusCode.ServiceUnavailable,"real file replacement failure 503");}
Check((await Send("/api/v1/directory/write-failure",owner:false)).StatusCode==HttpStatusCode.NotFound,"write failure leaves lookup absent");
using(var r=await Send("/api/v1/owner/room")){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Check(d.RootElement.GetProperty("serverId").GetString()=="second-room","write failure preserves primary ID");}
Check((await Send("/api/v1/register",new{invitationCode=pw},false)).StatusCode==HttpStatusCode.OK,"write failure preserves actual password");
Check((await Send("/api/v1/directory/challenge",new{grant},false)).StatusCode==HttpStatusCode.OK,"same-profile challenge issued");
Check((await Send("/api/v1/owner/room",new{serverId="pending-room",joinPassword=(string?)null})).StatusCode==HttpStatusCode.Conflict,"live challenge reserves alias");
clock.Advance(121);
Check((await Send("/api/v1/owner/room",new{serverId="pending-room",joinPassword=(string?)null})).StatusCode==HttpStatusCode.OK,"expired challenge releases alias");
var concurrent=await Task.WhenAll(Send("/api/v1/owner/room",new{serverId="concurrent-a",joinPassword="ConcurrentA!"}),Send("/api/v1/owner/room",new{serverId="concurrent-b",joinPassword="ConcurrentB!"}));
Check(concurrent.All(x=>x.StatusCode==HttpStatusCode.OK||(int)x.StatusCode==429)&&concurrent.Any(x=>x.StatusCode==HttpStatusCode.OK),"concurrent saves succeed serially or return 429");
string concurrentId;
using(var r=await Send("/api/v1/owner/room")){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());concurrentId=d.RootElement.GetProperty("serverId").GetString()!;}
Check(concurrentId=="concurrent-a"||concurrentId=="concurrent-b","concurrent primary belongs to submitted operation");
string concurrentPw=concurrentId=="concurrent-a"?"ConcurrentA!":"ConcurrentB!";
Check((await Send("/api/v1/register",new{invitationCode=concurrentPw},false)).StatusCode==HttpStatusCode.OK,"concurrent primary ID matches same-operation password");
clock.Advance(61);
int retained;
using(var r=await Send("/api/v1/owner/room")){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());retained=d.RootElement.GetProperty("serverIds").GetArrayLength();}
for(int i=retained;i<8;i++)Check((await Send("/api/v1/owner/room",new{serverId="retained-"+i,joinPassword=(string?)null})).StatusCode==HttpStatusCode.OK,"retain ID slot "+i);
Check((await Send("/api/v1/owner/room",new{serverId="ninth-room",joinPassword="Unwanted8!"})).StatusCode==HttpStatusCode.Conflict,"ninth ID denied atomically");
string finalId;
using(var r=await Send("/api/v1/owner/room")){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());finalId=d.RootElement.GetProperty("serverId").GetString()!;}
string finalPw=concurrentPw;
Check((await Send("/api/v1/register",new{invitationCode=finalPw},false)).StatusCode==HttpStatusCode.OK,"concurrent winner ID and password coherent");
await app.StopAsync();
await using(var restarted=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},options)){
await restarted.StartAsync();
using var h=PinnedHttpClientFactory.CreateClient(pin);
Check((await h.GetAsync(url+"/api/v1/directory/"+finalId)).StatusCode==HttpStatusCode.OK,"restart restores alias overlay");
Check((await h.PostAsJsonAsync(url+"/api/v1/register",new{invitationCode=finalPw})).StatusCode==HttpStatusCode.OK,"restart restores password verifier");
await restarted.StopAsync();}
options.OwnerFeature!.AllowSelfPublish=false;
await using(var disabled=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},options)){
await disabled.StartAsync();
Check((await Send("/api/v1/owner/room",new{serverId="disabled-room",joinPassword=pw})).StatusCode==HttpStatusCode.Forbidden,"opt-in false denies room mutation");
await disabled.StopAsync();}
string badState=Path.Combine(dir,"bad-state.json");
File.WriteAllText(badState,"{\"version\":1,\"primaryServerId\":\"invalid-room\",\"serverIds\":[\"invalid-room\"]}");
options.OwnerFeature!.StatePath=badState;options.OwnerFeature.AllowSelfPublish=true;
bool rejected=false;
try{await using var invalid=PeerDeskServerApp.BuildApp(Array.Empty<string>(),options);}catch(InvalidOperationException){rejected=true;}
Check(rejected,"startup rejects room IDs without password verifier");

var validState=File.ReadAllText(Path.Combine(dir,"owner.json"));
foreach(var mutation in new[]{validState.Replace("\"primaryServerId\":", "\"primaryServerId\":\"duplicate\",\"primaryServerId\":"),validState.Replace("\"serverIds\":[","\"serverIds\":[\"duplicate\",\"duplicate\","),validState.Replace("\"serverIds\":[","\"serverIds\":[\"one\",\"two\",\"three\",\"four\",\"five\",\"six\",\"seven\",\"eight\",\"nine\",")})
{
File.WriteAllText(badState,mutation);bool fail=false;try{await using var invalid=PeerDeskServerApp.BuildApp(Array.Empty<string>(),options);}catch(InvalidOperationException){fail=true;}Check(fail,"corrupt duplicate/excess room state rejected");
}
File.WriteAllText(badState,validState);
string foreignStartup=JsonSerializer.Serialize(new DirectoryStateFile(1,"room-test",new[]{new DirectoryStateEntry(finalId,foreignProfile,new string('c',64),clock.UtcNow.ToUnixTimeSeconds())}),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
File.WriteAllText(options.DirectoryFeature!.StatePath,foreignStartup);
bool collision=false;try{await using var invalid=PeerDeskServerApp.BuildApp(Array.Empty<string>(),options);}catch(InvalidOperationException){collision=true;}Check(collision,"startup rejects persisted foreign alias collision");
Console.WriteLine("SUMMARY "+n+" PASS");
}finally{await app.StopAsync();}

sealed class RoomClock:IClock {public DateTimeOffset UtcNow{get;private set;}=DateTimeOffset.UtcNow;public void Advance(int seconds){UtcNow=UtcNow.AddSeconds(seconds);}}
