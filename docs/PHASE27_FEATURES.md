# Phase27 기능 확장 후보 — 도쿄 AWS·로컬 EXE 반영, 공개 릴리즈는 미교체

> **2026-10-21 갱신:** 보완 전 로컬 실행 파일 `CD0A98F5…`와 현재 소스를 `v0.1.0-preview.2`로 공개 보존했습니다. [릴리즈 설명](RELEASE_NOTES_v0.1.0-preview.2.md)과 [현재 검증·알려진 문제](VERIFICATION.md)를 먼저 확인하세요. 아래 ‘현재 후보/공개 미교체’와 해시는 각 과거 검증 시점의 기록이며 최신 버전 표기가 아닙니다.

이 문서는 **후보 빌드**와 기존 공개 `v0.1.0-preview.1`을 구분합니다. 사용자 승인으로 도쿄 AWS의 RemoteBridge 서비스만 phase27로 업데이트했습니다. 공개 릴리즈는 유지하고 로컬 phase7 실행 파일과 phase27 후보는 아래 검증 산출물로 동기화했습니다. 새 암호화 연결에는 **양쪽 새 클라이언트와 v2 서버**가 필요합니다. 구 서버에서 실패했다고 암호화를 자동으로 끄지 않습니다.

## 입력 중단 원인 진단 보강 — 2026-10-18 후속

현재 로컬 EXE SHA-256: `F72B8C9905FD49BBFC23C3EEDBC38AFC5E6919ED52F6EE874F544D2AC8EFA2B9`, MVID `f759ccee-9378-4372-b09f-7f93904f903e`. 실제 검증한 `artifacts/build/input-diagnostics-2-final/bin/PeerDesk.InputDiagnosticsSmoke/release/RemoteBridge.exe`를 지정 phase7/release 및 phase27-candidate에 복사하고 양쪽 해시를 대조했습니다. 직전 `75E163…` 두 EXE는 `artifacts/distribution/pre-input-diagnostics-20261018-1747/{phase7,candidate}/RemoteBridge.exe`에 보존했습니다.

사용자 제공 양 PC 로그에서는 직접 전송이 실제로 꺼져 있었고, 공유측 시작 약20초 뒤 Input worker의 InvalidOperation fatal 뒤 Frame 취소가 관측됐습니다. guard/권한 만료/컨텍스트/native 중 어느 세부 분기인지는 기존 로그에 없었습니다. 이번 변경은 **그 진단 공백을 보강한 것이며 끊김 해결은 아닙니다**. 아래 사용법대로 양 PC에서 같은 새 EXE를 다시 실행하고 재현 후 JSONL을 수집합니다.

- 입력 worker의 고정 실패 지점·context 원인, 실제 poll/lease/대기 경과·한도·batch index/count를 기록합니다. 평가되지 않은 조건은 NotEvaluated 또는 -1로 구분하며 진단용 안전 조건 재호출은 하지 않습니다.
- guard의 heartbeat/서버 lease/scope/본문 예외/native 실패/queue 종료를 구분합니다. 해제 전 held 수는 guard thread에서만 보존하고, 최초 원인 snapshot은 메모리에 먼저 원자 게시하되 기존 해제/정리 뒤에 이벤트를 넣습니다. 정상 Dispose 기록은 fatal 판정이 아닙니다. Join timeout·OS/강제 종료의 해제 보증은 추가하지 않았습니다.
- SendInput 직후 같은 thread에서 requested/sent/숫자 Win32 오류를 보존하고 partial 호출과 후속 복구 결과를 분리합니다. 오류값은 net48에서 stale일 수 있어 `ErrorMayBeStale=true / AuthoritativeCause=false`로 기록합니다. 0이나 특정 코드만으로 성공·UIPI 원인을 단정하지 않습니다. 실제 키/버튼값·문자·좌표·HWND는 남기지 않습니다.
- 기존 화면250ms/입력100ms/hold·대기750ms, 승인·암호화·취소·입력 해제·오류 전파는 유지했습니다. 서버·공개 릴리즈·사용자 실행 프로세스는 변경하지 않았습니다.

