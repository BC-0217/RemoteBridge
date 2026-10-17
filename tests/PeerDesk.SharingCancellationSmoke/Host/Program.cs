using System.Diagnostics;using System.Net;using System.Net.Http.Json;using System.Security.Cryptography;using System.Security.Cryptography.X509Certificates;using System.Text.Json;using Microsoft.AspNetCore.Hosting.Server;using Microsoft.AspNetCore.Hosting.Server.Features;using PeerDesk.Core;using PeerDesk.Server;
using var rsa=RSA.Create(2048);var cr=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);cr.CertificateExtensions.Add(san.Build());using var raw=cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));using var cert=X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx),(string?)null,X509KeyStorageFlags.DefaultKeySet);
string mode=args.Length>1?args[1]:"";bool slow=mode.StartsWith("--");
const string invitation="OWNED_CANCEL_TEST_INVITATION";int starts=0,leases=0,frames=0,sharingRequests=0,frameGets=0,inputGets=0;
var perfWatch=Stopwatch.StartNew();var frameTimes=new List<double>();long frameBytes=0;int inputLeaseGets=0;object perfGate=new();var sentSequences=new List<long>();var sentKinds=new List<string>();var inputGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int deliveredBatchRows=0,inputPostAttempts=0;
var app=PeerDeskServerApp.BuildApp(Array.Empty<string>(),new ServerOptions{InvitationCode=invitation,EphemeralCertificate=cert,SessionTtl=TimeSpan.FromMinutes(5)});
app.Use(async(ctx,next)=>{
 var path=ctx.Request.Path.Value??""; if(path.EndsWith("/input-lease"))Interlocked.Increment(ref inputLeaseGets); if(path.EndsWith("/frame")&&ctx.Request.Method=="GET")Interlocked.Increment(ref frameGets);if(path.EndsWith("/input")&&ctx.Request.Method=="GET")Interlocked.Increment(ref inputGets);
 long sentSequence=0;string sentKind="";if((mode=="--send-order"||mode=="--send-close"||mode=="--motion-budget"||mode=="--mouse-events")&&path.EndsWith("/input")&&ctx.Request.Method=="POST"){ctx.Request.EnableBuffering();using var json=await JsonDocument.ParseAsync(ctx.Request.Body);sentSequence=json.RootElement.GetProperty("sequence").GetInt64();sentKind=json.RootElement.GetProperty("kind").GetString()!;Interlocked.Increment(ref inputPostAttempts);ctx.Request.Body.Position=0;if(mode=="--motion-budget")await Task.Delay(100);if(sentSequence==1)await Task.Delay(mode=="--send-close"?300:80);}
 if((mode=="--input-expired"||mode=="--input32")&&path.EndsWith("/input-lease")){await inputGate.Task.WaitAsync(TimeSpan.FromSeconds(5));var original=ctx.Response.Body;using var buffer=new MemoryStream();ctx.Response.Body=buffer;try{await next();buffer.Position=0;using(var doc=await JsonDocument.ParseAsync(buffer)){deliveredBatchRows=Math.Max(deliveredBatchRows,doc.RootElement.GetProperty("events").GetArrayLength());}if(mode=="--input-expired")await Task.Delay(150);buffer.Position=0;await buffer.CopyToAsync(original);}finally{ctx.Response.Body=original;}return;}
 if(path.EndsWith("/lease")){
 int n=Interlocked.Increment(ref leases);
 if(mode=="--lease-stale")await Task.Delay(150);
 if(mode=="--lease-generation"&&n==2){var original=ctx.Response.Body;using var buffer=new MemoryStream();ctx.Response.Body=buffer;try{await next();buffer.Position=0;using var doc=await JsonDocument.ParseAsync(buffer);long gen=doc.RootElement.GetProperty("generation").GetInt64();ctx.Response.Body=original;ctx.Response.ContentLength=null;await ctx.Response.WriteAsJsonAsync(new{generation=gen+1,leaseMs=100});}finally{ctx.Response.Body=original;}return;}
 }
 if(path.EndsWith("/frame")&&ctx.Request.Method=="PUT")Interlocked.Increment(ref frames);
 if(path.EndsWith("/sharing")&&ctx.Request.Method=="POST"){
 Interlocked.Increment(ref sharingRequests);
 ctx.Request.EnableBuffering();string body=await new StreamReader(ctx.Request.Body,leaveOpen:true).ReadToEndAsync();ctx.Request.Body.Position=0;
 if(body.Contains("true")&&!slow){
 Interlocked.Increment(ref starts);var original=ctx.Response.Body;using var buffer=new MemoryStream();ctx.Response.Body=buffer;
 try{await next();await Task.Delay(1500);buffer.Position=0;await buffer.CopyToAsync(original);}catch(OperationCanceledException){}finally{ctx.Response.Body=original;}return;
 }}
 await next();
 if(sentSequence>0&&ctx.Response.StatusCode==204){lock(perfGate){sentSequences.Add(sentSequence);sentKinds.Add(sentKind);}}
 if(path.EndsWith("/frame")&&ctx.Request.Method=="PUT"&&ctx.Response.StatusCode==204){lock(perfGate){frameTimes.Add(perfWatch.Elapsed.TotalMilliseconds);frameBytes+=ctx.Request.ContentLength??0;}}
});
await app.StartAsync();
try{
 string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();string pin=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));using var http=PinnedHttpClientFactory.CreateClient(pin);
 async Task<T> Post<T>(string path,object body,string? token=null){using var req=new HttpRequestMessage(HttpMethod.Post,url+path){Content=JsonContent.Create(body)};if(token!=null)req.Headers.Add("Authorization","Bearer "+token);using var resp=await http.SendAsync(req);resp.EnsureSuccessStatusCode();return(await resp.Content.ReadFromJsonAsync<T>())!;}
 var a=await Post<RegisterResponse>("/api/v1/register",new RegisterRequest(invitation));var b=await Post<RegisterResponse>("/api/v1/register",new RegisterRequest(invitation));var s=await Post<SessionResponse>("/api/v1/sessions/request",new SessionRequest(b.DeviceId,b.TemporaryPassword),a.BearerToken);await Post<SessionResponse>("/api/v1/sessions/decision",new DecisionRequest(s.SessionId,true),b.BearerToken);await Post<SessionResponse>("/api/v1/sessions/connect",new ConnectRequest(s.SessionId),a.BearerToken);
 var psi=new ProcessStartInfo(args[0],slow?mode:""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
 using var child=Process.Start(psi)!;
 await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {Version=1,Name="owned cancellation",RendezvousUrl=url,RelayUrl=url,CertificateSha256=pin}));
 foreach(var value in new[]{b.DeviceId,b.BearerToken,b.TemporaryPassword,b.PasswordExpiresAt.ToString("O"),s.SessionId})await child.StandardInput.WriteLineAsync(value);
 if(mode=="--perf"||mode=="--send-order"||mode=="--send-close"||mode=="--motion-budget"||mode=="--mouse-events"||mode=="--mouse-events"||mode=="--end-feedback")foreach(var value in new[]{a.DeviceId,a.BearerToken,a.TemporaryPassword,a.PasswordExpiresAt.ToString("O")})await child.StandardInput.WriteLineAsync(value);
 if(slow){child.StandardInput.Close();using var limit=new CancellationTokenSource(10000);string? line;bool passed=false;
 while((line=await child.StandardOutput.ReadLineAsync(limit.Token))!=null){Console.WriteLine(line);if(line=="INPUT_STARTED"){using var leaseReq=new HttpRequestMessage(HttpMethod.Get,url+"/api/v1/sessions/"+s.SessionId+"/lease");leaseReq.Headers.Add("Authorization","Bearer "+b.BearerToken);using var leaseResponse=await http.SendAsync(leaseReq);leaseResponse.EnsureSuccessStatusCode();var lease=await leaseResponse.Content.ReadFromJsonAsync<JsonElement>();long gen=lease.GetProperty("generation").GetInt64();for(int n=1;n<=32;n++){using var inputReq=new HttpRequestMessage(HttpMethod.Post,url+"/api/v1/sessions/"+s.SessionId+"/input"){Content=JsonContent.Create(new{generation=gen,sequence=n,kind="text",x=0,y=0,text="q"})};inputReq.Headers.Add("Authorization","Bearer "+a.BearerToken);using var inputResponse=await http.SendAsync(inputReq);inputResponse.EnsureSuccessStatusCode();}inputGate.TrySetResult();}if(line=="CAPTURE_STARTED")await Post<SessionResponse>("/api/v1/sessions/revoke",new RevokeRequest(s.SessionId),a.BearerToken);if(line.StartsWith("TIMING_PASS"))passed=true;}
 await child.WaitForExitAsync(limit.Token);if(child.ExitCode!=0||!passed)throw new Exception("timing child failed");
 if(mode=="--slow-fixed"||mode=="--perf"||mode=="--send-order"||mode=="--send-close"){if(frames<2)throw new Exception("expected repeated frame upload");}
 else if(mode!="--input32"&&mode!="--input-expired"&&mode!="--motion-budget"&&mode!="--mouse-events"&&mode!="--end-feedback"){if(frames!=0)throw new Exception("unexpected frame upload");}
 if((mode=="--input32"||mode=="--input-expired")&&deliveredBatchRows!=32)throw new Exception("not full32 batch");
 if(mode=="--lease-stale"&&leases!=3)throw new Exception("stale retry not bounded3");
 using var frameReq=new HttpRequestMessage(HttpMethod.Get,url+"/api/v1/sessions/"+s.SessionId+"/frame");frameReq.Headers.Add("Authorization","Bearer "+a.BearerToken);using var frame=await http.SendAsync(frameReq);if((int)frame.StatusCode!=409)throw new Exception("expected inactive409 after cleanup");
 if(mode=="--perf"){double[] gaps;lock(perfGate)gaps=frameTimes.Zip(frameTimes.Skip(1),(x,y)=>y-x).ToArray();Console.WriteLine("PERF_HOST inputLeaseGets="+inputLeaseGets+" acceptedFrames="+frameTimes.Count+" bytes="+frameBytes+" avgGapMs="+(gaps.Length>0?gaps.Average():0).ToString("F2")+" maxGapMs="+(gaps.Length>0?gaps.Max():0).ToString("F2"));}
 if(mode=="--mouse-events"){Console.WriteLine("ACTUAL_MOUSE_WIRE="+string.Join(",",sentKinds));int d=sentKinds.LastIndexOf("mouseDown");int u=sentKinds.LastIndexOf("mouseUp");if(d<0||u<=d||!sentKinds.Skip(d+1).Take(u-d-1).Contains("move")||sentKinds.Skip(d+1).Take(u-d-1).Contains("releaseAll"))throw new Exception("actual drag wire sequence incomplete");Console.WriteLine("MOUSE_EVENTS_WIRE_PASS down-move-up preserved");}
 if(mode=="--motion-budget"){if(!sentKinds.SequenceEqual(new[]{"mouseUp","releaseAll"})||!sentSequences.SequenceEqual(new long[]{3,5}))throw new Exception("motion barrier wire mismatch");Console.WriteLine("MOTION_WIRE_PASS mouseUp3 releaseAll5 no move/down POST; seeded rolling budget");}
 if(mode=="--send-order"){if(!sentSequences.SequenceEqual(new long[]{1,2,3}))throw new Exception("Viewer input order/count mismatch");Console.WriteLine("VIEWER_WIRE_ORDER_PASS 1,2,3 (UI method invocation; not physical keyboard events)");}
 if(mode=="--send-close"){if(inputPostAttempts!=1||sentSequences.Count!=0)throw new Exception("queued close emitted extra accepted input");Console.WriteLine("VIEWER_PENDING_CLOSE_PASS attempts1 accepted0");}
 Console.WriteLine("TIMING_HOST_PASS "+mode+" leases="+leases+" frames="+frames+" frameGets="+frameGets+" inputGets="+inputGets);return 0;}

 string? pre=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
 if(pre!="PRE_CANCEL_PASS"||sharingRequests!=0)throw new Exception("pre-cancel emitted HTTP sharing request");Console.WriteLine("PASS pre-cancel sharing HTTP count zero");
 await child.StandardInput.WriteLineAsync("CONTINUE");child.StandardInput.Close();
 var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(12000);await child.WaitForExitAsync(timeout.Token);
 string output=pre+"\n"+await stdout;Console.Write(output);if(child.ExitCode!=0)throw new Exception("child failed: "+await stderr);
 await Task.Delay(1600);
 if(starts!=1||leases!=0||frames!=0||!output.Contains("PRE_CANCEL_PASS")||!output.Contains("DELAY_CANCEL_PASS"))throw new Exception("cancellation counters "+starts+"/"+leases+"/"+frames);
 Console.WriteLine("PASS authenticated delayed sharing response reached; no lease and no frame upload");
 Console.WriteLine("SHARING_CANCEL_PASS 3");return 0;
}finally{await app.StopAsync();await app.DisposeAsync();}
