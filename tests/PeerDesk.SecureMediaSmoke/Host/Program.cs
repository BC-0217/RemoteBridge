using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PeerDesk.Core;
using PeerDesk.Server;
using var rsa=RSA.Create(2048);
var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(System.Net.IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
Console.OutputEncoding=new UTF8Encoding(false);
var options=new ServerOptions{InvitationCode="SYNTHETIC_SECURE_MEDIA",EphemeralCertificate=cert,Port=0,SessionTtl=TimeSpan.FromMinutes(5)};
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},options);
if(Environment.GetEnvironmentVariable("PEERDESK_TEST_INPUT_BINDING_GATE")=="1"||Environment.GetEnvironmentVariable("PEERDESK_TEST_VIEWONLY_TIMEOUT")=="1"||Environment.GetEnvironmentVariable("PEERDESK_TEST_BOUND_EDGEFATAL")=="1"){
 int delayedInputLease=0,timeoutEnabled=0;long lastFastInputLeaseResponseTicks=0;
 bool timeoutMode=Environment.GetEnvironmentVariable("PEERDESK_TEST_VIEWONLY_TIMEOUT")=="1";
 app.Use(async(context,next)=>{
  if(timeoutMode&&context.Request.Method=="POST"&&context.Request.Path=="/__test/input-lease-delay") { Interlocked.Exchange(ref timeoutEnabled,1);context.Response.ContentType="text/plain";await context.Response.WriteAsync(Interlocked.Read(ref lastFastInputLeaseResponseTicks).ToString(System.Globalization.CultureInfo.InvariantCulture));return; }
  bool inputLease=context.Request.Method=="GET"&&context.Request.Path.Value!=null&&context.Request.Path.Value.EndsWith("/input-lease",StringComparison.Ordinal);
  bool delay=timeoutMode&&Volatile.Read(ref timeoutEnabled)!=0;
  if(inputLease&&delay)await Task.Delay(1000);
  else if(inputLease&&!timeoutMode&&Interlocked.CompareExchange(ref delayedInputLease,1,0)==0)await Task.Delay(150);
  await next();
  if(inputLease&&timeoutMode&&!delay)Interlocked.Exchange(ref lastFastInputLeaseResponseTicks,System.Diagnostics.Stopwatch.GetTimestamp());
 });
}
await app.StartAsync();
try {
 string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 string pin=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));using var http=PinnedHttpClientFactory.CreateClient(pin);
 async Task<T> Send<T>(string path,string? token,object value){using var q=new HttpRequestMessage(HttpMethod.Post,url+path);if(token!=null)q.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);q.Content=JsonContent.Create(value);using var r=await http.SendAsync(q);r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<T>())!;}
 var a=await Send<RegisterResponse>("/api/v1/register",null,new RegisterRequest(options.InvitationCode));
 var b=await Send<RegisterResponse>("/api/v1/register",null,new RegisterRequest(options.InvitationCode));
 var pending=await Send<SessionResponse>("/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId));
 await Send<SessionResponse>("/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(pending.SessionId,true));
 await Send<SessionResponse>("/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(pending.SessionId));
 var p=new Process{StartInfo=new ProcessStartInfo(args[0]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=new UTF8Encoding(false,true)}};
 p.Start();var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
 await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{url,pin,sid=pending.SessionId,aId=a.DeviceId,aToken=a.BearerToken,bId=b.DeviceId,bToken=b.BearerToken}));
 p.StandardInput.Close();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
 try{await p.WaitForExitAsync(deadline.Token);}catch{if(!p.HasExited)p.Kill(true);await p.WaitForExitAsync();Console.Write(await output);Console.Error.Write(await error);throw;}
 Console.Write(await output);Console.Error.Write(await error);return p.ExitCode;
}finally{await app.StopAsync();}
