# Verification, test scope and known issues

## Initial public preview: v0.1.0-preview.1

This release packages the existing phase26 implementation. Results below are **previously executed evidence**, not new tests rerun merely by editing release documentation.

| Check | Recorded result / scope |
|---|---|
| Release solution build |20 projects;0 warnings/errors; about10.42s on the development Windows host |
| Fixed access backend | Windows40 and isolated Tokyo Linux40 assertions; valid temp/fixed OR, blank/manual, wrong PW rejection, roles/epochs, restart, real write failures, v1 migration and actual concurrent replacement |
| Actual UI access flow |13 assertions locally and13 through the AWS relay: fixed-PW setup, correct fixed/temp no-dialog frame delivery, wrong PW no session/capture, blank manual prompt |
| Recent peers |10 checks: successful-only upsert, ordering/bounds, pin/URL isolation, legacy projection, ID-first rendering |
| Saved input/policy |10 groups including DPAPI policy-OFF persistence through saved-input rewriting |
| Optimized media proof |20 checks: strict caps, original100ms expiry, canceled/old epoch and same-ID bearer-replacement/in-flight revision boundaries |
| Stateful media wire |132 assertions including legacy/optimized negotiation, role/auth/generation/sequence and typed stop reasons |
| Guard/client safety |33 checks, including fake-native ownership/cleanup and an actual cross-process mutex test; not33 physical native actions |
| AWS native core acceptance |27 assertions: real owned-window WM_CHAR/key/click, Viewer Ctrl+A/Alt-up path, held mouse drag, wheel, modifiers/releaseAll, stop/no automatic restart |

The native/media fixture captured only its own480x320 HWND via PrintWindow and gated input on the owned foreground HWND/PID. It did **not** capture an unrelated private desktop or establish a two-physical-PC benchmark.

### Narrow performance observations

- Same local fixture:40 unique frames/5.020s (~7.97fps) before optimization vs74/5.003s (~14.79fps) after; sample JPEG9223 bytes.
- Tokyo relay:42–45 rendered frames/about5.01s (~8.38–8.98fps).
-20fps is a negotiated maximum, not observed general WAN/full-desktop throughput. No512MB long-term-load guarantee or RustDesk-equivalent codec claim is made.

### Open issue: intermittent single-text-input miss

Two AWS runs that first waited5s for performance measurement then sent one text character observed no WM_CHAR before a timeout. Sharing stayed active with no reported fatal error; a later sample also checked native foreground. **The cause is not established.**

The same product delivered the remote character in a smaller diagnostic, and a separate full native core run without the performance-wait segment passed27 assertions. This does not prove the intermittent issue fixed. The diagnostic deliberately exited unsuccessfully after measurement and is not counted as a pass. Zero input loss/stutter is not promised.

### Other unverified or unavailable capabilities

- Actual Windows7 SP1 execution, fresh-PC runtime installation and separate physical-PC interoperability.
- Full-desktop/high-resolution performance, long-duration/many-device capacity and power-loss durability.
- E2E encryption, modern video codec transport, audio, file transfer and clipboard synchronization.
- Complete IME/layout fidelity, secure-desktop/UAC handling and OS-reserved shortcuts.
- Cleanup guarantees under force-kill/OS shutdown/UIPI/concurrent physical input.
- Authenticode signing is not asserted; inspect the asset's signature status and verify its published SHA-256.

## Run the maintained narrow checks

Build on Windows with .NET10 SDK and .NET Framework4.8 reference assemblies:

```powershell
dotnet build PeerDesk.slnx -c Release --artifacts-path artifacts/build/local
dotnet artifacts/build/local/bin/FixedAccess/release/FixedAccess.dll
& artifacts/build/local/bin/HistoryRecent/release/HistoryRecent.exe
& artifacts/build/local/bin/PeerDesk.MediaOptimizationSmoke/release/PeerDesk.MediaAcceptance.exe
& artifacts/build/local/bin/PeerDesk.StatefulClientSmoke/release/PeerDesk.MediaAcceptance.exe
& artifacts/build/local/bin/PeerDesk.StatefulInputSmoke/release/PeerDesk.StatefulInputSmoke.exe
```

FixedAccess assertion counts can vary with valid concurrent outcomes and Linux-specific checks. Inspect assertions and exit status, not only a fixed total.

Actual owned-window manual consent/frame test (uses a private ephemeral HTTPS fixture, not a live server):

```powershell
dotnet build tests/PeerDesk.AutoConnectAcceptance/PeerDesk.AutoConnectAcceptance.csproj -c Release --artifacts-path artifacts/build/local
$env:PEERDESK_TEST_NO_NATIVE = '1'
& artifacts/build/local/bin/PeerDesk.MediaAcceptance.Host/release/PeerDesk.MediaAcceptance.Host.exe `
  artifacts/build/local/bin/PeerDesk.AutoConnectAcceptance/release/PeerDesk.MediaAcceptance.exe `
  artifacts/test-evidence/local-manual
# To exercise actual fixed/temp/blank UI flows:
$env:PEERDESK_TEST_ACCESS_AUTH = '1'
& artifacts/build/local/bin/PeerDesk.MediaAcceptance.Host/release/PeerDesk.MediaAcceptance.Host.exe `
  artifacts/build/local/bin/PeerDesk.AutoConnectAcceptance/release/PeerDesk.MediaAcceptance.exe `
  artifacts/test-evidence/local-access
Remove-Item Env:PEERDESK_TEST_ACCESS_AUTH
Remove-Item Env:PEERDESK_TEST_NO_NATIVE
```

Run interactive native tests only in a dedicated desktop/session with the owned test window in foreground. Do not run native fixtures concurrently.

## Historical and live fixtures are not a blanket green suite

The repository retains historical test tools. Some encode older password/session/state assumptions and need adaptation before use with the current protocol. A source file's presence is not a pass claim.

- `HostGuestAcceptance` optional live modes and `ServerFormSmoke/AwsRegistrationChecks.cs` contain development-trial endpoint-specific operational logic, including an explicit destination-settings path. **Do not run these on a live server or your real settings as generic CI smoke.** They require deliberate authorization and isolated inputs.
- `SharingCancellationSmoke/TestProduct` is a historical, separately compiled test copy with the same RemoteBridge assembly name. It is **not the released product**. Never locate release EXEs with an unrestricted recursive filename search.
- Tests can create private ephemeral certificates, registered synthetic devices, input events and evidence under artifacts. These are not distribution assets.
- No public CI campaign is implied by this release.

## Published asset provenance

The Windows EXE is the previously tested product binary, not a TestProduct copy. The Linux ZIP is framework-dependent and contains only server/Core DLLs and public runtime/dependency metadata. Neither includes any private server configuration, certificate private key, settings, invitation or credential.

See release `SHA256SUMS.txt` for the exact downloadable asset digests. Source builds from a different path/toolchain may not be byte-identical; reproducible-build certification is not claimed.