최종 Release 여섯 fixture build/직접 실행 모두 exit0: 신규 InputDiagnostics67조건(4.717초, actual poll worker LeaseMidBatch/ContextBeforePoll/GuardPreFailed와 JSON 이벤트, fake native·watchdog·원인 경합·queue 종료·privacy), InputBinding14, CaptureProof60, OpaqueClose20. StatefulClient와 DiagnosticLog는 각33/63개의 PASS 출력행 및 전체 성공을 확인했으며 이를 별도 formal assertion 수로 확대하지 않습니다. 여섯 fixture 인접 EXE 해시는 위 최종 파일과 같습니다. root가 전체 핵심 루틴을 재검토하고 정확 EXE logger Initialize/Shutdown의 MVID 일치 JSONL2행을 별도 확인했습니다(PID31124); Program.Main/실제 공유 세션 시험은 아닙니다.

UI 소스는 바뀌지 않아 아래 이전 UI243의 해당 범위 증거를 재사용하며 새 바이너리 UI 시험으로 세지 않습니다. 실제 Win7·실 SendInput/UIPI·물리2PC 재현·secure worker mid-batch/ContextAfterPoll 등 나머지 site 런타임은 미검증입니다. 기존 로그 회전·privacy·누락 한계도 그대로입니다.

준비·구현은 시간 목표를 초과했습니다. 실제 Opus 호출은 초기10분40.7초/후속8분21.2초/마지막1분57.9초였고, 최종 build→runtime 누적 약38초(작업자 메타데이터 포함 검증 약46초)입니다. 첫 BaseIntermediateOutputPath 빌드는 중복 AssemblyAttributes로 실패해 경로 설정을 교정했고, 최종은 격리 artifacts-path 및 실제 시험한 인접 EXE를 선정했습니다. root probe의 PSObject 인자 오류는 명시 string 변환으로 한 번 교정 후 성공했으며 제품 결함이 아닙니다.

## 이전 1차 화면 공유 중단 진단 로그 — 2026-10-18

메인창은 남고 화면 공유만 끊기는 사용자 보고를 조사하기 위한 **관측 전용** 변경입니다. 직접 전송을 꺼도 재현됐다는 사용자 보고가 있으나, 이번 변경이 끊김 원인을 해결했다는 뜻은 아닙니다. 기존 화면250ms·입력100ms·미결합 입력750ms, 암호화·승인·정상 종료 구분은 바꾸지 않았습니다.

현재 로컬 EXE SHA-256: `75E163BA1B559D1D77169CE963711F7A0314CA3B6215D392059B9C260FEAD333` (MVID `14e76e41-89b0-4acb-8d54-8c511e7bddb7`). 지정 phase7/release와 phase27-candidate를 같은 파일로 동기화했습니다. 이전 `0E3E0CD2…` 두 파일은 `artifacts/distribution/pre-diagnostic-20261018/{phase7,candidate}/RemoteBridge.exe`에 보존했습니다. 아래의 이전 UI·미디어 해시는 역사적 시험 산출물입니다. 서버·공개 GitHub 릴리즈는 변경하지 않았습니다.

사용법:
1. **양쪽 PC에서 기존 앱을 종료하고 같은 새 EXE를 실행**합니다.
2. 끊김을 재현한 뒤 **화면·편의 → 진단 로그 폴더 열기**를 누릅니다.
3. 각 PC의 `%LOCALAPPDATA%/PeerDesk/logs/remotebridge-diag-0.jsonl`~`2.jsonl` 중 존재하는 파일을 확인합니다. 두 PC의 로그가 필요하며 자동 업로드는 하지 않습니다.

