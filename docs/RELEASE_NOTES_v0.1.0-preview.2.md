# RemoteBridge v0.1.0-preview.2 — pre-improvement snapshot / 보완 전 스냅샷

## 한국어

내 서버를 통해 허락받은 Windows PC의 화면을 보고 키보드·마우스로 조작하는 프로그램입니다. 양쪽 새 클라이언트와 v2 서버를 사용하면 상대 PC 보안 키를 직접 확인한 종단 간 암호화 연결을 사용할 수 있습니다. 선택형 화면 직결, 명시적으로 동의한 텍스트·파일 전송, 화질 조절, 최근 PC, 서명된 후보 업데이트 검증, 로컬 진단 로그가 포함됩니다. 설정·비밀번호·개인키는 배포 파일에 포함하지 않습니다.

**이번 릴리즈는 새 보완 작업 직전 상태를 보존한 시험판입니다.** 키를 길게 누를 때 입력 전송 대기750ms 초과로 화면 공유가 중단되는 문제가 남아 있고, 상대에게 정상 종료라고 잘못 표시될 수 있습니다. 간헐 입력 누락·실제 Win7 전체 GUI·장기 WAN/소형 서버 부하도 해결·인증된 상태가 아닙니다. 보안 시간 제한을 늘려 우회하지 않았습니다.

Windows: `RemoteBridge.exe`만 내려받아 별도 폴더에서 실행하세요(.NET Framework4.8 필요). 기존 앱은 정상 종료한 뒤 교체하세요. 새 기능은 양쪽 PC를 같은 버전으로 맞추세요. 설정·키·기존 계정은 삭제하지 마세요. 프로그램 내부 파일 버전은 기존1.0.0.0으로 남아 있으며 이 스냅샷은 릴리즈 태그·아래 해시로 구분합니다. 화면 내 버전 표시와 버전 갱신 체계는 후속 보완에 적용합니다.

Linux 서버 ZIP은 설정·인증서·상태 없는 framework-dependent 서버 파일입니다. .NET10 ASP.NET Core 런타임과 별도 HTTPS·비공개 설정이 필요합니다. [설치 안내](SERVER_SETUP.md)를 먼저 읽고 기존 운영 설정을 덮어쓰지 마세요. 이 게시 작업은 운영 서버를 재시작하거나 재배포하지 않습니다.

## English

RemoteBridge lets authorized users view and control Windows PCs through servers they manage. Updated clients plus the v2 server support default-on peer-verified E2EE, opt-in direct screen transport, consent-based text/file transfer, adaptive JPEG quality, recent peers, signed candidate update verification and bounded local diagnostic logs. No private settings, passwords or private keys are included.

This is the **snapshot immediately before the next improvement cycle**, not a stability fix. Sustained key input can exceed the750ms viewer queue budget and stop sharing; the other peer may incorrectly see a normal-termination message. Intermittent input loss, complete real-Win7 GUI behavior, long WAN sessions and small-server capacity remain unverified or unresolved. Security deadlines have not been relaxed.

Windows requires .NET Framework4.8; close the existing app normally before replacing the standalone EXE. Update both peers. The existing internal file version remains1.0.0.0; identify this snapshot by its release tag and hash. In-app version display and version maintenance follow in the next cycle. The Linux server ZIP requires the .NET10 ASP.NET Core runtime, your own TLS and private configuration; see [setup](SERVER_SETUP.md). Publishing does not change any running server.

## Integrity and verification / 무결성·검증

- Windows: 550912 bytes; SHA256 `CD0A98F5226777B98EDAE208492DBA612A2D62CEAC3BD20C5E9557C5AB8ACBC5`.
- Fresh ConnectionDiagnostics:16 checks passed; exact product hash matched. Matching server publish succeeded; combined11.694s. Prior module tests are reused only in their original scope; see [verification](VERIFICATION.md).
- Authenticode: unsigned. A checksum detects corruption relative to a trusted reference; it does not by itself establish publisher identity.
- No claim of uninterrupted/lossless remote access, independent security audit, Windows7 certification or production readiness. No license grant has been selected.
