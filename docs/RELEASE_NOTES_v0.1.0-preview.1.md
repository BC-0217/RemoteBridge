# RemoteBridge v0.1.0-preview.1 — initial public preview

> Early preview, not production-ready. Use only with authorized computers and trusted peers.
> **한국어 요약은 아래에 있습니다.**

## User-friendly overview

Run `RemoteBridge.exe` on each Windows PC, join the same trusted server using its **server ID/PW**, then connect using the other PC's **PC ID**.

- Correct temporary **or fixed PC PW**: connect without an approval dialog when the target's visible automatic-access option is enabled.
- Blank PC PW: ask the target to approve.
- Wrong nonblank PW: reject the connection; no approval fallback.
- Set a fixed PC PW in **PC 접근 설정 → 고정 PW 설정**.
- Recent successful connections now display peer IDs, newest first.
- Mouse drag/hold/release, wheel and Ctrl/Shift/Alt combinations are supported.
- Close the viewer or use the target's sharing-stop action to end a session.

**Both PCs should use this preview.** Do not send your personal settings, owner key, server password or SSH key with the EXE.

## Downloads

| Asset | Purpose |
|---|---|
| RemoteBridge.exe | Standalone Windows client; .NET Framework 4.8 must already be installed |
| RemoteBridge.Server.linux-x64.zip | Framework-dependent Linux x64 HTTPS server; requires the .NET 10 ASP.NET Core runtime |
| SHA256SUMS.txt | SHA-256 digests of these downloadable client/server assets |

The Windows EXE is **not Authenticode-signed**. Verify its SHA-256 and obtain it through the trusted repository/release. No runtime installer, automatic server installer or private configuration is included.

The repository also offers GitHub-generated source archives for this tag.

## Detailed implementation changes

- Optional server-side PBKDF2-SHA256 fixed-password verifier/revision; temporary OR fixed verification uses one failed-attempt budget.
- Exact-session server authorization metadata, target runtime epoch, policy-gated one-shot auto approval/share grants; blank input retains manual consent.
- Device registry v2 retains registered IDs/bearer hashes across restart and migrates v1. Back up matching state before rollback to an older v1-only binary.
- DPAPI CurrentUser policy/input persistence; recent Connected-only peer-ID upsert with endpoint/certificate isolation.
- Explicit optimized media negotiation up to20fps, fewer authorization round trips, one viewer decode and fewer same-size frame copies; legacy mode remains compatible.
- Stateful input edge ordering, move coalescing, held-input cleanup/watchdogs and typed normal/error termination feedback.

The engine remains original .NET/GDI/Win32 JPEG-over-HTTPS—not a RustDesk engine integration.

## Verification evidence reused for this release

- Windows solution build:20 projects,0 warnings/errors.
- Fixed access: Windows40 / isolated Linux40 recorded assertions.
- Actual AWS access UI13; separate actual AWS native core27.
- Media authorization20, stateful wire132, guard/client33, recent history10 and saved-input/policy10 groups.
- Narrow480x320 fixture: local about8→15fps; Tokyo relay about8–9fps. These are not general performance/capacity guarantees.

See [full verification scope](https://github.com/BC-0217/RemoteBridge/blob/main/docs/VERIFICATION.md) before interpreting counts.

## Known issues and limitations

- Two AWS performance-sequence runs missed a subsequent single text input. The cause is unresolved. A smaller diagnostic and a separate27-assertion native core run succeeded; this does **not** prove zero input loss.
- Real Windows7 SP1 and separate-physical-PC execution are unverified.
- TLS terminates at the server: **media/input are server-readable, not E2E-encrypted**.
- No audio/file transfer/clipboard synchronization/full IME support, privileged UAC bypass or installed background service.
- No guarantee of cleanup after force-kill/OS shutdown, no production-load certification or availability SLA.
- The public trial registry anchor is not a credential/free relay entitlement. Arbitrary self-hosted servers are not automatically enrolled in that registry.

## Server-owner documentation

[Server setup](https://github.com/BC-0217/RemoteBridge/blob/main/docs/SERVER_SETUP.md) explains server address, SHA-256 fingerprint, owner key, server ID/PW, trusted registry bootstrap, publication approval and device state. The **서버 만들기** page configures an already installed server; it does not provision AWS.

## License status

No project license is selected for this first public preview. Public visibility is not an MIT/AGPL grant. No RustDesk source/binary/assets are included; see [provenance](https://github.com/BC-0217/RemoteBridge/blob/main/PROVENANCE.md).

---

## 한국어 — 사용 방법

1. 각 PC에 .NET Framework 4.8을 준비하고 **RemoteBridge.exe**를 실행합니다.
2. **서버 설정 → 서버 참가**에서 서버 주인에게 받은 **서버 ID/PW**를 입력합니다.
3. 홈에서 **상대 PC ID**와 상대의 **임시 PW 또는 고정 PW**를 입력합니다.
4. PW가 맞고 상대의 자동 접근 옵션이 켜져 있으면 승인창 없이 화면·마우스·키보드 공유가 시작됩니다.
5. PC PW를 비우면 상대에게 승인 요청이 표시되며, 틀린 PW는 연결이 차단됩니다.
6. 고정 PW는 **PC 접근 설정**에서8~128자로 정합니다. **서버 참가 PW와는 별개**입니다.

최근 기록은 연결에 성공한 상대 PC ID 중심으로 표시됩니다. 동일 상대는 한 줄로 갱신되고 더블클릭하면 ID가 채워집니다.

### 서버를 만드는 분

[서버 설치 안내](https://github.com/BC-0217/RemoteBridge/blob/main/docs/SERVER_SETUP.md)를 먼저 읽어 주세요. 서버 주소·인증서 지문·관리키는 서버를 설치한 주인이 준비합니다. 관리키는 서버 참가 PW와 다르며 비공개로 보관해야 합니다.

### 꼭 확인해 주세요

- **초기 시험판**이며, 완성된 상용 원격 프로그램 수준의 안정성을 보장하지 않습니다.
- 성능 측정 후 문자 입력이 누락된2회는 원인 미확정으로 공개합니다.
- 실제 Win7과 별도 물리 PC 두 대 검증은 미완료입니다.
- 종단간 암호화가 아니므로 **서버 주인이 화면/입력을 볼 수 있습니다**.
- EXE에 개인 설정·비밀번호·키를 포함하지 않았습니다.
- 현재 EXE는 **코드 서명되지 않았습니다**. 공개 SHA-256을 확인하세요.
- 프로젝트 재사용 라이선스는 아직 지정하지 않았습니다.