로그는 최초 관측 오류 / 최종 정상·오류 판정 / 후속 정리를 분리합니다. 캡처 만료 단계·응답/캡처/인코딩 시간·입력 권한 대기 예산, 어댑터 최초 오류·직접 연결/중계 전환을 enum·수치로 남깁니다. 예외 메시지·스택 원문, URL/IP·PC/세션 ID·비밀번호·토큰·키·화면·키 입력·클립보드·파일 내용은 기록하지 않습니다. correlation은 해당 PC 프로세스 안에서만 쓰는 난수이며 양 PC의 공통 세션 ID가 아닙니다.

생산 경로는 bounded queue에만 넣고, 별도 writer가 1MiB 파일3개(합계 최대3MiB)로 회전합니다. 다른 프로세스/로그온 세션에서도 같은 폴더의 0바이트 `remotebridge-diag.lock`을 배타적으로 열어 회전을 조정합니다. 경합 시 기다리지 않고 제한된 우선 이벤트만 잠시 보관하며, 초과/IO 실패는 드롭 카운터로 구분합니다. 로그 쓰기가 계속 불가능하거나 강제 종료하면 기록이 누락될 수 있습니다. 정상 종료 flush는 최대1500ms입니다.

검증:
- 최종 Release 진단 smoke66조건(실제 Worker→settled 이벤트, 최신/직전 RTT 키 분리, sentinel 미출력, 32필드 복사/중복 키, 큐·회전, 배타 잠금·사용 불가 경로·3프로세스 동시 기록), OpaqueClose20, CaptureProof60 통과. 진단 런타임4.792초/종료 구분3.730초/권한0.742초는 별도 범위입니다.
- root가 정확 배포 EXE를 UI fixture 옆에 복사·SHA 대조한 뒤 UI243조건 통과(PID28404). 760/900 창·기본/1.5배 글꼴이며 실제 OS150%DPI 시험은 아닙니다. 새 PNG는 `artifacts/build/diagnostic-root-ui/artifacts/build/phase28-ui/`에 있습니다. root 별도 제품 logger 초기화/종료에서 위 MVID의 실제 JSONL2행을 확인했습니다(PID3932); 실제 공유 세션 재현은 아닙니다.
- 초기 SendGuard46/MediaOptimization20은 후속 helper·동등 catch 정리 전 실행한 범위의 증거를 재사용하며 새 실행으로 세지 않습니다.
- 기존 Worker reflection 진입점 보존. 진단용 false-filter 뒤에는 이미 원래 cleanup catch가 있었으므로 cleanup 회귀가 있었다고 단정한 초기 검토는 철회했습니다. 최종본은 같은 정리 동작의 단일 catch/rethrow로 단순화했습니다.
- 실제 별도 물리2PC 끊김 원인·실제 공유 로그, 실제 ACL 거부/다른 Windows 로그온 세션의 동시 실행, Win7 전체 GUI는 미검증입니다. 사용 불가 경로 시험을 ACL 거부 시험으로 부르지 않습니다.

준비·구현·검토는 30초 목표를 넘겼습니다. 첫 위임은 PowerShell5 native 인자에서 한글 경로가 손상되어 read 실패/편집0 상태를 확인하고 ASCII 지시로 복구했습니다. 실제 구현 호출15분35.6초·정확 후속 보정6분9.2초이며 이 준비/구현 시간을 짧은 빌드·런타임 시간으로 대신하지 않습니다.

## 이전 화면·편의 UI 후속 수정 — 2026-10-18

창 폭에 맞춘 줄바꿈과 세로 스크롤을 적용하고 체크 항목을 쉬운 말로 정리했습니다. 예: ‘종단 간 암호화’ → ‘화면과 입력 내용을 안전하게 주고받기’, ‘확인한 상대 PC’, ‘내 화면을 보내는 화질’. 기존 보안 기본값·승인·저장·연결 동작과 AWS는 변경하지 않았습니다.

이전 로컬 UI 후보 SHA-256: `0E3E0CD2A753C774A8D802A6EA257FA8848BB40A9E7A9739343D2FE0BA09BD70`. 아래 AWS 시험의 정확한 산출물 `42E91…`과는 다른 UI 빌드입니다. 미디어 핵심 3개 소스 해시는 동일해 기존 범위의 증거를 재사용했으며, 새 EXE로 AWS 시험을 다시 실행한 것은 아닙니다. 이전 EXE는 `artifacts/distribution/phase27-pre-friendly-ui-20261018-1316/`에 보존했습니다.

