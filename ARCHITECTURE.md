# RemoteBridge architecture

## Components

| Project | Runtime | Responsibility |
|---|---|---|
| PeerDesk.Windows | .NET Framework4.8 / WinForms | Independent wire implementation, UI, DPAPI state, GDI capture/JPEG, viewer, Win32 input |
| PeerDesk.Server | .NET10 / ASP.NET Core | Pinned-HTTPS endpoint, registration, owner/directory features, sessions and bounded media relay |
| PeerDesk.Core | .NET10 | Server protocol models, profile validation and TLS helpers |

No RustDesk capture/codec/transport engine is integrated. `RelayUrl` is reserved profile data; implemented media uses `RendezvousUrl`.

## Trust and roles

A public profile contains Version, Name, RendezvousUrl, RelayUrl and CertificateSha256. It contains no enrollment secret. Certificate hostname, validity and exact DER SHA-256 pin are checked, redirects are disabled, and requests/responses are bounded.

The **server creator**, **server participant** and **remote-session target** are different roles. Creating a server ID does not enroll a PC or authorize capture.

- Registration requires a server invitation/join PW and issues a random10-digit PC ID plus a high-entropy bearer.
- Only a target can decide; only an initiator can connect; either participant can revoke.
- Blank PW creates manual Pending, deduplicated by pair and rate limited.
- Nonempty PW must match temporary OR fixed; wrong PW creates no session. A combined target failure budget is shared across both credential types.
- Fixed PW: PBKDF2-SHA256100k, random16-byte salt and32-byte result. Bounded KDF concurrency is outside the shared state lock; registered identity/verifier revision are rechecked before commit.
- A target app advertises a self-authenticated in-memory version1 runtime epoch (60s TTL,20s renewal). Server-generated exact-session authorization kind/revision/epoch is checked at password decision and connect.
- The target also requires its own one-shot grant bound to exact client, peer pair, server binding, session, lifecycle epoch and scope before capture. Approved/Connected snapshots alone are not sharing permission.
- The visible automatic-access option is saved before capability advertisement. OFF/close/reset invalidate pending local authority; unsupported servers fall back to manual approval.
- Fixed PW change/removal revokes related unconnected requests, not already Connected sessions. Use explicit Stop to terminate current sharing.

## Session and media lifecycle

Pending/Approved use absolute TTL. Connected uses an authenticated user-media idle TTL, renewed by consuming a new frame or real user input—not empty polls, repeated frames, upload alone or password rotation.

Sharing target opt-in negotiates stateful input1 and optional mediaOptimizationVersion1. Optimized frame cadence is at least50ms (max20fps); legacy is at least100ms. Limits remain1920x1080/512KiB per JPEG,16MP capture source,32 queued inputs and60 media requests/s per device.

Target input polls run independently at50ms. Optimized frame work may reuse a fully parsed input-lease authorization with the **original request-start clock**, never a renewed timestamp. Proof is bound to client/session/device/URL/full pin/token/monotonic credential revision/local epoch/generation; mutation-in-progress revisions are rejected. It is checked before capture and PUT. No extra fallback GETs are issued; capture attempts are paced, and consecutive expired encodes fail closed.

The server checks role/Connected/Active/generation before consuming media bodies and rechecks after awaited reads under the same lifecycle lock. Cancellation/Stop invalidate the local proof epoch; late replies cannot revive it.

JPEG structure/dimensions are validated before a single viewer decode. Same-size capture avoids an extra full-image copy. Only latest-frame storage is retained. This is not a modern streaming codec.

## Input safety

Viewer preserves key/button edges, coalesces only adjacent moves, and prioritizes release over motion. Monotonic timing avoids signed TickCount uptime failures. Stateful input includes down/up, wheel, releaseAll and holdHeartbeat.

A dedicated guard thread owns the interactive-session mutex and tracks successful injected holds only. Every new actuation needs a fresh request-start authorization of at most100ms. Heartbeat/recent-server watchdogs of750ms protect existing holds; they do not extend authorization for new input. Cleanup releases only keys/buttons owned by this guard.

Stale held-state batches fail closed. Stop cancels/releases before network join; terminal sessions cannot revive. Force-kill, OS shutdown, privilege boundaries and simultaneous physical input do not have guaranteed cleanup. OS-reserved shortcuts, secure desktop and IME composition are not bypassed.

Authenticated typed end reasons distinguish user-stopped/session-ended/error-stopped/expired. Generic409 is not assumed to be normal termination, and cleanup never disguises an earlier error as a user stop.

## Persistence

Device-registry v2 stores ID, bearer SHA-256, registration time, temporary-password generation floor and optional fixed-PW verifier/revision. v1 is accepted and migrated atomically; missing/corrupt production state fails closed. Raw bearer, temporary PW/verifier, fixed PW plaintext, sessions and runtime authority are not persisted.

On restart, identity is retained but a fresh temporary PW is required. This is **not hardware-derived identity**; deleting settings/resetting registration/OS reinstall/VM clone recovery is not guaranteed. State files use a single writer and atomic same-directory replacement, with Linux0700 parent/0600 file. Power-loss durability and administrator anti-rollback are not claimed.

A v1-only old binary cannot read v2 state. Rollback requires a matching private state backup and must account for legitimate changes since that backup.

Windows settings bind credentials to exact endpoint/pin and DPAPI CurrentUser. Remembered inputs are unverified drafts, not authority. Successful recent peers are upserted by exact URL/full pin/peer ID, capped50/64KiB; no PW/token/session ID is history.

## Security limitations

TLS terminates at the server: **screen and input are server-readable**. There is no E2E encryption, installed background service, privileged capture or UAC bypass. See [SECURITY.md](SECURITY.md) and [VERIFICATION.md](docs/VERIFICATION.md), including the unresolved intermittent single-input miss.
