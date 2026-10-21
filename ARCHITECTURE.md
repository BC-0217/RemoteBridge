# RemoteBridge architecture

## Local input-flow and appearance build (2026-10-21)

- Input flow is an authenticated extension, not a server change: encrypted control offers/ACKs90/91 and typed failure notices/ACKs92/93, bound to the sharing generation. Only acknowledged peers send channel2 binary batches. Old peers retain single-input fallback. Each batch preserves original commands/sequences/repeats, at most32 events/16KiB; decode and capacity validation complete before atomic publication. Focus loss drops only unsent input and retains an in-flight prefix before the existing releaseAll barrier.
- Viewer queue age remains750ms from original enqueue, checked again after the shared send gate immediately before Seal in batch **and** single fallback. Target input authority100ms, capture250ms, receive-age750ms, permission, generation, replay and cancel checks are unchanged. Sealed packets are never retried. Gate wait, batch count, original age, RTT and fixed failure codes are numeric/enum diagnostics; keys/text/coordinates are not logged.
- Typed input failures are best-effort and bounded; broken transport/old peers may not receive them. Independent failures and received typed reasons cannot be overwritten by a later revoke. A generic session-ended message is neutral, not proof of normal user termination.
- Main UI has Home/Sessions; ☰ opens an owned settings window retaining the original controls and handlers. Offline embedded locales:ko/en/ja/zh-CN/ru/fr/hi, Korean default; language/theme dropdowns, immediate light/dark changes, atomic cosmetic settings v2 with v1 compatibility. Identifiers, passwords, aliases and server-provided names are never translated. Fixed read-only status rendering is explicit and never repeats an owner request or credential binding.
- Product version lives in src/PeerDesk.Windows/Version.props. tools/Update-Version.ps1 is called before assembly-info generation: a changed shipped-source/resource fingerprint increments the preview counter once; unchanged rebuilds do not. A5s lock serializes parallel builds; file/product metadata and displayed version agree. BuildVersion.Display reads the assembly informational version. This does not publish GitHub releases, sign updates or change the signed-update rollback floor.

The public pre-improvement snapshot is v0.1.0-preview.2; the local build below is not a new public release. See docs/VERIFICATION.md for exact hashes, evidence and limitations.


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

## Candidate-only local disconnect diagnostics

Control-plane `Send` failures also record fixed operation/stage categories, elapsed/budget milliseconds, deadline versus caller cancellation, and typed HTTP/Web/socket exception facts. The timer still aborts at the original bounded deadline (maximum 10 seconds); a timer abort is displayed as a timeout, not as an unexplained user cancellation. Addresses, raw request paths, credentials and payloads are excluded. `WaitingResponse` includes connection/TLS work and does not identify an individual network subphase. ConnectionDiagnosticsSmoke covers stalled loopback, cancellation, normal/503 responses, title and log privacy; it is not a live server reachability test.

Stable component ID: `win.local-diagnostics`. This is an observation-only boundary in the unreleased candidate, not a change to the public preview's protocol or the media authorization policy.

`Program` starts `DiagnosticLog`; `MediaSharingSession` records the first observed worker error separately from the settled fatal/typed-normal result and later cleanup. Capture expiry stages and input-authorization timeout budgets carry only numeric timing snapshots. `SecureSessionAdapter` records its first underlying failure and direct/relay outcomes; `ViewerForm` and `MainForm` record viewing and cleanup/refinement events. A local random correlation links these components within one process; it is not a device/session ID and is not shared between PCs.

The fixed local log directory is `%LOCALAPPDATA%/PeerDesk/logs`. Typed enum/numeric records exclude exception message/stack text, addresses, credentials, raw device/session IDs, images, input contents, clipboard and file contents. Producers enqueue bounded events; only a background writer performs rotating file IO. Logging failure must not change authorization, exception propagation, cancellation, input cleanup or reconnect behavior. Normal/fatal classification remains owned by the original media lifecycle, never by the logger.

`InputSafety` additionally records fixed guard/body/native causes and pre-release counts/ages without re-evaluating safety predicates. Its first immutable cause is atomically published in memory before failure visibility/release, while event enqueue follows the original cleanup. Input workers name fixed throw sites and retain actual request-start timing; skipped checks are NotEvaluated. Native SendInput counts/error are captured immediately on the calling thread, with partial-call and repair results separate; Win32 values may be stale and are not authoritative causes. Input contents, key/button values, coordinates and HWNDs remain excluded. This instrumentation does not relax the 250/100/750ms policy or certify a physical two-PC fix.

Regression boundaries: preserve existing `Worker` reflection entry points, original 250ms screen/100ms input/750ms pending authority limits, proof checks before/after Seal, and first-independent-fatal precedence over cleanup/normal-close fallout. The diagnostic smoke and the affected capture/input/opaque-close fixtures verify these boundaries; running those fixtures does not certify the user's physical two-PC WAN connection.

## Security limitations

TLS terminates at the server: **screen and input are server-readable**. There is no E2E encryption, installed background service, privileged capture or UAC bypass. See [SECURITY.md](SECURITY.md) and [VERIFICATION.md](docs/VERIFICATION.md), including the unresolved intermittent single-input miss.