최종 증분 빌드 2.84초/경고·오류0, 실제 net48 UI 231조건 통과(기존33조건 포함). 실행 도구 호출은 약6.4초 추정이며 별도 native process duration은 수집하지 않았습니다. 760×620·900×720 각각 기본/1.5배 글꼴에서 폭·글높이·화질 선택 글자·하단 버튼 접근과 실제 렌더를 확인했습니다. **실제 Windows 150% DPI 시험은 아닙니다.** 준비·위임·실패 진단까지 약20분이 걸려 일반 시간 목표를 넘겼습니다. 시험의 GDI+/제품 GDI 불일치는 동일 초기화로 교정했고, 별개로 실제 마지막 버튼15px 잘림은 내용 높이 기반 스크롤 범위 지정으로 수정했습니다. assertion 완화는 하지 않았습니다.

## 이전 미디어 후보 반영 상태 — 2026-10-18

화면은 별도 협상 CaptureLease v1 **원 요청 시작부터250ms·최대10fps**, 입력 실행 권한은 **원 요청 시작부터100ms**입니다. 화면의 서버 철회 미관측 허용창100→250ms는 정책 변경입니다. 같은 캡처 proof를 실제 송신 개시까지 검사하며 만료 프레임 재인증·자동 legacy 전환은 없습니다.

후속 서버 `/opt/peerdesk/releases/phase27-capture-v1-20261017` 반영 후 PID102444, 기존9설정 해시·원래2장치 고정 필드·VPN 보존을 확인했습니다. 로컬 proof60/송신경계46/서버30/Wake와 종료구분20, 실제 로컬 큰 owned 화면5초43프레임·드래그·초기/후행 입력 증거는 각 시험 시점의 범위로만 유효합니다. exact opaque POST404/409 뒤 동일 인증·세션으로 종료 사유를 제한적으로 확인하며 실제 다른 오류를 정상 종료로 숨기지 않습니다.

AWS에서는 큰 첫화면 후 입력누락이 간헐 재현됐습니다. seq2 권한 응답155ms에 native 진입0인 만료 경로를 확인했지만 모든 과거 누락의 원인으로 단정하지 않습니다. stream 연결 그룹 분리만으로는 첫입력 누락이 다시 발생해 해결되지 않았습니다. 새 정책은 아직 권한과 결합되지 않은 MAC 검증 입력만 **로컬 수신부터 최대750ms/32개** 보관하고 fresh100ms 권한에서 한 번만 dequeue/bind합니다. stale 응답은 dequeue0, 이미 bind된 입력은 재삽입·재인증하지 않습니다. context 변경은 폐기·held 해제, 입력이 없어도 fresh 권한 부재750ms에 종료합니다.

root 직접 실행으로 binding/FakeNative14·실제 TLS 화면전용+750ms worker9·bound 후100ms 만료의 실제 worker 안전중단12 조건을 통과했습니다. 각각 합성 어댑터/실제 TLS/반사 초기화 worker의 범위이며 물리 입력 종단으로 합산하지 않습니다. 최초 화면전용 복합 assertion 실패 원인은 미확정으로 보존하고 latest-only 계약에 맞춰 같은 generation·seq>0로 교정했습니다.

