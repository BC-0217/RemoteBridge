using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using PeerDesk.Core;

namespace PeerDesk.Server;

public sealed class MediaState
{
    public const int MinFrameIntervalMs = 100;
    public const int MaxInputDrain = 32;
    public const int MaxInputQueue = 32;
    public const int StatefulInputVersion1 = 1;
    private int _statefulInputVersion;
    public int MediaOptimizationVersion { get; private set; }
    public int FrameIntervalMs { get; private set; } = MinFrameIntervalMs;
    public const int OptimizedFrameIntervalMs = 50;
    public string? StopReason { get; private set; }
    private long _generation;
    private byte[]? _latestFrame;
    private long _latestFrameSeq;
    private long _lastDeliveredFrameSeq;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private long _lastFrameTickMs;
    private readonly ConcurrentQueue<InputEvent> _inputQueue = new();
    private int _inputQueueSize;
    private long _inputSeq;
    public bool Active => Volatile.Read(ref _generation) > 0 && _sharingOn;
    private bool _sharingOn;

    public long Generation => Volatile.Read(ref _generation);
    public long FrameSequence => _latestFrameSeq;
    public long InputSequence => _inputSeq;
    public int InputCount => _inputQueueSize;
    // 0 = legacy tap-only target, 1 = target opted into stateful down/up/wheel/releaseAll/holdHeartbeat.
    public int StatefulInputVersion => _sharingOn ? _statefulInputVersion : 0;
    public bool FrameRateLimited(long nowMs) => _latestFrame != null && nowMs - _lastFrameTickMs < FrameIntervalMs;

    // Called under gate
    public long StartSharing(int statefulInputVersion = 0, int mediaOptimizationVersion = 0)
    {
        if (statefulInputVersion is not (0 or StatefulInputVersion1)) throw new ArgumentOutOfRangeException(nameof(statefulInputVersion));
        if(mediaOptimizationVersion is not (0 or 1) || (mediaOptimizationVersion == 1 && statefulInputVersion != 1)) throw new ArgumentOutOfRangeException(nameof(mediaOptimizationVersion));
        MediaOptimizationVersion = mediaOptimizationVersion;
        FrameIntervalMs = mediaOptimizationVersion == 1 ? OptimizedFrameIntervalMs : MinFrameIntervalMs;
        _sharingOn = true;
        StopReason = null;
        _statefulInputVersion = statefulInputVersion;
        _generation = Interlocked.Increment(ref _generation);
        ClearMedia();
        return _generation;
    }

    // Called under gate
    public long StopSharing(string stopReason = "error")
    {
        if(stopReason is not ("user" or "error")) throw new ArgumentOutOfRangeException(nameof(stopReason));
        StopReason = stopReason;
        _sharingOn = false;
        _statefulInputVersion = 0;
        _generation = Interlocked.Increment(ref _generation);
        ClearMedia();
        return _generation;
    }

    // Called under gate — terminal cleanup (revoke/expire)
    public void Stop()
    {
        if (!_sharingOn && _latestFrame == null) return;
        if (_sharingOn) Interlocked.Increment(ref _generation);
        _sharingOn = false;
        _statefulInputVersion = 0;
        ClearMedia();
    }

    private void ClearMedia()
    {
        _latestFrame = null;
        _latestFrameSeq = 0;
        _lastDeliveredFrameSeq = 0;
        _latestFrameWidth = 0;
        _latestFrameHeight = 0;
        _lastFrameTickMs = 0;
        while (_inputQueue.TryDequeue(out _)) { }
        _inputQueueSize = 0;
        _inputSeq = 0;
    }

    // Under gate
    public bool TryPutFrame(long generation, long seq, int width, int height, byte[] data, long nowMs)
    {
        if (!_sharingOn || _generation != generation) return false;
        if (seq <= _latestFrameSeq) return false;
        if (nowMs - _lastFrameTickMs < FrameIntervalMs && _latestFrame != null) return false;
        _latestFrame = data;
        _latestFrameSeq = seq;
        _latestFrameWidth = width;
        _latestFrameHeight = height;
        _lastFrameTickMs = nowMs;
        return true;
    }

    // Under gate: a repeated GET of the same frame is not new media activity.
    public bool MarkFrameDelivered(long sequence)
    {
        if (sequence <= _lastDeliveredFrameSeq) return false;
        _lastDeliveredFrameSeq = sequence;
        return true;
    }

    // Under gate — returns copy of reference (immutable bytes)
    public (byte[]? data, long seq, int width, int height) GetFrame()
        => (_latestFrame, _latestFrameSeq, _latestFrameWidth, _latestFrameHeight);

