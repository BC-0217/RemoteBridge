# RemoteBridge — 리모트브리지

> **Current preview: [v0.1.0-preview.28](https://github.com/BC-0217/RemoteBridge/releases/tag/v0.1.0-preview.28).** Seven UI languages, instant light/dark mode, a separate settings window, softer buttons and negotiated input batching. See [release notes](docs/RELEASE_NOTES_v0.1.0-preview.28.md). This is **not a production release or a guarantee that every screen-sharing disconnect is fixed**.

**Windows remote screen sharing and control, with servers you manage.**

[한국어 설명](README.ko.md) · [Download preview](https://github.com/BC-0217/RemoteBridge/releases) · [Server setup](docs/SERVER_SETUP.md) · [Security](SECURITY.md) · [Known issues & verification](docs/VERIFICATION.md)

> **Early preview — not production-ready.** Use only on authorized computers. Default-on peer-verified encryption requires both updated clients and a compatible v2 server; explicitly selected legacy mode remains server-readable. Opt-in direct screen transport, explicit text/file transfer and signed candidate-update verification have separately scoped tests. No live server was changed for this release. See [features and boundaries](docs/PHASE27_FEATURES.md) and [verification](docs/VERIFICATION.md).

Open **☰ → Settings** for PC access, server and display preferences. Choose Korean (default), English, Japanese, Simplified Chinese, Russian, French or Hindi from the language dropdown. Light/dark mode applies immediately and is remembered.

## What it does

- Standalone Windows client: distribute **RemoteBridge.exe**, without your settings or keys.
- Server creator configures an HTTPS server and creates a **server ID + server PW**.
- Friends join that server using the shared server ID/PW, then connect to each other's **PC IDs**.
- Server-issued 10-digit PC ID; visible 9-letter mixed-case temporary PW that changes on restart, hourly, and after a temporary-password connection.
- Optional per-PC **fixed PW** (8–128 characters), retained through app/server restart as a server-side salted verifier.
- Correct temporary **or** fixed PW → automatic connection when the target's visible automatic-access option is enabled.
- Blank PC PW → target approval dialog. Wrong nonblank PW → rejection, **not** an approval fallback.
- Mouse hold/drag/release, wheel, key down/up and Ctrl/Shift/Alt combinations; stop sharing at the target or close the viewer to end a session.
- Recent successfully connected **peer IDs**, newest first; double-click to fill an ID. No passwords in history.
- Remembers server inputs with Windows DPAPI for the current account.
- Original bounded JPEG-over-HTTPS relay, with explicit optimized mode up to **20fps**; legacy peers remain up to10fps. The new strict E2EE capture-v1 candidate is capped at10fps. Real throughput is lower and depends on the network/PC.

## Requirements

| Component | Requirement |
|---|---|
| Windows client | .NET Framework4.8 and applicable Windows updates |
| Intended Windows versions | Windows7 SP1 /10 /11; **real Windows7 execution is not certified** |
| Server | .NET10 ASP.NET Core runtime; Linux x64 preview package available |
| Building | .NET10 SDK; Windows .NET Framework4.8 reference assemblies for the client |

An EXE-only download is **not a self-contained .NET runtime installer**. The app must be running; it is not a Windows service or a UAC/secure-desktop bypass.

## Quick start: join a friend's server

1. Download `RemoteBridge.exe` from [Releases](https://github.com/BC-0217/RemoteBridge/releases).
2. Ask the **server owner** for the **server ID and server PW**.
3. Open **☰ → Settings → Server Settings → Join the Server** and enter those two values.
4. After registration, the home page displays **your PC ID and temporary PW**.
5. To control a peer, enter **their PC ID** and temporary or fixed PW. Leave the PC PW blank if you want them to approve manually.
6. Both PCs should run the same current preview.

The UI has seven built-in languages; no translation API or API key is required at runtime.

### Set this PC's fixed PW

Open **☰ → Settings → PC Access Settings** and set a fixed password. This password controls access to **this PC**, not membership of the server.

**PW가 맞으면 승인 없이 화면·입력 공유 허용** is enabled by default and visibly disclosed. While enabled and this app is running, a person who knows either PC password can see the screen and control mouse/keyboard **without an approval dialog**. Turn it off to require approval. Changing a PW or this option does not terminate a connected session; use **공유 중지** for that.

## Own a server

Read [SERVER_SETUP.md](docs/SERVER_SETUP.md) before opening ports. **서버 만들기** configures an existing RemoteBridge server; it does not create an AWS account or install a server automatically.

Server address, certificate fingerprint, owner key, server ID/PW and PC ID/PW are different things. Their sources and purpose are explained in the setup guide.

**Registry caveat:** the executable embeds a public trial registry URL/certificate pin/identity. These are public trust configuration, not credentials. A public download does **not** include an invitation, provide free public relay access, promise availability, or automatically publish third-party servers. Independently hosted groups need a trusted registry/bootstrap configuration (or the advanced explicit-profile path). Certificate/registry changes require an explicit trusted update; there is no automatic pin replacement.

## Build

On a Windows development machine with the prerequisites:

```powershell
dotnet build PeerDesk.slnx -c Release --artifacts-path artifacts/build/local
```

Client output:

```text
artifacts/build/local/bin/PeerDesk.Windows/release/RemoteBridge.exe
```

Publish a framework-dependent Linux server:

```powershell
dotnet publish src/PeerDesk.Server/PeerDesk.Server.csproj -c Release -r linux-x64 --self-contained false -o artifacts/publish/linux-x64
```

Internal project/namespace names remain `PeerDesk.*` for compatibility; the product and executable are **RemoteBridge**.

## Verification and limitations

The initial public preview reuses the tested implementation described in [VERIFICATION.md](docs/VERIFICATION.md). Narrow owned-window tests observed about8→15fps locally and8–9fps through a trial AWS relay; these are **not** general performance guarantees.

Known limitations include historical intermittent text-input misses, unverified real Windows7 and separate-physical-PC WAN/long-session reproduction, and no audio or complete IME fidelity. Explicit text/file transfer is not automatic clipboard synchronization. Legacy mode remains server-readable. Input freshness/authority failures can still stop sharing safely; batching is not a lossless-input guarantee. See the current and historical test scopes in the verification guide.

## Repository contents

- `src/PeerDesk.Windows`: net48 WinForms UI, capture, viewer and native input.
- `src/PeerDesk.Server`: HTTPS registration, owner/directory, authorization and media relay.
- `src/PeerDesk.Core`: server-side shared protocol and TLS helpers.
- `tests/`: isolated fixtures and historical acceptance tools; see the verification guide before running them.
- `deploy/`: generic **examples**, not private deployment configuration.

Private keys, live server secrets/state, user settings, screenshots, logs and build outputs are intentionally excluded. Release downloads contain only explicit public assets; never distribute your `%LOCALAPPDATA%\PeerDesk` folder.

## Source provenance and licensing

See [PROVENANCE.md](PROVENANCE.md). The present engine is independently implemented; **it is not RustDesk and does not contain an integrated RustDesk engine**. A RustDesk peer-model file was reviewed as a UX reference, not copied.

**No project license has been selected for this initial public preview.** Public visibility is not an MIT/AGPL grant; no broader reuse rights are asserted here. Framework/dependency terms remain separate. License selection must precede any claim that this repository is licensed open-source.