실제 AWS PID23560에서는 owned960×600 큰 화면·초기 WM_CHAR·드래그·실제 up/held0·클릭 후 **40초188개 고유 프레임(lastSeq207), 공유 유지/오류 없음**을 확인했습니다. 이후 후행 입력 직전 foreground 불일치로 보호 중단해 그 실행은 exit1이며 정상 종료까지 통과한 전체 성공이 아닙니다. 포커스 이탈 원인은 미확정/강제 재활성화·자동 입력 재전송은 없습니다. 별도 late-only PID38524는40초246프레임 후 초점 일치 상태에서도 문자 조건 timeout이 발생했으며 원인은 미확정입니다. 5초 최소 snapshot 진단으로 수집 조건을 검증한 뒤, 빠진 관측을 보완한40초 시험1회(PID25392)는 **235개 고유 프레임 → 첫1회 활성화 → 실제 WM_CHAR0→1 → held0 → target session-ended/LastError없음**, exit0/8조건을 통과했습니다. 초기 드래그·클릭 증거와 이번 후행 입력·정상 종료는 별도 실행입니다. 간헐 실패의 근본 원인이 해결됐거나 무손실이라는 뜻은 아닙니다.

**동기화 산출물 SHA-256:** `42E91D605B21325932F6679563367B4BC9CDDB0EF56944106E139ECA661EDB9E`
- 사용자 경로: `artifacts/build/phase7/bin/PeerDesk.Windows/release/RemoteBridge.exe`
- 동일 후보: `artifacts/distribution/phase27-candidate/RemoteBridge.exe`
- 이전 두 파일 백업: `artifacts/distribution/phase27-pre-sync-20261018/`

실제 시험 child의 unsigned RemoteBridge 참조·기본 AppBase·별도 로더/리디렉션 없음·인접 정확 파일 해시를 교차 확인해 이 산출물을 선정했습니다. 역사 PID의 `Assembly.Location`을 직접 계측한 것은 아닙니다. 다른 빌드 경로의 `FCB9…`에 이 바이너리의 시험 성공을 전이하지 않습니다. 사용자 앱 강제 종료·추가 공개 push/release는 하지 않았습니다.

## 이전 AWS 반영·검증 (후속 CaptureLease 수정 전, 2026-10-17)

- 서비스 PID100967 / health200 / 재시작1회. OS·IP·포트·VPN 변경 없음. 설정·인증서·VPN 등9파일 해시 동일, 기존2장치의 ID/tokenhash/등록시각/fixed revision 보존. 임시 PW 세대 floor는 정상 재시작/후속 운영 중 변경될 수 있습니다.
- 실제 AWS에서 암호화 relay JPEG·입력 payload/독립 lease·텍스트 payload·파일 청크/이어받기 동의/완료/중복 ACK 등 첫19조건 통과. 그러나 복잡한 MainForm 자체를 캡처하는 단계는 실패했으며 전체21 성공으로 집계하지 않습니다.
- 실패 진단: RTT79ms, 캡처 시작 proof age87.4ms, PrintWindow+encode39.6ms, 종료 age126.9ms로100ms를 넘었습니다. Auto 최저 품질에서도3회 연속 만료되어 안전하게 중지했습니다. 연결 풀 장애·모든 화면의 원인으로 일반화하지 않습니다.
- 최소 synthetic owned Form(320×180)으로 범위를 분리한 AWS capture-only4조건은 통과했습니다(PID31812). **작은 화면 전송 성공 / 복잡 화면 시간 초과 미해결 / 전체 desktop·WAN 안정성 미검증**은 별개입니다. 사용자 데스크톱 캡처·네이티브 입력은 이 시험에서 하지 않았습니다.
- 시험 장치는 매번 본인 생성2개만 finally 해제했고, 최종 운영 장치 수는 기존2개입니다.
- 클라이언트는 Auto에서 만료 프레임도 품질 하향 피드백에 반영하도록 보완했습니다. 만료 프레임 전송 금지·원래100ms·연속3회 종료·취소/등록 revision 검사는 유지합니다. 품질26/원래 권한20 조건 통과. 이 보완이 위 복잡 화면 실패를 해결했다는 뜻은 아닙니다.
- 이 이전 후보 EXE SHA256: `95BA23DB3EDB463C8ADE00E7D2F81B075F6BB28072C616BAC120A32CCBE80859`. 공개 릴리즈·기존 실행 중인 앱을 자동 교체하지 않았습니다.

## 사용자 설정

**서버 설정 → 화면·편의**에서 다음을 선택합니다.

