using System.Net;using System.Net.Http.Headers;using System.Net.Http.Json;using System.Security.Cryptography;using System.Security.Cryptography.X509Certificates;using System.Text.Json;using Microsoft.AspNetCore.Hosting.Server;using Microsoft.AspNetCore.Hosting.Server.Features;using Microsoft.Extensions.DependencyInjection;using PeerDesk.Core;using PeerDesk.Server;
int n=0;void Check(bool x,string s){if(!x)throw new Exception(s);Console.WriteLine("PASS "+s);n++;}
using var rsa=RSA.Create(2048);var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeFixed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);string file=Path.Combine(dir,"devices.json");DeviceRegistryStateStore.Initialize(file,100);
var clock=new TestClock();var opts=new ServerOptions{InvitationCode="SYNTHETIC_TEST_INVITE_ONLY",EphemeralCertificate=cert,Port=0,DeviceStatePath=file,MaxDevices=100,Clock=clock,SessionTtl=TimeSpan.FromHours(4)};
var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);await app.StartAsync();string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();using var http=PinnedHttpClientFactory.CreateClient(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)));
async Task<HttpResponseMessage> Send(string verb,string path,string? token=null,object? body=null){using var q=new HttpRequestMessage(new HttpMethod(verb),url+path);if(token!=null)q.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);if(body!=null)q.Content=JsonContent.Create(body);return await http.SendAsync(q);}
async Task<T> Ok<T>(string verb,string path,string? token=null,object? body=null){using var r=await Send(verb,path,token,body);if(!r.IsSuccessStatusCode)throw new Exception(path+" status="+(int)r.StatusCode);return (await r.Content.ReadFromJsonAsync<T>())!;}
async Task Status(string verb,string path,string token,object? body,int expected,string label){using var r=await Send(verb,path,token,body);Check((int)r.StatusCode==expected,label+" actual="+(int)r.StatusCode);}
try{
var a=await Ok<RegisterResponse>("POST","/api/v1/register",null,new RegisterRequest(opts.InvitationCode));var b=await Ok<RegisterResponse>("POST","/api/v1/register",null,new RegisterRequest(opts.InvitationCode));
await Status("GET","/api/v1/devices/fixed-password","bad",null,401,"fixed own bearer required");
var state=await Ok<FixedPasswordStatusResponse>("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password="SyntheticFixed!1"});Check(state.Configured&&state.Revision==1,"fixed configured");
var blank=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId));Check(blank.AuthorizationKind=="manual"&&!blank.AutoApprovalEligible,"blank remains manual");
var duplicate=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,""));Check(duplicate.SessionId==blank.SessionId,"blank pending dedupe");
await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"DefinitelyWrong"),401,"wrong password creates no authorization");
var snap=await Ok<SessionListResponse>("GET","/api/v1/sessions/snapshot",b.BearerToken);Check(snap.Sessions.Count==1,"wrong password creates no session");
var runtime=await Ok<AccessRuntimeResponse>("POST","/api/v1/devices/access-runtime",b.BearerToken,new AccessRuntimeRequest(1,true,Guid.NewGuid().ToString("N")));
var fixedReq=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!1"));Check(fixedReq.AutoApprovalEligible&&fixedReq.AuthorizationKind=="fixed-password"&&fixedReq.RuntimeEpoch==runtime.RuntimeEpoch,"server fixed provenance binds runtime");
await Status("POST","/api/v1/sessions/decision",a.BearerToken,new DecisionRequest(fixedReq.SessionId,true,"password",runtime.RuntimeEpoch),403,"initiator cannot auto approve");
await Status("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(fixedReq.SessionId,true,"password",new string('0',32)),409,"wrong runtime epoch denied");
await Ok<SessionResponse>("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(fixedReq.SessionId,true,"password",runtime.RuntimeEpoch));var conn=await Ok<SessionResponse>("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(fixedReq.SessionId));Check(conn.Status=="Connected","fixed auth target decision connect");
var me=await Ok<JsonElement>("GET","/api/v1/devices/me",b.BearerToken);Check(me.GetProperty("passwordGeneration").GetInt64()==b.PasswordGeneration,"fixed connect preserves temporary credential");
var tmp=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,b.TemporaryPassword));Check(tmp.AuthorizationKind=="temporary-password"&&tmp.AutoApprovalEligible,"temporary OR fixed accepted");
await Ok<FixedPasswordStatusResponse>("DELETE","/api/v1/devices/fixed-password",b.BearerToken);snap=await Ok<SessionListResponse>("GET","/api/v1/sessions/snapshot",b.BearerToken);Check(snap.Sessions.Single(x=>x.SessionId==conn.SessionId).Status=="Connected","fixed removal retains Connected");
await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!1"),401,"deleted fixed no longer authenticates");
await Ok<FixedPasswordStatusResponse>("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password="SyntheticFixed!2"});
await app.StopAsync();await app.DisposeAsync();app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);await app.StartAsync();url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
state=await Ok<FixedPasswordStatusResponse>("GET","/api/v1/devices/fixed-password",b.BearerToken);Check(state.Configured&&state.Revision==3,"fixed persisted restart same bearer");
var restored=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!2"));Check(restored.AuthorizationKind=="fixed-password"&&!restored.AutoApprovalEligible,"restart clears runtime authority not fixed verifier");
string disk=File.ReadAllText(file);Check(!disk.Contains("SyntheticFixed")&&!disk.Contains(b.BearerToken),"state contains no raw fixed password or bearer");Check(JsonDocument.Parse(disk).RootElement.GetProperty("Version").GetInt32()==2,"device state schema2");