    // Under gate — frame, generation and stateful capability observed in one snapshot.
    public (byte[]? data, long seq, int width, int height, long generation, int statefulInputVersion) GetFrameSnapshot()
        => (_latestFrame, _latestFrameSeq, _latestFrameWidth, _latestFrameHeight, _generation, StatefulInputVersion);

    // Under gate
    public bool TryEnqueueInput(long generation, long seq, InputEvent ev)
    {
        if (!_sharingOn || _generation != generation) return false;
        if (seq <= _inputSeq) return false;
        if (ev.Kind == "releaseAll")
        {
            if (_statefulInputVersion != StatefulInputVersion1) return false;
            // Safety barrier: nothing queued before the release may be actuated after it.
            while (_inputQueue.TryDequeue(out _)) { }
            _inputQueueSize = 0;
        }
        else if (_inputQueueSize >= MaxInputQueue) return false;
        _inputSeq = seq;
        _inputQueue.Enqueue(ev);
        Interlocked.Increment(ref _inputQueueSize);
        return true;
    }

    // Under gate — drains queue
    public (long generation, List<InputEvent> events) DrainInput()
    {
        var list = new List<InputEvent>();
        while (list.Count < MaxInputDrain && _inputQueue.TryDequeue(out var ev))
        {
            list.Add(ev);
            Interlocked.Decrement(ref _inputQueueSize);
        }
        return (_generation, list);
    }
}

public sealed class InputEvent
{
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public string Kind { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Button { get; init; }
    public int KeyCode { get; init; }
    public string Text { get; init; } = "";
    public int WheelDelta { get; init; }

    // Liveness/safety control events are not user media activity and never renew idle TTL.
    internal bool RenewsIdle => Kind is not ("holdHeartbeat" or "releaseAll");
}

public static class MediaRelay
{
    private const int InputPollIntervalMsStateful = 50;
    private const int MaxWheelDelta = 1200;
    private static readonly JsonSerializerOptions StrictBody = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private const int MaxFrameBytes = 524288;
    private const int MediaRateWindowMs = 1000;
    private const int MediaRateMax = 60;

    // Per-device media rate: deviceId -> (windowStartMs, count)


    public static void Map(
        WebApplication app,
        object gate,
        ConcurrentDictionary<string, SessionState> sessions,
        Func<HttpContext, string?> authenticate,
        Action cleanExpired,
        IClock clock)
    {
        var mediaRate = new ConcurrentDictionary<string, (long windowStart, int count)>();
        var group = app.MapGroup("/api/v1/sessions/{sessionId}");

        // POST /sharing
        group.MapPost("/sharing", async (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);
            lock (gate) {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var candidate)) return Results.NotFound();
                if (candidate.TargetDeviceId != deviceId) return Results.StatusCode(403);
                if (candidate.Status != "Connected") return Results.Conflict();
            }
            SharingRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<SharingRequest>(StrictBody); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Results.StatusCode(413); }
            catch { return Results.BadRequest(); }
            if (req == null) return Results.BadRequest();
            if (!TryGetStatefulVersion(req, out var statefulVersion)) return Results.BadRequest();
            if(req.StopReason is not (null or "user" or "error") || (req.Enabled && req.StopReason != null)) return Results.BadRequest();
            int mediaVersion = 0;
            if(req.MediaOptimizationVersion.ValueKind != JsonValueKind.Undefined && (req.MediaOptimizationVersion.ValueKind != JsonValueKind.Number || !req.MediaOptimizationVersion.TryGetInt32(out mediaVersion) || mediaVersion is not (0 or 1))) return Results.BadRequest();
            if(mediaVersion == 1 && (!req.Enabled || statefulVersion != 1)) return Results.BadRequest();