| 기능 | 사용 방법 / 안전 경계 |
|---|---|
| 화면과 입력 내용을 안전하게 주고받기 | 기본 켜짐. 두 PC만 내용을 읽도록 보호합니다. 처음 연결할 때 양쪽 PC의 확인 코드를 직접 비교하고 일치할 때만 확인합니다. 서버 관리키/서버 인증서 지문과는 별개의 PC 보안 키입니다. 이후 같은 PC 키는 다시 묻지 않으며 바뀐 키는 차단합니다. |
| 화면만 보기 | 화면을 내주는 PC가 ‘상대가 내 PC의 마우스·키보드를 사용하도록 허용’을 끕니다. 새 연결부터 적용됩니다. |
| 내 화면을 보내는 화질 | 자동 조절/글자가 잘 보이게/가볍게 보내기. 자동은 처리·전송 지연을 관찰해 **전체 JPEG**의 해상도·품질·간격을 조절합니다. 바뀐 영역만 보내는 기능은 제외했습니다. |
| 상대에게 보여줄 화면 | 화면을 내주는 PC가 연결 전에 전체/특정 모니터를 선택합니다. 모니터 구성 변경 시 안전하게 중지하며, 상대가 임의로 공유 범위를 넓히지 않습니다. |
| 텍스트 | 양쪽에서 받기 요청 허용을 선택한 뒤 뷰어 또는 공유 표시줄의 ‘텍스트 보내기’를 누릅니다. 보내기·받기 확인이 따로 있고 자동 붙여넣기/실행은 하지 않습니다. 텍스트만, UTF-8 64KiB 이하입니다. |
| 파일 | 받기 요청 허용 후 파일 선택→상대가 저장 위치 선택/수신 동의. 파일당 최대256MiB, 청크64KiB, 동시 송수신 각2개 제한, SHA-256 검사 후 완료 파일로 이동합니다. 기존 파일을 덮어쓰지 않습니다. |
| 일시 중지/계속 | 현재 세션에서 파일 전송을 일시 중지하고 이어 보낼 수 있습니다. 받는 PC에서 다시 확인합니다. 세션이 바뀌면 기존 전송 권한을 재사용하지 않습니다. 중단된 partial/checkpoint는 보존되며 위치를 표시합니다. 앱 재실행/다른 세션에서 자동 재개·자동 삭제하는 기능은 없습니다. 보류된2개가 수신 슬롯을 점유할 수 있습니다. |
| 화면 직접 연결 | 양쪽에서 명시적으로 켜고 화면을 내주는 PC의 로컬 IP를 선택합니다. 같은 공유기/VPN 등 실제 도달 가능한 환경에서 **화면만** 직결, 입력·권한 확인은 서버를 유지합니다. 직결 불가/네트워크 단절은 별도 암호화 중계 경로로 복귀합니다. 공유기·방화벽·AWS 포트는 자동 변경하지 않습니다. 상대에게 선택 IP/임시 포트가 전달됩니다. 모든 NAT를 통과하는 기능은 아닙니다. |
| 다시 연결 | 기본 꺼짐. 확실한 네트워크 오류에 한해 새 요청1회. 기존 PW·승인·입력 hold를 재사용하지 않고 **상대가 다시 승인**해야 합니다. 사용자 종료/옵션 해제/만료/인증·암호 오류는 재시도 대상이 아닙니다. |
| 최근 PC | 최근 연결한 상대 ID 중심. 오른쪽 클릭으로 내 PC에만 보이는 별명/즐겨찾기를 설정합니다. 온라인 표시는 과거 연결한 PC에 대해30초 간격 확인하는 참고 정보이며, 연결 가능성·공유 권한을 보장하지 않습니다. 실패/알 수 없음은 ‘확인 불가’입니다. |
| 뷰어 | 창 맞춤/100~200% 배율, 전체화면, 연결 종료, 실제 표시 FPS·프레임 요청·표시 처리·용량 진단. 공유하는 PC에도 상대 ID와 중지 버튼을 계속 표시합니다. |