var rt2=await Ok<AccessRuntimeResponse>("POST","/api/v1/devices/access-runtime",b.BearerToken,new AccessRuntimeRequest(1,true,Guid.NewGuid().ToString("N")));
var stale=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!2"));
await Ok<SessionResponse>("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(stale.SessionId,true,"password",rt2.RuntimeEpoch));
await Ok<AccessRuntimeResponse>("POST","/api/v1/devices/access-runtime",b.BearerToken,new AccessRuntimeRequest(1,false,Guid.NewGuid().ToString("N")));
await Status("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(stale.SessionId),409,"runtime disable rejects approved auto connect");
var revBefore=(await Ok<FixedPasswordStatusResponse>("GET","/api/v1/devices/fixed-password",b.BearerToken)).Revision;
string held=file+".held";File.Move(file,held);Directory.CreateDirectory(file);
try{
 await Status("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password="FailedWriteValue!"},503,"fixed update write failure is503");
 await Status("DELETE","/api/v1/devices/fixed-password",b.BearerToken,null,503,"fixed delete write failure is503");
 state=await Ok<FixedPasswordStatusResponse>("GET","/api/v1/devices/fixed-password",b.BearerToken);
 Check(state.Configured&&state.Revision==revBefore,"write failures preserve fixed revision and configured state");
 var valid=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!2"));
 Check(valid.AuthorizationKind=="fixed-password","write failures preserve previous password");
 await Status("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"FailedWriteValue!"),401,"failed new password never installed");
}finally{Directory.Move(file,file+".failed-write-directory");File.Move(held,file);}
rt2=await Ok<AccessRuntimeResponse>("POST","/api/v1/devices/access-runtime",b.BearerToken,new AccessRuntimeRequest(1,true,Guid.NewGuid().ToString("N")));
var pendingOld=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!2"));
await Ok<FixedPasswordStatusResponse>("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password="SyntheticFixed!3"});
snap=await Ok<SessionListResponse>("GET","/api/v1/sessions/snapshot",b.BearerToken);Check(snap.Sessions.Single(x=>x.SessionId==pendingOld.SessionId).Status=="Revoked","fixed update revokes old pending");
await Status("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(pendingOld.SessionId,true,"password",rt2.RuntimeEpoch),400,"fixed revision update blocks old pending authorization");
var fresh=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,"SyntheticFixed!3"));
Check(fresh.AuthorizationRevision==revBefore+1,"new request has current fixed revision");

var tempNow=await Ok<PasswordResponse>("POST","/api/v1/devices/password/rotate",b.BearerToken);
clock.Advance(TimeSpan.FromSeconds(3590));
var rtNearExpiry=await Ok<AccessRuntimeResponse>("POST","/api/v1/devices/access-runtime",b.BearerToken,new AccessRuntimeRequest(1,true,Guid.NewGuid().ToString("N")));
var expReq=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,tempNow.TemporaryPassword));
Check(expReq.AutoApprovalEligible&&expReq.RuntimeEpoch==rtNearExpiry.RuntimeEpoch,"temporary expiry fixture starts with current runtime proof");
clock.Advance(TimeSpan.FromSeconds(11));
await Status("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(expReq.SessionId,true,"password",expReq.RuntimeEpoch),409,"expired temporary proof cannot auto approve");
await app.StopAsync();await app.DisposeAsync();
var before=new DeviceRegistryStateStore(file,100).LoadRequired();
var legacy=before.Select(d=>new{d.DeviceId,d.TokenHashSha256,RegisteredAt=d.RegisteredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),d.PasswordGenerationFloor}).ToArray();
File.WriteAllText(file,JsonSerializer.Serialize(new{Version=1,Devices=legacy}));
if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);await app.StartAsync();url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var migrated=new DeviceRegistryStateStore(file,100).LoadRequired();
Check(migrated.Count==before.Count&&migrated.All(d=>before.Any(old=>old.DeviceId==d.DeviceId&&old.TokenHashSha256==d.TokenHashSha256&&d.PasswordGenerationFloor==old.PasswordGenerationFloor+1)),"v1 migration preserves IDs token hashes and advances floors");
me=await Ok<JsonElement>("GET","/api/v1/devices/me",b.BearerToken);Check(me.GetProperty("deviceId").GetString()==b.DeviceId,"v1 migrated bearer authenticates same device");
Check(JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("Version").GetInt32()==2,"legacy file durably upgraded v2");
if(!OperatingSystem.IsWindows())Check(File.GetUnixFileMode(file)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"migrated file private0600");

for(int race=0;race<3;race++){
 string oldPw="SyntheticRaceOld!"+race,newPw="SyntheticRaceNew!"+race;
 await Ok<FixedPasswordStatusResponse>("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password=oldPw});
 var requestTask=Send("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,oldPw));
 var replaceTask=Ok<FixedPasswordStatusResponse>("PUT","/api/v1/devices/fixed-password",b.BearerToken,new{password=newPw});
 await Task.WhenAll(requestTask,replaceTask);
 using var raced=await requestTask;Check(raced.StatusCode==HttpStatusCode.OK||raced.StatusCode==HttpStatusCode.Unauthorized,"concurrent credential replacement yields authorized-before-change or denial");
 if(raced.StatusCode==HttpStatusCode.OK){var row=(await raced.Content.ReadFromJsonAsync<SessionResponse>())!;var all=await Ok<SessionListResponse>("GET","/api/v1/sessions/snapshot",b.BearerToken);Check(all.Sessions.Single(x=>x.SessionId==row.SessionId).Status=="Revoked","concurrent old proof has no surviving pending authority");}
 var validNew=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId,newPw));Check(validNew.AuthorizationRevision==replaceTask.Result.Revision,"concurrent replacement current password revision accepted");
}
Console.WriteLine("FIXED_ACCESS_PASS "+n);
}finally{await app.StopAsync();await app.DisposeAsync();}
