using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PeerDesk.Core;
using PeerDesk.Server;

static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
if (args.Contains("--wake-coalesce"))
{
    // Narrow mode: drives the production Descriptor.Signal helper and Wake semaphore via reflection under a shared lock.
    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
    var descriptorType = typeof(OpaqueSessionRelay).GetNestedType("Descriptor", System.Reflection.BindingFlags.NonPublic)!;
    var packetType = typeof(OpaqueSessionRelay).GetNestedType("Packet", System.Reflection.BindingFlags.NonPublic)!;
    var descriptor = Activator.CreateInstance(descriptorType, nonPublic: true)!;
    var wake = (SemaphoreSlim)descriptorType.GetField("Wake", flags)!.GetValue(descriptor)!;
    var signal = descriptorType.GetMethod("Signal", flags)!;
    var queues = (System.Collections.IDictionary)descriptorType.GetField("Queues", flags)!.GetValue(descriptor)!;
    var queueType = typeof(Queue<>).MakeGenericType(packetType);
    var queue = Activator.CreateInstance(queueType)!;
    queues["target"] = queue;
    var enqueue = queueType.GetMethod("Enqueue")!; var dequeue = queueType.GetMethod("Dequeue")!;
    var countProp = queueType.GetProperty("Count")!;
    var wakeLock = new object();
    void Enqueue() { lock (wakeLock) { enqueue.Invoke(queue, [Activator.CreateInstance(packetType, (byte)1, new byte[1])]); signal.Invoke(descriptor, null); } }
    int Drain() { var n = 0; lock (wakeLock) while ((int)countProp.GetValue(queue)! > 0) { dequeue.Invoke(queue, null); n++; } return n; }
    Require(wake.CurrentCount == 0, "wake starts unsignalled");
    for (var i = 0; i < 1500; i++) Enqueue();
    Require(wake.CurrentCount == 1, $"sustained backlog coalesces to one notification, got {wake.CurrentCount}");
    Require(wake.Wait(0), "coalesced notification consumable");
    Require(Drain() == 1500, "queue is authoritative for the whole backlog");
    Require(!wake.Wait(0), "no stale notification after drain");
    Enqueue();
    Require(wake.CurrentCount == 1 && wake.Wait(0), "enqueue after drain supplies a fresh signal");
    Require(Drain() == 1, "post-drain packet delivered");
    // Consumer pattern: examine queue under the lock, then await; an enqueue in between must wake the waiter.
    lock (wakeLock) Require((int)countProp.GetValue(queue)! == 0, "queue empty before wait");
    var pending = wake.WaitAsync(TimeSpan.FromSeconds(5));
    Enqueue();
    Require(await pending, "waiter woken by enqueue after empty check");
    Require(Drain() == 1 && wake.CurrentCount == 0, "waiter drained");
    using (var cts = new CancellationTokenSource())
    {
        var cancelled = wake.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        try { await cancelled; Require(false, "cancelled wait completed"); } catch (OperationCanceledException) { }
    }
    for (var i = 0; i < 1100; i++) Enqueue();
    Require(wake.CurrentCount == 1 && wake.Wait(0) && Drain() == 1100 && !wake.Wait(0), "signal survives cancelled wait and recoalesces");
    // Invalidate-style signal on an already-signalled descriptor must not overflow either.
    lock (wakeLock) { signal.Invoke(descriptor, null); signal.Invoke(descriptor, null); }
    Require(wake.CurrentCount == 1, "repeated invalidate signals coalesce");
    Console.WriteLine("WAKE_COALESCE_PASS 1: 1500+1100 enqueue/drain, post-drain signal, wait-after-empty, cancel, invalidate coalesce");
    return;
}
var builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
await using var app = builder.Build();
var gate = new object();
var blockedWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelledWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Query["stall"] != "1") { await next(); return; }
    var original = ctx.Response.Body;
    ctx.Response.Body = new BlockWriteStream(original, blockedWrite, cancelledWrite);
    try { await next(); } finally { ctx.Response.Body = original; }
});
var allowRate = true;
var clock = new TestClock();
var sessions = new ConcurrentDictionary<string, SessionState>();
var session = new SessionState { SessionId = "s1", InitiatorDeviceId = "initiator", TargetDeviceId = "target", Status = SessionStatus.Connected };
sessions[session.SessionId] = session;
Func<HttpContext, string?> auth = c => c.Request.Headers["X-Device"].FirstOrDefault();
MediaRelay.Map(app, gate, sessions, auth, () => { }, clock, _ => allowRate);
OpaqueSessionRelay.Map(app, gate, auth, id => id is "initiator" or "target", id => sessions.GetValueOrDefault(id), () => { },
    (_, _) => allowRate, () => clock.UtcNow, 10);
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    HttpClient NewClient(string device) { var c = new HttpClient { BaseAddress = new Uri(address) }; c.DefaultRequestHeaders.Add("X-Device", device); return c; }
    using var target = NewClient("target");
    using var initiator = NewClient("initiator");
    async Task<(HttpStatusCode, JsonElement)> Send(HttpClient c, HttpMethod m, string path, string? json = null)
    {
        using var msg = new HttpRequestMessage(m, path);
        if (json != null) msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var r = await c.SendAsync(msg);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }
    const string Lease = "/api/v1/sessions/s1/capture-lease";
    const string Sharing = "/api/v1/sessions/s1/sharing";
    const string Configure = "/api/v2/sessions/s1/opaque/configure";
    const string Cfg = """{"permissionBits":15,"permissionRevision":1,"geometryVersion":1}""";
    int passed = 0; void Pass(bool ok, string m) { Require(ok, m); passed++; }

    // Inactive share: no capture authority.
    Pass((await Send(target, HttpMethod.Get, Lease)).Item1 != HttpStatusCode.OK, "inactive share granted capture lease");

    // Wrong version / version without media optimization rejected.
    Pass((await Send(target, HttpMethod.Post, Sharing, """{"enabled":true,"statefulInputVersion":1,"mediaOptimizationVersion":1,"captureLeaseVersion":2}""")).Item1 == HttpStatusCode.BadRequest, "captureLeaseVersion 2 accepted");
    Pass((await Send(target, HttpMethod.Post, Sharing, """{"enabled":true,"statefulInputVersion":1,"mediaOptimizationVersion":1,"captureLeaseVersion":"1"}""")).Item1 == HttpStatusCode.BadRequest, "string captureLeaseVersion accepted");
    Pass((await Send(target, HttpMethod.Post, Sharing, """{"enabled":true,"statefulInputVersion":1,"captureLeaseVersion":1}""")).Item1 == HttpStatusCode.BadRequest, "capture v1 without media v1 accepted");

    // Legacy media-v1 share: no capture fields, lease endpoint conflicts, descriptor keeps legacy shape.
    var (st, legacy) = await Send(target, HttpMethod.Post, Sharing, """{"enabled":true,"statefulInputVersion":1,"mediaOptimizationVersion":1}""");
    Pass(st == HttpStatusCode.OK && !legacy.TryGetProperty("captureLeaseVersion", out _) && legacy.GetProperty("minFrameIntervalMs").GetInt32() == 50, "legacy media v1 caps changed");
    Pass((await Send(target, HttpMethod.Get, Lease)).Item1 == HttpStatusCode.Conflict, "non-negotiated share granted capture lease");
    var (cst, legacyDesc) = await Send(target, HttpMethod.Post, Configure, Cfg);
    Pass(cst == HttpStatusCode.OK && !legacyDesc.TryGetProperty("captureLeaseVersion", out _) && !legacyDesc.TryGetProperty("minFrameIntervalMs", out _)
        && !legacyDesc.TryGetProperty("transportKeepaliveVersion", out _), "legacy descriptor shape changed");
    // Configure body cannot claim capture policy.
    Pass((await Send(target, HttpMethod.Post, Configure, """{"permissionBits":15,"permissionRevision":1,"geometryVersion":1,"captureLeaseVersion":1}""")).Item1 == HttpStatusCode.BadRequest, "untrusted capture claim accepted");

    // Capture v1 negotiated.
    var (ost, caps) = await Send(target, HttpMethod.Post, Sharing, """{"enabled":true,"statefulInputVersion":1,"mediaOptimizationVersion":1,"captureLeaseVersion":1}""");
    Pass(ost == HttpStatusCode.OK && caps.GetProperty("captureLeaseVersion").GetInt32() == 1 && caps.GetProperty("captureLeaseMs").GetInt32() == 250
        && caps.GetProperty("minFrameIntervalMs").GetInt32() == 100, "capture v1 caps wrong");
    var generation = caps.GetProperty("generation").GetInt64();
    var (lst, lease) = await Send(target, HttpMethod.Get, Lease);
    Pass(lst == HttpStatusCode.OK && lease.GetProperty("generation").GetInt64() == generation && lease.GetProperty("captureLeaseVersion").GetInt32() == 1
        && lease.GetProperty("leaseMs").GetInt32() == 250 && !lease.TryGetProperty("captureLeaseMs", out _) && !lease.TryGetProperty("events", out _), "capture lease shape wrong");
    Pass((await Send(initiator, HttpMethod.Get, Lease)).Item1 == HttpStatusCode.Forbidden, "initiator granted capture lease");
    Pass((await Send(NewClient("outsider"), HttpMethod.Get, Lease)).Item1 == HttpStatusCode.Forbidden, "outsider granted capture lease");
    Pass(session.LastMediaActivityAt is null, "capture lease renewed idle TTL");
    var (ist, input) = await Send(target, HttpMethod.Get, "/api/v1/sessions/s1/input-lease");
    Pass(ist == HttpStatusCode.OK && input.GetProperty("leaseMs").GetInt32() == 100, "input lease 100 changed");
    allowRate = false;
    Pass((int)(await Send(target, HttpMethod.Get, Lease)).Item1 == 429, "capture lease bypassed media rate");
    allowRate = true;

    // Opaque descriptor derives capture policy from actual session media.
    var (dst, desc) = await Send(target, HttpMethod.Post, Configure, Cfg);
    Pass(dst == HttpStatusCode.OK && desc.GetProperty("captureLeaseVersion").GetInt32() == 1 && desc.GetProperty("captureLeaseMs").GetInt32() == 250
        && desc.GetProperty("minFrameIntervalMs").GetInt32() == 100 && desc.GetProperty("sharingGeneration").GetInt64() == generation && desc.GetProperty("transportKeepaliveVersion").GetInt32() == 1, "capture descriptor wrong");
    var (gst, got) = await Send(initiator, HttpMethod.Get, "/api/v2/sessions/s1/opaque/descriptor");
    Pass(gst == HttpStatusCode.OK && got.GetProperty("captureLeaseVersion").GetInt32() == 1 && got.GetProperty("transportKeepaliveVersion").GetInt32() == 1, "participant descriptor lacks capture policy");

    // Keepalive v1 stream: idle heartbeat is a bare zero-length header with no media activity; data still flows afterwards.
    var epoch = desc.GetProperty("transportEpoch").GetString();
    var activityBefore = session.LastMediaActivityAt;
    var started = DateTime.UtcNow;
    using (var streamResponse = await target.GetAsync($"/api/v2/sessions/s1/opaque/stream?epoch={epoch}", HttpCompletionOption.ResponseHeadersRead))
    {
        Pass(streamResponse.StatusCode == HttpStatusCode.OK, "keepalive stream not opened");
        await using var stream = await streamResponse.Content.ReadAsStreamAsync();
        async Task<byte[]> ReadExact(int n, TimeSpan within)
        {
            using var cts = new CancellationTokenSource(within); var buf = new byte[n];
            await stream.ReadExactlyAsync(buf, cts.Token); return buf;
        }
        var heartbeat = await ReadExact(4, TimeSpan.FromSeconds(15));
        var waited = DateTime.UtcNow - started;
        Pass(heartbeat.All(b => b == 0) && waited >= TimeSpan.FromSeconds(9), $"heartbeat wrong after {waited}");
        Pass(session.LastMediaActivityAt == activityBefore, "heartbeat renewed media activity");
        using var packet = new ByteArrayContent(new byte[85]);
        using var post = await initiator.PostAsync($"/api/v2/sessions/s1/opaque/packet?epoch={epoch}", packet);
        Pass(post.StatusCode == HttpStatusCode.Accepted, "packet after heartbeat rejected");
        var header = await ReadExact(5, TimeSpan.FromSeconds(5));
        Pass(header[0] == 0 && header[1] == 0 && header[2] == 0 && header[3] == 85 && header[4] == 0, "data frame after heartbeat wrong");
        await ReadExact(84, TimeSpan.FromSeconds(5));
    }

    // An independently monitored revoke must interrupt blocked body IO, before the 5s IO deadline.
    // Closing the first stream may abort mid-IO and terminate its descriptor, so the stall uses a fresh descriptor.
    var (fst, fresh) = await Send(target, HttpMethod.Post, Configure, Cfg);
    Pass(fst == HttpStatusCode.OK && fresh.GetProperty("transportKeepaliveVersion").GetInt32() == 1, "fresh capture descriptor wrong");
    var stallEpoch = fresh.GetProperty("transportEpoch").GetString();
    Pass(stallEpoch != epoch, "fresh descriptor reused epoch");
    using (var blockedResponse = await target.GetAsync($"/api/v2/sessions/s1/opaque/stream?epoch={stallEpoch}&stall=1", HttpCompletionOption.ResponseHeadersRead))
    {
        Pass(blockedResponse.StatusCode == HttpStatusCode.OK, "stalled stream opened");
        using var body = new ByteArrayContent(new byte[85]);
        using var accepted = await initiator.PostAsync($"/api/v2/sessions/s1/opaque/packet?epoch={stallEpoch}", body);
        Pass(accepted.StatusCode == HttpStatusCode.Accepted, "stalled packet rejected");
        await blockedWrite.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // While authority is still valid the watcher must leave blocked IO alone.
        await Task.Delay(500);
        Pass(!cancelledWrite.Task.IsCompleted, "valid blocked write cancelled before revoke");
        var revokedAt = System.Diagnostics.Stopwatch.StartNew();
        lock (gate) session.Status = SessionStatus.Revoked;
        await cancelledWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelledAfter = revokedAt.Elapsed;
        // Write entered >=0.5s before revoke, so the 5s per-op deadline would land at >=4.5s after revoke.
        Pass(cancelledAfter < TimeSpan.FromSeconds(2), $"blocked write cancelled too late {cancelledAfter}");
        lock (gate) session.Status = SessionStatus.Connected;
        Console.WriteLine($"blocked write cancelled {cancelledAfter.TotalMilliseconds:F0}ms after revoke");
    }

    // Stop: lease denied with stop reason.
    var (sst, _) = await Send(target, HttpMethod.Post, Sharing, """{"enabled":false,"stopReason":"user"}""");
    Pass(sst == HttpStatusCode.OK, "stop failed");
    using (var stopped = await target.GetAsync(Lease))
        Pass(stopped.StatusCode == HttpStatusCode.Conflict && session.Media.CaptureLeaseVersion == 0
            && stopped.Headers.TryGetValues("X-PeerDesk-Media-End", out var end) && end.Single() == "user-stopped", "stopped share lease/stop reason wrong");
    Console.WriteLine($"CAPTURE_LEASE_SERVER_PASS {passed}: caps/lease/descriptor/legacy/role/rate/keepalive/stop-reason");
}
finally { await app.StopAsync(); }

sealed class BlockWriteStream(Stream inner, TaskCompletionSource<bool> entered, TaskCompletionSource<bool> cancelled) : Stream
{
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        entered.TrySetResult(true);
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
    }
    public override Task WriteAsync(byte[] b, int o, int n, CancellationToken ct) => WriteAsync(b.AsMemory(o,n),ct).AsTask();
    public override void Write(byte[] b,int o,int n) => throw new NotSupportedException();
    public override int Read(byte[] b,int o,int n) => throw new NotSupportedException();
    public override long Seek(long o,SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long n) => throw new NotSupportedException();
}