품질·별명/즐겨찾기는 저장됩니다. 새 보안/직결/텍스트·파일/재요청 선택은 현재 실행의 연결 정책이며 서버 참가 PW·PC PW·기존 승인과 별개입니다. 정책은 진행 중인 연결을 몰래 확대하지 않습니다.

## 업데이트

이 후보에 내장한 **별도 제작자 공개 키**로 서명된 업데이트 묶음만 검증합니다. 서버 TLS 지문·관리키로 업데이트를 승인하지 않습니다.

1. 최초1회 ‘업데이트 처음 설정하기’으로 설치 폴더/현재 Windows 계정의 DPAPI 보호 기록을 만듭니다. 기존/손상 기록을 덮어쓰지 않습니다.
2. 제작자에게 받은 폴더(`release.manifest`, `release.signature`, 서명 대상 EXE)를 선택·검증합니다.
3. 적용을 선택하면 현재 프로그램이 **정상 종료된 뒤** 별도 도우미가 교체합니다. 강제 종료·관리자 권한 요청·자동 다운로드/재실행은 없습니다.
4. 이전 버전 복구는 서명과 정확한 파일 해시가 확인된 known-good 백업만 가능합니다. 최초 unsigned 공개본 백업을 서명된 것으로 인정하지 않습니다. 복구해도 anti-rollback floor는 낮아지지 않습니다.

새 개인 서명키는 개발 PC의 비공개 DPAPI 저장본에만 존재하고 Git/EXE에 포함하지 않습니다. 현재 키는 새 후보용이며 기존 공개본의 신뢰를 자동 승격하지 않습니다. Authenticode 코드서명/자동 공개 업데이트 서비스는 아닙니다. `artifacts/verification/phase27/*bundle*`에는 테스트용 synthetic 실행 파일도 있으므로 배포하지 마세요.

## 구현 경계

- RSA3072 장기 PC identity, P256 ephemeral ECDH(CNG), HKDF-SHA256, 방향별 AES256-CBC/HMAC-SHA256 encrypt-then-MAC. MAC 검증 전 CBC 복호화를 하지 않습니다.
- Hello의 역할/양측 ID/서버 URL·full TLS pin/세션/transport epoch/generation/권한·geometry revision을 결합합니다. 양방향 encrypted Finished 뒤에만 실제 기능을 허용합니다.
- 서버 v2 opaque relay는 암호문을 제한된 큐로 중계합니다. JPEG/입력·파일 내용 검사는 복호화한 양쪽 PC가 수행합니다. PC ID·세션·시간·트래픽 크기 등 메타데이터까지 숨기지는 않습니다.
- 암호화 성공/스트림 생존은 공유 동의가 아닙니다. 협상된 request-start250ms 화면/100ms 입력 권한 확인, 중지 tombstone, held-input 해제, 권한·등록 revision 검사는 유지합니다.
- 직접 연결은 별도 fresh 키/Finished를 사용합니다. 화면의 direct/relay 동시 도착은 검증 후 단일 lock에서 순번 확인·게시하여 역행을 방지합니다. 암호화 패킷은 Seal 후 임의로 버리지 않습니다.
- RustDesk 엔진/코덱을 도입한 구현이 아닙니다. 오디오, 변경 영역 전송, 현대 비디오 코덱, UAC/보안 데스크톱, 완전한 IME는 이번 범위에 없습니다.

## 검증과 남은 한계

변경된 부분에 최소 시험을 적용하고, 입력·해시·환경이 같은 독립 모듈 결과는 재사용했습니다. 과거 PASS를 새 실행으로 집계하지 않습니다.

