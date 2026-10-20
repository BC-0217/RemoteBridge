using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace PeerDesk.Server;

public static class OpaqueSessionRelay
{
    private const int MaxDescriptors = 500, MaxReaders = 32;
    private const int MaxQueueMessages = 32, MaxRecipientBytes = 2 * 1024 * 1024, MaxGlobalBytes = 8 * 1024 * 1024;
    private static readonly int[] Caps = { 16_384, 524_288, 16_384, 65_536, 262_144 };
    // Keepalive v1 rides on capture v1 descriptors only: no total stream deadline, per-operation write deadline,
    // and an empty 4-byte frame after 10s without a successful write.
    private const int TransportKeepaliveVersion1 = 1;
    private static readonly TimeSpan StreamOperationDeadline = TimeSpan.FromSeconds(5), HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static async Task<byte[]?> ReadBounded(Stream body, int limit, CancellationToken token)
    {
        var buffer = new byte[limit + 1]; var used = 0;
        while (used < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(used), token);
            if (n == 0) break;
            used += n;
        }
        return used > limit ? null : buffer.AsSpan(0, used).ToArray();
    }
    private sealed record Packet(byte Channel, byte[] Bytes);
    private sealed class Descriptor
    {
        public required string SessionId, Initiator, Target, Epoch;
        public required long Generation;
        public required int PermissionBits, PermissionRevision, GeometryVersion;
        // Derived from the actual session Media at configure time, never from the configure body.
        public required int CaptureLeaseVersion;
        public readonly Dictionary<string, Queue<Packet>> Queues = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Readers = new(StringComparer.Ordinal);
        public int QueuedBytes;
        public bool Invalid;
        // Binary, coalesced wake: the queue stays authoritative and the consumer re-examines it under the shared lock after each wait.
        public readonly SemaphoreSlim Wake = new(0, 1);
        // Every caller holds sharedStateLock, so releasers are serialized and the count never exceeds 1.
        public void Signal() { if (Wake.CurrentCount == 0) Wake.Release(); }
    }

    public static void Map(WebApplication app, object sharedStateLock,
        Func<HttpContext, string?> authenticate, Func<string, bool> deviceExists,
        Func<string, SessionState?> findSession, Action cleanExpired,
        Func<HttpContext, string, bool> rateAllowed, Func<DateTimeOffset> utcNow, int maxSessions)
    {
        var descriptors = new Dictionary<string, Descriptor>(StringComparer.Ordinal);
        int globalReaders = 0, globalQueuedBytes = 0, inFlightBodyBytes = 0;
        var deviceReaders = new Dictionary<string, int>(StringComparer.Ordinal);
        bool ValidSession(SessionState? s, string sid, long generation) =>
            s is not null && s.SessionId == sid && s.Status == SessionStatus.Connected &&
            s.Media.Active && s.Media.Generation == generation;

        void Prune()
        {
            cleanExpired();
            foreach (var pair in descriptors.ToArray())
            {
                var d = pair.Value; var s = findSession(d.SessionId);
                if (d.Invalid || !ValidSession(s, d.SessionId, d.Generation))
                { descriptors.Remove(pair.Key); globalQueuedBytes -= d.QueuedBytes; d.QueuedBytes = 0; d.Queues.Clear(); d.Invalid = true; d.Signal(); }
            }
        }
        bool Authenticated(HttpContext ctx, out string device, out int failure)
        {
            lock (sharedStateLock)
            {
                device = authenticate(ctx) ?? "";
                if (device.Length == 0 || !deviceExists(device)) { failure = 401; return false; }
                if (!rateAllowed(ctx, device)) { failure = 429; return false; }
                failure = 0; return true;
            }
        }
        bool Participant(Descriptor d, string device) => device == d.Initiator || device == d.Target;
        // Capture fields appear only when the share negotiated captureLeaseVersion 1; legacy shape otherwise.
        object View(Descriptor d) => d.CaptureLeaseVersion == MediaState.CaptureLeaseVersion1
            ? new { sessionId = d.SessionId, initiatorDeviceId = d.Initiator, targetDeviceId = d.Target,
                transportEpoch = d.Epoch, sharingGeneration = d.Generation, permissionBits = d.PermissionBits,
                permissionRevision = d.PermissionRevision, geometryVersion = d.GeometryVersion,
                captureLeaseVersion = MediaState.CaptureLeaseVersion1, captureLeaseMs = MediaState.CaptureLeaseMs, minFrameIntervalMs = MediaState.MinFrameIntervalMs,
                transportKeepaliveVersion = TransportKeepaliveVersion1 }
            : new { sessionId = d.SessionId, initiatorDeviceId = d.Initiator, targetDeviceId = d.Target,
                transportEpoch = d.Epoch, sharingGeneration = d.Generation, permissionBits = d.PermissionBits,
                permissionRevision = d.PermissionRevision, geometryVersion = d.GeometryVersion };

        app.MapPost("/api/v2/sessions/{sid}/opaque/configure", async (HttpContext ctx, string sid) =>
        {
            if (!Authenticated(ctx, out var device, out var authFailure)) return Results.StatusCode(authFailure);
            lock (sharedStateLock)
            {
                Prune(); var before = findSession(sid);
                if (!deviceExists(device) || before is null || before.TargetDeviceId != device || before.Status != SessionStatus.Connected || !before.Media.Active)
                    return Results.StatusCode(403);
            }
            if (ctx.Request.ContentLength is > 4096) return Results.StatusCode(413);
            byte[]? configBytes;
            try { configBytes = await ReadBounded(ctx.Request.Body, 4096, ctx.RequestAborted); }
            catch { return Results.BadRequest(); }
            if (configBytes is null) return Results.StatusCode(413);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 4 }); }
            catch { return Results.BadRequest(); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (!names.Add(property.Name) || property.Name is not ("permissionBits" or "permissionRevision" or "geometryVersion")) return Results.BadRequest();
                if (names.Count != 3 ||
                    !doc.RootElement.TryGetProperty("permissionBits", out var bitsEl) || bitsEl.ValueKind != JsonValueKind.Number || !bitsEl.TryGetInt32(out var bits) ||
                    !doc.RootElement.TryGetProperty("permissionRevision", out var revEl) || revEl.ValueKind != JsonValueKind.Number || !revEl.TryGetInt32(out var rev) ||
                    !doc.RootElement.TryGetProperty("geometryVersion", out var geoEl) || geoEl.ValueKind != JsonValueKind.Number || !geoEl.TryGetInt32(out var geo) ||
                    bits is < 1 or > 31 || (bits & 1) == 0 || rev <= 0 || geo <= 0) return Results.BadRequest();
                lock (sharedStateLock)
                {
                    Prune();
                    var s = findSession(sid);
                    if (!deviceExists(device) || s is null || s.TargetDeviceId != device || !ValidSession(s, sid, s.Media.Generation)) return Results.StatusCode(403);
                    if (!descriptors.ContainsKey(sid) && descriptors.Count >= Math.Min(Math.Max(1, maxSessions), MaxDescriptors)) return Results.StatusCode(429);
                    if (descriptors.Remove(sid, out var old))
                    { globalQueuedBytes -= old.QueuedBytes; old.QueuedBytes = 0; old.Queues.Clear(); old.Invalid = true; old.Signal(); }
                    var d = new Descriptor { SessionId = sid, Initiator = s.InitiatorDeviceId, Target = s.TargetDeviceId,
                        Epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                        Generation = s.Media.Generation, PermissionBits = bits, PermissionRevision = rev, GeometryVersion = geo,
                        CaptureLeaseVersion = s.Media.CaptureLeaseVersion };
                    descriptors[sid] = d;
                    return Results.Json(View(d));
                }
            }
        });

        app.MapGet("/api/v2/sessions/{sid}/opaque/descriptor", (HttpContext ctx, string sid) =>
        {
            if (!Authenticated(ctx, out var device, out var authFailure)) return Results.StatusCode(authFailure);
            lock (sharedStateLock)
            {
                Prune();
                if (!deviceExists(device) || !descriptors.TryGetValue(sid, out var d) || !Participant(d, device)) return Results.NotFound();
                var s = findSession(sid); if (!ValidSession(s, sid, d.Generation)) return Results.NotFound();
                return Results.Json(View(d));
            }
        });

        app.MapPost("/api/v2/sessions/{sid}/opaque/packet", async (HttpContext ctx, string sid) =>
        {
            if (!Authenticated(ctx, out var device, out var authFailure)) return Results.StatusCode(authFailure);
            var epoch = ctx.Request.Query["epoch"].ToString();
            Descriptor? d;
            lock (sharedStateLock)
            {
                Prune();
                if (!deviceExists(device) || !descriptors.TryGetValue(sid, out d) || d.Epoch != epoch || !Participant(d, device)) return Results.NotFound();
                if (!ValidSession(findSession(sid), sid, d.Generation)) return Results.StatusCode(410);
            }
            var bodyLimit = Caps.Max() + 85;
            var max = bodyLimit;
            var bodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly) bodySizeFeature.MaxRequestBodySize = null;
            if (ctx.Request.ContentLength is > 0 && ctx.Request.ContentLength > max) return Results.StatusCode(413);
            int reservation = max + 1;
            lock (sharedStateLock)
            {
                if (globalQueuedBytes + inFlightBodyBytes + reservation > MaxGlobalBytes)
                    return Results.StatusCode(429);
                inFlightBodyBytes += reservation;
            }
            try
            {
            var body = new byte[max + 1]; var count = 0;
            while (count < body.Length)
            { var n = await ctx.Request.Body.ReadAsync(body.AsMemory(count), ctx.RequestAborted); if (n == 0) break; count += n; }
            if (count > max || count < 85) return Results.StatusCode(count > max ? 413 : 400);
            if (!string.Equals(authenticate(ctx), device, StringComparison.Ordinal) || !deviceExists(device)) return Results.Unauthorized();
            var channel = body[0]; if (channel >= Caps.Length || count - 1 > Caps[channel] + 84) return Results.StatusCode(413);
            var length = count - 1; var recipient = device == d!.Target ? d.Initiator : d.Target;
            if ((channel == 1 && device != d.Target) || (channel == 2 && device != d.Initiator)) return Results.StatusCode(403);
            if ((channel == 2 && (d.PermissionBits & 2) == 0) || (channel == 3 && (d.PermissionBits & 4) == 0) ||
                (channel == 4 && (d.PermissionBits & 8) == 0)) return Results.StatusCode(403);
            lock (sharedStateLock)
            {
                Prune();
                if (!deviceExists(device) || !descriptors.TryGetValue(sid, out var current) || !ReferenceEquals(current, d) || d.Invalid ||
                    d.Epoch != epoch || !ValidSession(findSession(sid), sid, d.Generation)) return Results.StatusCode(410);
                if (!d.Queues.TryGetValue(recipient, out var q)) d.Queues[recipient] = q = new Queue<Packet>();
                if (q.Count >= MaxQueueMessages || d.QueuedBytes + length > MaxRecipientBytes || globalQueuedBytes + inFlightBodyBytes + length > MaxGlobalBytes)
                    return Results.StatusCode(429);
                q.Enqueue(new Packet(channel, body.AsSpan(1, length).ToArray()));
                d.QueuedBytes += length; globalQueuedBytes += length; d.Signal();
            }
            return Results.Accepted();
            }
            finally { lock (sharedStateLock) inFlightBodyBytes -= reservation; }
        });

        app.MapGet("/api/v2/sessions/{sid}/opaque/stream", async (HttpContext ctx, string sid) =>
        {
            if (!Authenticated(ctx, out var device, out var authFailure)) { ctx.Response.StatusCode = authFailure; return; }
            Descriptor? d; var epoch = ctx.Request.Query["epoch"].ToString();
            lock (sharedStateLock)
            {
                Prune();
                if (!deviceExists(device) || !descriptors.TryGetValue(sid, out d) || d.Epoch != epoch || !Participant(d, device) ||
                    !ValidSession(findSession(sid), sid, d.Generation)) { ctx.Response.StatusCode = 404; return; }
                if (d.Readers.GetValueOrDefault(device) >= 1) { ctx.Response.StatusCode = 409; return; }
                if (globalReaders >= MaxReaders || deviceReaders.GetValueOrDefault(device) >= 2)
                { ctx.Response.StatusCode = 429; return; }
                globalReaders++; deviceReaders[device] = deviceReaders.GetValueOrDefault(device) + 1;
                d.Readers[device] = d.Readers.GetValueOrDefault(device) + 1;
            }
            var keepalive = d!.CaptureLeaseVersion == MediaState.CaptureLeaseVersion1;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            // Legacy streams keep the 30s total lifetime; keepalive streams end only on abort or the 100ms watcher below.
            if (!keepalive) lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            void Terminate()
            {
                lock (sharedStateLock)
                {
                    if (descriptors.TryGetValue(sid, out var failed) && ReferenceEquals(failed, d))
                    { descriptors.Remove(sid); globalQueuedBytes -= failed.QueuedBytes; failed.QueuedBytes = 0; failed.Queues.Clear(); failed.Invalid = true; failed.Signal(); }
                }
            }
            // Keepalive: each Start/Write/Flush gets its own 5s deadline; any failure or partial write aborts and terminates the descriptor.
            async Task<bool> KeepaliveIo(Func<CancellationToken, Task> op)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(StreamOperationDeadline);
                try { await op(deadline.Token); return true; }
                catch { ctx.Abort(); Terminate(); return false; }
            }
            // This independent watcher also cancels blocked response IO, not only idle queue waits.
            var authorityWatcher = keepalive ? Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        await Task.Delay(100, lifetime.Token);
                        bool valid;
                        lock (sharedStateLock)
                        {
                            Prune();
                            valid = deviceExists(device) && string.Equals(authenticate(ctx), device, StringComparison.Ordinal) &&
                                descriptors.TryGetValue(sid, out var active) && ReferenceEquals(active, d) &&
                                !d.Invalid && d.Epoch == epoch && Participant(d, device) &&
                                ValidSession(findSession(sid), sid, d.Generation);
                        }
                        if (!valid) { lifetime.Cancel(); ctx.Abort(); break; }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch { lifetime.Cancel(); ctx.Abort(); }
            }) : Task.CompletedTask;
            ctx.Response.ContentType = "application/octet-stream";
            try
            {
                if (!keepalive) await ctx.Response.StartAsync(lifetime.Token);
                else if (!await KeepaliveIo(t => ctx.Response.StartAsync(t))) return;
                var lastWrite = System.Diagnostics.Stopwatch.StartNew();
                while (!lifetime.IsCancellationRequested)
                {
                    Packet? p = null;
                    lock (sharedStateLock)
                    {
                        Prune();
                        if (!deviceExists(device) || !string.Equals(authenticate(ctx), device, StringComparison.Ordinal) ||
                            !descriptors.TryGetValue(sid, out var current) || !ReferenceEquals(current, d) || d!.Invalid ||
                            d.Epoch != epoch || !Participant(d, device) || !ValidSession(findSession(sid), sid, d.Generation))
                        { ctx.Abort(); break; }
                        if (d.Queues.TryGetValue(device, out var q) && q.Count > 0) p = q.Peek();
                    }
                    if (p is null)
                    {
                        // Heartbeat: zero-length header only; no queue accounting, sequence, media activity or idle renewal.
                        if (keepalive && lastWrite.Elapsed >= HeartbeatInterval)
                        {
                            if (!await KeepaliveIo(t => ctx.Response.Body.WriteAsync(new byte[4], t).AsTask()) ||
                                !await KeepaliveIo(t => ctx.Response.Body.FlushAsync(t))) break;
                            lastWrite.Restart(); continue;
                        }
                        try { await d!.Wake.WaitAsync(TimeSpan.FromMilliseconds(100), lifetime.Token); } catch (OperationCanceledException) { break; } continue;
                    }
                    var frame = new byte[5 + p.Bytes.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), checked((uint)(1 + p.Bytes.Length)));
                    frame[4] = p.Channel; p.Bytes.CopyTo(frame, 5);
                    if (keepalive)
                    {
                        if (!await KeepaliveIo(t => ctx.Response.Body.WriteAsync(frame, t).AsTask()) ||
                            !await KeepaliveIo(t => ctx.Response.Body.FlushAsync(t))) break;
                        lastWrite.Restart();
                    }
                    else
                    {
                        try { await ctx.Response.Body.WriteAsync(frame, lifetime.Token); await ctx.Response.Body.FlushAsync(lifetime.Token); }
                        catch { Terminate(); break; }
                    }
                    lock (sharedStateLock)
                    {
                        if (!deviceExists(device) || !string.Equals(authenticate(ctx), device, StringComparison.Ordinal) ||
                            !descriptors.TryGetValue(sid, out var current) || !ReferenceEquals(current, d) ||
                            !ValidSession(findSession(sid), sid, d!.Generation)) { ctx.Abort(); break; }
                        if (d.Queues.TryGetValue(device, out var q) && q.Count > 0 && ReferenceEquals(q.Peek(), p))
                        { q.Dequeue(); d.QueuedBytes -= p.Bytes.Length; globalQueuedBytes -= p.Bytes.Length; if (p.Channel != 0) findSession(sid)!.LastMediaActivityAt = utcNow(); }
                    }
                }
            }
            finally
            {
                lifetime.Cancel();
                await authorityWatcher;
                lock (sharedStateLock)
                {
                    globalReaders--;
                    if (deviceReaders.GetValueOrDefault(device) > 1) deviceReaders[device]--; else deviceReaders.Remove(device);
                    if (d!.Readers.GetValueOrDefault(device) > 1) d.Readers[device]--; else d.Readers.Remove(device);
                }
            }
        });
    }
}
