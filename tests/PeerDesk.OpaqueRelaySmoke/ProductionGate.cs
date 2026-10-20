using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PeerDesk.Core;
using PeerDesk.Server;

internal static class ProductionGate
{
    sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
    public static async Task Run(X509Certificate2 cert,bool presenceOnly=false)
    {
        int checks=0;
        void Check(bool ok,string label) { if(!ok) throw new Exception(label); checks++; Console.WriteLine("PASS "+label); }
        var clock=new Clock();
        var options=new ServerOptions { InvitationCode="SYNTHETIC_OPAQUE_INVITE",EphemeralCertificate=cert,Clock=clock,Port=0,SessionTtl=TimeSpan.FromMinutes(5) };
        await using var app=PeerDeskServerApp.BuildApp(new[]{"--Logging:LogLevel:Default=Warning"},options);
        await app.StartAsync();
        try {
            string url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http=PinnedHttpClientFactory.CreateClient(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)));
            async Task<HttpResponseMessage> Send(string method,string path,string? token=null,object? value=null) {
                using var q=new HttpRequestMessage(new HttpMethod(method),url+path);
                if(token!=null)q.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                if(value!=null)q.Content=JsonContent.Create(value);
                return await http.SendAsync(q);
            }
            async Task<T> Ok<T>(string method,string path,string? token=null,object? value=null) {
                using var r=await Send(method,path,token,value);
                if(!r.IsSuccessStatusCode)throw new Exception(path+" HTTP "+(int)r.StatusCode);
                return (await r.Content.ReadFromJsonAsync<T>())!;
            }
            var a=await Ok<RegisterResponse>("POST","/api/v1/register",null,new RegisterRequest(options.InvitationCode));
            var b=await Ok<RegisterResponse>("POST","/api/v1/register",null,new RegisterRequest(options.InvitationCode));
            var pending=await Ok<SessionResponse>("POST","/api/v1/sessions/request",a.BearerToken,new SessionRequest(b.DeviceId));
            await Ok<SessionResponse>("POST","/api/v1/sessions/decision",b.BearerToken,new DecisionRequest(pending.SessionId,true));
            var connected=await Ok<SessionResponse>("POST","/api/v1/sessions/connect",a.BearerToken,new ConnectRequest(pending.SessionId));
            Check(connected.Status=="Connected","real registration manual consent and connect");
            if(presenceOnly) {
                var other=await Ok<RegisterResponse>("POST","/api/v1/register",null,new RegisterRequest(options.InvitationCode));
                var before=await Ok<JsonElement>("GET","/api/v2/peers/presence?ids="+b.DeviceId,a.BearerToken);
                Check(before.GetProperty("peers")[0].GetProperty("state").GetString()=="offline","allowed unseen heartbeat observation is offline");
                await Ok<JsonElement>("GET","/api/v1/devices/me",b.BearerToken);
                var seen=await Ok<JsonElement>("GET","/api/v2/peers/presence?ids="+b.DeviceId,a.BearerToken);
                Check(seen.GetProperty("peers")[0].GetProperty("state").GetString()=="online","authenticated own me heartbeat makes approved peer online");
                var hidden=await Ok<JsonElement>("GET","/api/v2/peers/presence?ids="+b.DeviceId+",0000000000",other.BearerToken);
                Check(hidden.GetProperty("peers").EnumerateArray().All(x=>x.GetProperty("state").GetString()=="unknown"),"unrelated registered and unknown IDs indistinguishable");
                Check(!hidden.GetRawText().Contains("Time")&&!hidden.GetRawText().Contains("At"),"presence exposes no observation timestamps");
                clock.UtcNow=clock.UtcNow.AddSeconds(30);
                var stale=await Ok<JsonElement>("GET","/api/v2/peers/presence?ids="+b.DeviceId,a.BearerToken);
                Check(stale.GetProperty("peers")[0].GetProperty("state").GetString()=="offline","presence read does not renew heartbeat");
                using(var invalid=await Send("GET","/api/v2/peers/presence?ids="+b.DeviceId+","+b.DeviceId,a.BearerToken))
                    Check((int)invalid.StatusCode==400,"duplicate presence IDs rejected");
                Console.WriteLine("PRESENCE_PRODUCTION_PASS "+checks);return;
            }
            var sid=pending.SessionId;var prefix="/api/v1/sessions/"+sid;var opaque="/api/v2/sessions/"+sid+"/opaque";
            await Ok<JsonElement>("POST",prefix+"/sharing",b.BearerToken,new{enabled=true});
            var d=await Ok<JsonElement>("POST",opaque+"/configure",b.BearerToken,new{permissionBits=15,permissionRevision=1,geometryVersion=1});
            Check(d.GetProperty("targetDeviceId").GetString()==b.DeviceId&&d.GetProperty("initiatorDeviceId").GetString()==a.DeviceId,"production descriptor actual participants");
            using(var denied=await Send("POST",opaque+"/configure",a.BearerToken,new{permissionBits=15,permissionRevision=1,geometryVersion=1}))
                Check((int)denied.StatusCode==403,"production initiator configure denied");
            clock.UtcNow=clock.UtcNow.AddSeconds(2);
            for(int i=0;i<59;i++) {
                using var r=await Send("GET",opaque+"/descriptor",b.BearerToken);
                if((int)r.StatusCode!=200)throw new Exception("descriptor shared budget early failure");
            }
            using(var last=await Send("GET",prefix+"/lease",b.BearerToken))Check((int)last.StatusCode==200,"legacy request consumes shared sixtieth operation");
            using(var limited=await Send("GET",opaque+"/descriptor",b.BearerToken))Check((int)limited.StatusCode==429,"opaque cannot bypass exhausted legacy shared media bucket");
            clock.UtcNow=clock.UtcNow.AddSeconds(2);
            using(var legacy=await Send("GET",prefix+"/lease",b.BearerToken))Check((int)legacy.StatusCode==200,"shared budget next window recovers");
            await Ok<SessionResponse>("POST","/api/v1/sessions/revoke",a.BearerToken,new RevokeRequest(sid));
            using(var terminal=await Send("GET",opaque+"/descriptor",b.BearerToken))Check((int)terminal.StatusCode==404,"revoked session invalidates opaque descriptor");
            Console.WriteLine("OPAQUE_PRODUCTION_PASS "+checks);
        } finally { await app.StopAsync(); }
    }
}
