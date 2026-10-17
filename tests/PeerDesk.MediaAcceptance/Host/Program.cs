using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;
if((args.Length!=1 && args.Length!=2) || !File.Exists(args[0])){Console.Error.WriteLine("Pass exact owned media acceptance EXE path.");return 2;}
using var rsa=RSA.Create(2048);
var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
using var raw=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var invite=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var clock=new AcceptanceClock();
string? deviceState=null;
if(Environment.GetEnvironmentVariable("PEERDESK_TEST_DEVICE_PERSISTENCE")=="1"){
 string deviceDir=Path.Combine(Path.GetTempPath(),"RemoteBridge-media-persistent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(deviceDir);
 if(!OperatingSystem.IsWindows())File.SetUnixFileMode(deviceDir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
 deviceState=Path.Combine(deviceDir,"devices.json");DeviceRegistryStateStore.Initialize(deviceState,100);
 Console.WriteLine("FIXTURE_DEVICE_PERSISTENCE_ENABLED");
}
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},new ServerOptions {DeviceStatePath=deviceState,InvitationCode=invite,EphemeralCertificate=cert,Port=0,Clock=clock,SessionTtl=TimeSpan.FromMinutes(5)});
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
try {
 await app.StartAsync(deadline.Token);
 var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 var profile=new ServerProfile {Version=1,Name="Owned Media Fixture",RendezvousUrl=url,RelayUrl=url,CertificateSha256=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256))};
 var start=new ProcessStartInfo(Path.GetFullPath(args[0])){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(args[0]))!};
 if(args.Length==2)start.ArgumentList.Add(Path.GetFullPath(args[1]));
 using var child=Process.Start(start)??throw new Exception("Fixture start failed");
 var errors=child.StandardError.ReadToEndAsync();
 await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(profile));
 await child.StandardInput.WriteLineAsync(invite);await child.StandardInput.FlushAsync();
 bool passed=false;
 async Task RelayOutput(){
  while(true){
   var line=await child.StandardOutput.ReadLineAsync(deadline.Token);
   if(line is null)break;
   Console.WriteLine(line);
   if(line.StartsWith("MEDIA_ACCEPTANCE_PASS count=",StringComparison.Ordinal))passed=true;
   if(line=="REQUEST_EXPIRE"){clock.Advance(TimeSpan.FromMinutes(10));await child.StandardInput.WriteLineAsync("EXPIRED");await child.StandardInput.FlushAsync();}
   if(line=="REQUEST_PASSWORD_EXPIRE"){clock.Advance(TimeSpan.FromMinutes(16));await child.StandardInput.WriteLineAsync("PASSWORD_EXPIRED");await child.StandardInput.FlushAsync();}
   if(line=="REQUEST_SERVER_STOP"){await app.StopAsync(deadline.Token);await child.StandardInput.WriteLineAsync("SERVER_STOPPED");await child.StandardInput.FlushAsync();}
  }
 }
 var output=RelayOutput();
 try{await child.WaitForExitAsync(deadline.Token);await output;}catch{if(!child.HasExited)child.Kill();throw;}
 Console.Error.Write(await errors);
 child.StandardInput.Close();
 if(child.ExitCode!=0)return child.ExitCode;
 if(!passed)throw new Exception("Child exited without explicit acceptance receipt");
 Console.WriteLine("HOST_REAL_MEDIA_INPUT_ACCEPTANCE_PASS");return 0;
}finally{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await app.StopAsync(cleanup.Token);}
sealed class AcceptanceClock:IClock {
 readonly object gate=new();readonly DateTimeOffset epoch=DateTimeOffset.UtcNow;readonly Stopwatch elapsed=Stopwatch.StartNew();TimeSpan offset;
 public DateTimeOffset UtcNow { get{lock(gate)return epoch+elapsed.Elapsed+offset;} }
 public void Advance(TimeSpan delta){lock(gate)offset+=delta;}
}
