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
if(args.Length!=1 || !File.Exists(args[0])){Console.Error.WriteLine("Pass exact net48 client fixture EXE path.");return 2;}
using var rsa=RSA.Create(2048);
var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
using var raw=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddHours(1));
using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
var invite=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var opts=new ServerOptions {InvitationCode=invite, EphemeralCertificate=cert, Port=0};
await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},opts);
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
try {
 await app.StartAsync(deadline.Token);
 var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 var profile=new ServerProfile {Version=1,Name="Fixture",RendezvousUrl=url,RelayUrl=url,CertificateSha256=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256))};
 var start=new ProcessStartInfo(Path.GetFullPath(args[0])){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(args[0]))!};
 using var child=Process.Start(start)??throw new Exception("Fixture start failed");
 var output=child.StandardOutput.ReadToEndAsync();var errors=child.StandardError.ReadToEndAsync();
 await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(profile));
 await child.StandardInput.WriteLineAsync(invite);child.StandardInput.Close();
 try{await child.WaitForExitAsync(deadline.Token);}catch(OperationCanceledException){if(!child.HasExited)child.Kill();throw;}
 Console.Write(await output);Console.Error.Write(await errors);
 if(child.ExitCode!=0)return child.ExitCode;
 Console.WriteLine("HOST_NET48_HTTPS_INTEROP_PASS");return 0;
}finally{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await app.StopAsync(cleanup.Token);}