            long generation; int grantedVersion = 0;
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.Status != "Connected")
                    return Results.Conflict();
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                generation = req.Enabled ? session.Media.StartSharing(statefulVersion, mediaVersion) : session.Media.StopSharing(req.StopReason ?? "error");
                grantedVersion = session.Media.StatefulInputVersion;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (grantedVersion == MediaState.StatefulInputVersion1 && mediaVersion == 1)
                return Results.Ok(new { generation, minFrameIntervalMs = MediaState.OptimizedFrameIntervalMs, inputLeasePollSupported = true, statefulInputVersion = grantedVersion, inputPollIntervalMs = InputPollIntervalMsStateful, mediaOptimizationVersion = 1 });
            if (grantedVersion == MediaState.StatefulInputVersion1)
                return Results.Ok(new { generation, minFrameIntervalMs = MediaState.MinFrameIntervalMs, inputLeasePollSupported = true,
                    statefulInputVersion = grantedVersion, inputPollIntervalMs = InputPollIntervalMsStateful });
            return Results.Ok(new { generation, minFrameIntervalMs = MediaState.MinFrameIntervalMs, inputLeasePollSupported = true });
        });

        // PUT /frame
        group.MapPut("/frame", async (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            lock (gate) {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var candidate)) return Results.NotFound();
                if (candidate.TargetDeviceId != deviceId) return Results.StatusCode(403);
                if (candidate.Status != "Connected" || !candidate.Media.Active) return MediaEnded(ctx, candidate);
            }
            if (!TryParseFrameHeaders(ctx, out var generation, out var seq, out var width, out var height))
                return Results.BadRequest();
            if (width <= 0 || width > 1920 || height <= 0 || height > 1080)
                return Results.BadRequest();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session.Status != "Connected" || !session.Media.Active)
                    return MediaEnded(ctx, session);
                if (session.Media.Generation != generation)
                    return Results.Conflict();
            }

            // Override max body size for frame
            var bodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature != null && !bodySizeFeature.IsReadOnly)
                bodySizeFeature.MaxRequestBodySize = null; // Application enforces exact bounded chunked limit below.

            if (ctx.Request.ContentType == null ||
                !ctx.Request.ContentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest();

            // Check content-length if provided
            if (ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > MaxFrameBytes)
                return Results.StatusCode(413);

            byte[] data;
            try
            {
                using var ms = new MemoryStream(MaxFrameBytes);
                var buf = ArrayPool<byte>.Shared.Rent(16384);
                try
                {
                    int total = 0;
                    int read;
                    while ((read = await ctx.Request.Body.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, MaxFrameBytes + 1 - total)), ctx.RequestAborted)) > 0)
                    {
                        total += read;
                        if (total > MaxFrameBytes)
                            return Results.StatusCode(413);
                        ms.Write(buf, 0, read);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
                data = ms.ToArray();
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Results.StatusCode(413); }
            catch { return Results.BadRequest(); }

            if (!ValidateJpeg(data, width, height))
                return Results.BadRequest();

            long nowMs = clock.UtcNow.ToUnixTimeMilliseconds();
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session2))
                    return Results.NotFound();
                if (session2.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session2.Status != "Connected" || !session2.Media.Active)
                    return MediaEnded(ctx, session2);
                if (session2.Media.Generation != generation || seq <= session2.Media.FrameSequence) return Results.Conflict();
                if (session2.Media.FrameRateLimited(nowMs)) return Results.StatusCode(429);
                if (!session2.Media.TryPutFrame(generation, seq, width, height, data, nowMs))
                    return Results.Conflict();
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.NoContent();
        });

        // GET /frame
        group.MapGet("/frame", (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            byte[]? data;
            long seq, frameGeneration; int width, height, frameStatefulVersion, frameMediaVersion;
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.InitiatorDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session.Status != "Connected" || !session.Media.Active)
                    return MediaEnded(ctx, session);
                (data, seq, width, height, frameGeneration, frameStatefulVersion) = session.Media.GetFrameSnapshot();
                frameMediaVersion = session.Media.MediaOptimizationVersion;
                if (data != null && session.Media.MarkFrameDelivered(seq)) session.LastMediaActivityAt = clock.UtcNow;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (data == null) return Results.StatusCode(204);
            ctx.Response.Headers["X-Share-Generation"] = frameGeneration.ToString();
            ctx.Response.Headers["X-Frame-Sequence"] = seq.ToString();
            ctx.Response.Headers["X-Frame-Width"] = width.ToString();
            ctx.Response.Headers["X-Frame-Height"] = height.ToString();
            if (frameStatefulVersion == MediaState.StatefulInputVersion1)
                ctx.Response.Headers["X-Stateful-Input-Version"] = "1";
            if(frameMediaVersion == 1) ctx.Response.Headers["X-Media-Optimization-Version"] = "1";
            return Results.Bytes(data, "image/jpeg");
        });

        // POST /input
        group.MapPost("/input", async (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            lock (gate) {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var candidate)) return Results.NotFound();
                if (candidate.InitiatorDeviceId != deviceId) return Results.StatusCode(403);
                if (candidate.Status != "Connected" || !candidate.Media.Active) return MediaEnded(ctx, candidate);
            }
            InputRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<InputRequest>(StrictBody); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Results.StatusCode(413); }
            catch { return Results.BadRequest(); }
            if (req == null) return Results.BadRequest();

            if (!ValidateInput(req)) return Results.BadRequest();

            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.Status != "Connected")
                    return Results.Conflict();
                if (session.InitiatorDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (!session.Media.Active)
                    return Results.Conflict();
                // Stateful kinds are only valid for a target that opted in for this exact sharing generation;
                // a legacy target sees them exactly as an old server would (unknown kind).
                if (IsStatefulKind(req.Kind) && session.Media.StatefulInputVersion != MediaState.StatefulInputVersion1)
                    return Results.BadRequest();
                if (session.Media.Generation != req.Generation || req.Sequence <= session.Media.InputSequence) return Results.Conflict();
                if (req.Kind != "releaseAll" && session.Media.InputCount >= MediaState.MaxInputQueue) return Results.StatusCode(429);
                var ev = new InputEvent
                {
                    Generation = req.Generation,
                    Sequence = req.Sequence,
                    Kind = req.Kind,
                    X = req.X, Y = req.Y,
                    Button = req.Button,
                    KeyCode = req.KeyCode,
                    Text = req.Text ?? "",
                    WheelDelta = req.WheelDelta
                };
                if (!session.Media.TryEnqueueInput(req.Generation, req.Sequence, ev))
                    return Results.Conflict();
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.NoContent();
        });

        // GET /input
        group.MapGet("/input", (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            long gen; List<InputEvent> events;
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session.Status != "Connected" || !session.Media.Active)
                    return MediaEnded(ctx, session);
                (gen, events) = session.Media.DrainInput();
                if (events.Exists(e => e.RenewsIdle)) session.LastMediaActivityAt = clock.UtcNow;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { generation = gen, events });
        });

        // GET /lease
        group.MapGet("/lease", (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            long gen;
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session.Status != "Connected" || !session.Media.Active)
                    return MediaEnded(ctx, session);
                gen = session.Media.Generation;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            // Note: no instantaneous remote revoke guarantee; client must check generation before actuation.
            return Results.Ok(new { generation = gen, leaseMs = 100 });
        });

        // GET /input-lease — combined lease + bounded input drain under the same target ACL as GET /input.
        group.MapGet("/input-lease", (HttpContext ctx, string sessionId) =>
        {
            var deviceId = authenticate(ctx);
            if (deviceId == null) return Results.Unauthorized();

            if (!CheckMediaRate(deviceId, clock, mediaRate)) return Results.StatusCode(429);

            long gen; List<InputEvent> events;
            lock (gate)
            {
                cleanExpired();
                if (!sessions.TryGetValue(sessionId, out var session))
                    return Results.NotFound();
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);
                if (session.Status != "Connected" || !session.Media.Active)
                    return MediaEnded(ctx, session);
                (gen, events) = session.Media.DrainInput();
                if (events.Exists(e => e.RenewsIdle)) session.LastMediaActivityAt = clock.UtcNow;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            // Note: no instantaneous remote revoke guarantee; client must check generation before actuation.
            return Results.Ok(new { generation = gen, leaseMs = 100, events });
        });
    }

    // Only called after the endpoint's participant-role ACL, while holding the lifecycle gate.
    // An unstarted share / stale generation must not masquerade as a user-initiated stop.
    private static IResult MediaEnded(HttpContext ctx, SessionState session)
    {
        string? reason = session.Status switch {
            "Revoked" => "session-ended", "Expired" => "expired",
            "Connected" when !session.Media.Active && session.Media.StopReason == "user" => "user-stopped",
            "Connected" when !session.Media.Active && session.Media.StopReason == "error" => "error-stopped",
            _ => null
        };
        if(reason != null) ctx.Response.Headers["X-PeerDesk-Media-End"] = reason;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.Conflict();
    }

    private static bool TryParseFrameHeaders(HttpContext ctx, out long generation, out long seq, out int width, out int height)
    {
        generation = 0; seq = 0; width = 0; height = 0;
        return ctx.Request.Headers.TryGetValue("X-Share-Generation", out var gv) && long.TryParse(gv, out generation) &&
               ctx.Request.Headers.TryGetValue("X-Frame-Sequence", out var sv) && long.TryParse(sv, out seq) && seq > 0 &&
               ctx.Request.Headers.TryGetValue("X-Frame-Width", out var wv) && int.TryParse(wv, out width) &&
               ctx.Request.Headers.TryGetValue("X-Frame-Height", out var hv) && int.TryParse(hv, out height);
    }

    private static bool ValidateJpeg(byte[] data, int declaredWidth, int declaredHeight)
    {
        if (data.Length < 4 || data[0] != 255 || data[1] != 216 ||
            data[^2] != 255 || data[^1] != 217) return false;
        int i = 2;
        while (i < data.Length - 2) {
            if (data[i++] != 255) return false;
            while (i < data.Length && data[i] == 255) i++;
            if (i >= data.Length) return false;
            byte marker = data[i++];
            if (marker == 0xDA || marker == 0xD9 || marker == 0) return false;
            if (marker == 1 || marker is >= 0xD0 and <= 0xD8) continue;
            if (i + 2 > data.Length) return false;
            int length = (data[i] << 8) | data[i + 1];
            if (length < 2 || i + length > data.Length) return false;
            bool sof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (sof) {
                if (length < 8 || i + 7 >= data.Length) return false;
                int height = (data[i + 3] << 8) | data[i + 4];
                int width = (data[i + 5] << 8) | data[i + 6];
                int components = data[i + 7];
                return components >= 1 && components <= 4 && length == 8 + components * 3 &&
                    width == declaredWidth && height == declaredHeight &&
                    width > 0 && width <= 1920 && height > 0 && height <= 1080;
            }
            i += length;
        }
        return false;
    }

    private static bool TryGetStatefulVersion(SharingRequest req, out int version)
    {
        version = 0;
        var v = req.StatefulInputVersion;
        if (v.ValueKind == JsonValueKind.Undefined) return true;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version)) return false;
        if (version is 0 or MediaState.StatefulInputVersion1) return true;
        version = 0;
        return false;
    }

    private static bool IsStatefulKind(string kind)
        => kind is "mouseDown" or "mouseUp" or "keyDown" or "keyUp" or "wheel" or "releaseAll" or "holdHeartbeat";

    private static bool ValidateInput(InputRequest req)
    {
        if (req.Sequence <= 0) return false;
        bool inBounds = req.X >= 0 && req.X <= 65535 && req.Y >= 0 && req.Y <= 65535;
        bool noPoint = req.X == 0 && req.Y == 0;
        bool noText = string.IsNullOrEmpty(req.Text);
        if (req.Kind != "wheel" && req.WheelDelta != 0) return false;
        return req.Kind switch
        {
            "mouseDown" or "mouseUp" => inBounds && req.Button >= 0 && req.Button <= 2 && req.KeyCode == 0 && noText,
            "keyDown" or "keyUp" => req.KeyCode >= 1 && req.KeyCode <= 254 && noPoint && req.Button == 0 && noText,
            "wheel" => inBounds && req.WheelDelta != 0 && req.WheelDelta >= -MaxWheelDelta && req.WheelDelta <= MaxWheelDelta &&
                       req.Button == 0 && req.KeyCode == 0 && noText,
            "releaseAll" or "holdHeartbeat" => noPoint && req.Button == 0 && req.KeyCode == 0 && noText,
            "move" => req.X >= 0 && req.X <= 65535 && req.Y >= 0 && req.Y <= 65535,
            "click" => req.X >= 0 && req.X <= 65535 && req.Y >= 0 && req.Y <= 65535 &&
                       req.Button >= 0 && req.Button <= 2,
            "key" => req.KeyCode >= 1 && req.KeyCode <= 254,
            "text" => req.Text != null && req.Text.Length == 1 &&
                      !char.IsSurrogate(req.Text[0]) && !char.IsControl(req.Text[0]),
            _ => false
        };
    }

    private static bool CheckMediaRate(string deviceId, IClock clock, ConcurrentDictionary<string, (long windowStart, int count)> mediaRate)
    {
        long nowMs = clock.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            if (!mediaRate.TryGetValue(deviceId, out var cur))
            {
                // Only allow known devices; if not in map, add with count=1
                mediaRate[deviceId] = (nowMs, 1);
                return true;
            }
            long elapsed = nowMs - cur.windowStart;
            long newStart; int newCount;
            if (elapsed >= MediaRateWindowMs)
                (newStart, newCount) = (nowMs, 1);
            else if (cur.count >= MediaRateMax)
                return false;
            else
                (newStart, newCount) = (cur.windowStart, cur.count + 1);
            if (mediaRate.TryUpdate(deviceId, (newStart, newCount), cur))
                return true;
        }
    }

    private sealed class SharingRequest
    {
        public bool Enabled { get; set; }
        public string? StopReason { get; set; }
        // Undefined = absent (legacy version 0); any present non-integer value is rejected.
        public JsonElement StatefulInputVersion { get; set; }
        public JsonElement MediaOptimizationVersion { get; set; }
    }
    private sealed class InputRequest
    {
        public long Generation { get; set; }
        public long Sequence { get; set; }
        public string Kind { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }
        public int KeyCode { get; set; }
        public string? Text { get; set; }
        public int WheelDelta { get; set; }
    }
}