| 증거 | 실제 범위 |
|---|---|
| 후속 최소 회귀 | root 직접 binding/FakeNative14(PID32876, 런타임1.137초), 실제 TLS 화면전용+750ms worker9(PID36316), 실제 worker bound-edge 만료12(PID36664). 반사 초기화/FakeNative 범위와 실제 물리 입력을 구분합니다. |
| 후속 AWS | 초기 text/drag/up·held0/click 및40초188프레임은 PID23560의 부분 성공(exit1). 별도 PID25392는40초235프레임→후행 실제 WM_CHAR→정상 종료8조건 exit0. 중간 PID38524 timeout은 원인 미확정으로 남깁니다. |
| 빌드·검증 시간 경계 | 제품 빌드는 오류0으로 확인했고 최종 시험 child 빌드는2.32초/CS1690 경고1입니다. 5초 최소 AWS 진단은 총12.1초였고40초 관측은30초 초과가 필요한 예외입니다. 준비·위임에서2분 목표 초과가 발생했으며 전체 검증을10초 이내로 최적화했다고 주장하지 않습니다. |
| 초기 통합 빌드(후속 변경 전) | 전체 솔루션 소스·참조 호환성1회 확인: 경고/오류0, 15.95초(PID25920). 전체 런타임 시험 재실행은 아닙니다. 제품은 net48 단일 EXE에 새 소스 linked compile. |
| 이전 로컬 SecureMedia21(후속 품질·진단 변경 전) | 실제 ephemeral pinned TLS 서버+net48 양측. 독립 peer pin·Finished, 직결 offer/화면, 네트워크 스트림 단절 후 암호화 relay 복귀·잘못된 직결 framing은 fallback하지 않고 종료, 별도 원래 입력 lease, concurrent 최신 frame 순번, 실제 MainForm target/viewer 준비·owned-HWND 실제 캡처→암호화 프레임(네이티브 입력은 비활성), 파일 청크/완료/중복 완료 ACK/중복 offer 거부/실제 이어받기 재동의 창/종료 시 ACK 취소/사용자 뷰어 닫기 tombstone. 파일 최초 저장창 선택은 synthetic consent setup으로 분리했고 OS 클립보드를 쓰지 않았습니다. |
| UI33 | 품질/별명 저장, URL·pin 분리, 모니터 geometry·배율·중지 표시, 신규 옵션 기본값/업데이트 버튼, 재연결 오류 분류. 최신 표시줄 크기 변경 이전 일부 UI 결과는 소스 영향이 없는 범위에 재사용했습니다. |
| 독립 모듈 재사용 | crypto53, peer trust14, direct10, protocol79(net48/net10 및 Win7), 품질19(net48/net10 및 Win7), 실제 owned-window 품질15, transfer24, release verifier23/floor11/actual 제품 helper4/서명된 복구7/historical3. 각 시험은 서로 다른 범위이며 ‘전체 몇백개 동작’으로 합산하지 않습니다. |
| 서버 | opaque relay의 실제 권한/등록/세션/공유 quota7, presence7, 기존 StatefulInput132 재사용. 이 행은 격리 서버 시험이며, 실제 AWS 배포 후 결과는 위 별도 항목을 확인하세요. |

**미검증/보류:** 새 후보의 별도 물리 PC 간 WAN 성능/다중 사용자 장기512MB 부하, Win7 전체 GUI·DPAPI 지속 저장(SSH crypto46 이후 DPAPI access denied), 임의 네트워크 장애에서 새 요청 전체 시나리오, 최초 파일 저장창 자동화/전원 손실/디스크 고장·모든 경합. Managed 파일 경로 사전 검사와 실제 open 사이 reparse TOCTOU를 완전히 제거했다고 주장하지 않습니다. 과거 WAN 단일 문자 누락2회의 원인은 아직 미확정입니다.

현재 관측은 ‘무끊김/입력 무손실/RustDesk 동급 성능/독립 보안 감사 완료’를 뜻하지 않습니다. 도쿄 AWS 서비스 반영은 완료했습니다. 로컬 phase7·후보 EXE는 위 해시로 동기화했고 공개 릴리즈·서명 신뢰 전환은 변경하지 않았습니다. 새 기능을 쓰려면 양쪽 PC에 같은 새 EXE가 필요하며 .NET Framework4.8 설치가 전제입니다.
