# RemoteBridge Windows client

The product is **RemoteBridge**; `PeerDesk.Windows` is the compatible internal project name.

See the maintained [root README](../../README.md), [한국어 안내](../../README.ko.md), [server setup](../../docs/SERVER_SETUP.md), [architecture](../../ARCHITECTURE.md), and [verification/known issues](../../docs/VERIFICATION.md).

- net48 WinForms; requires .NET Framework4.8 on the target PC.
- Visible10-digit server-issued PC ID and9-letter mixed-case temporary PW; optional8–128-character fixed PW.
- Correct temporary/fixed PW can authorize automatic sharing under the target's visible policy. Blank PW requests manual consent; wrong nonblank PW is rejected.
- Mouse hold/drag/release, wheel, virtual-key down/up and modifiers, with generation/sequence checks and bounded authorization.
- Negotiated optimized JPEG relay up to20fps; old peers remain legacy. Actual throughput is not the protocol upper bound.
- DPAPI CurrentUser settings and successful recent-peer-ID history; do not distribute personal settings.
- TLS is pinned but server-readable, **not E2E encryption**.
- Real Windows7 execution and separate-physical-PC validation remain unverified. The known intermittent input-miss report remains open.

This project is not a background service, UAC bypass, or RustDesk engine integration. It contains no production invitation, owner key, bearer credential or user settings.
