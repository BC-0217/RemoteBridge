using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PeerDesk.Server;
using var rsa=RSA.Create(2048);
var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());
using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
string url="https://127.0.0.1:"+port+"/";
string pin=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
string profile=JsonSerializer.Serialize(new{Version=1,Name="Synthetic Saved Inputs",RendezvousUrl=url,RelayUrl=url,CertificateSha256=pin});
string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),pw=Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
string dir=Path.Combine(Path.GetTempPath(),"saved-input-network-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
string deviceState=Path.Combine(dir,"devices.json");DeviceRegistryStateStore.Initialize(deviceState,100);
var opts=new ServerOptions{DeviceStatePath=deviceState,InvitationCode="legacy-not-used",Port=port,EphemeralCertificate=cert,
 OwnerFeature=new OwnerFeatureOptions{Version=1,OwnerSecret=key,StatePath=Path.Combine(dir,"owner.json"),PublicProfileJson=profile,RegistryIdentity="synthetic-registry",AllowSelfPublish=true},
 DirectoryFeature=new DirectoryFeatureOptions(1,"synthetic-registry",Path.Combine(dir,"directory.json"),Array.Empty<DirectoryGrantOptions>())};
var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
await app.StartAsync(timeout.Token);
try{
 var start=new ProcessStartInfo(Path.GetFullPath(args[0])){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
 start.ArgumentList.Add("--network");start.ArgumentList.Add("--restart-server");
 using var p=Process.Start(start)!;
 await p.StandardInput.WriteLineAsync(profile);await p.StandardInput.WriteLineAsync(key);await p.StandardInput.WriteLineAsync(pw);await p.StandardInput.FlushAsync();
 async Task<string> ReadOutput(){var output=new System.Text.StringBuilder();while(true){var line=await p.StandardOutput.ReadLineAsync(timeout.Token);if(line is null)break;output.AppendLine(line);if(line=="REQUEST_PERSISTENT_SERVER_RESTART"){await app.StopAsync(timeout.Token);await app.DisposeAsync();app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);await app.StartAsync(timeout.Token);await p.StandardInput.WriteLineAsync("PERSISTENT_SERVER_RESTARTED");await p.StandardInput.FlushAsync();}}return output.ToString();}
 var output=ReadOutput();var error=p.StandardError.ReadToEndAsync();
 try{await p.WaitForExitAsync(timeout.Token);}catch{if(!p.HasExited)p.Kill();await p.WaitForExitAsync();Console.Write(await output);Console.Error.Write(await error);throw;}
 Console.Write(await output);Console.Error.Write(await error);return p.ExitCode;
}finally{await app.StopAsync();await app.DisposeAsync();}
