# RemoteBridge v0.1.0-preview.28

**Experimental public preview — not a production release.**

## 한국어

- **설정창 정렬 개선:** 역할 선택/입력 기억과 저장·삭제·도움말 버튼을 별도 줄로 정리했습니다. 언어를 바꿨다가 돌아와도 버튼이 이전의 긴 폭으로 남지 않습니다.
- **부드러운 버튼:** 주요 화면과 설정 버튼에 둥근 모서리, 은은한 색, 마우스를 올리거나 누를 때의 반응을 적용했습니다. 키보드 포커스·Space·Esc·비활성 동작은 유지합니다.
- **7개 언어와 화면 모드:** 한국어 기본, 영어·일본어·중국어 간체·러시아어·프랑스어·힌디어를 목록에서 선택합니다. ☰ 메뉴로 설정창을 열며 밝게/다크 모드는 바로 적용됩니다.
- **입력 전송과 진단 보완:** 새 기능을 협상한 양쪽 PC 사이에서 반복 입력을 횟수·순서를 유지한 채 묶어 보냅니다. 입력이 오래 대기하거나 권한이 만료되면 중단하는 기존 안전 제한은 유지합니다. 실패 이유의 상대 전달은 가능한 경우에만 이루어집니다.
- 실행 파일과 설정창에 버전을 표시하며, 제품 소스가 바뀐 빌드에는 시험판 번호가 자동으로 올라갑니다.

**사용법:** 아래의 RemoteBridge.exe를 받고 현재 앱을 정상 종료한 뒤 새 파일을 실행하세요. 양쪽 PC를 같은 버전으로 맞추세요. .NET Framework 4.8이 필요합니다. 기존 사용자 설정·비밀키를 다른 사람에게 배포하지 마세요.

**한계:** 실제 Win7, 독립 물리 PC 두 대의 WAN 장시간 공유 및 사용자가 겪은 끊김의 완전한 해결은 이번 릴리즈로 인증하지 않습니다. 안전 제한·통신 오류로 공유가 여전히 중단될 수 있습니다. 운영 서버는 변경하지 않았습니다.

## English

- Settings now separate role/remember controls from save/clear/help actions, align spacing and let translated buttons shrink again.
- Main/settings action buttons have rounded corners, softer colors and hover/pressed feedback while keeping native keyboard, focus, disabled and Cancel behavior.
- The ☰ menu opens a separate settings window. Seven embedded languages (Korean default, English, Japanese, Simplified Chinese, Russian, French and Hindi), instant light/dark mode and cosmetic preference persistence require no runtime translation API/key.
- Negotiated encrypted input batches preserve original event counts/order. Existing queue-age and authority limits are not relaxed. Failure notices are best-effort, not guaranteed after a broken connection.
- Actual build versions appear in the main/settings UI; product-source changes increment the preview version automatically.

Download RemoteBridge.exe, close the current app normally and run the new file on both PCs. Requires .NET Framework 4.8 and applicable Windows updates. Do not share your settings, logs, private keys or credentials.

## Verification and assets

- Windows EXE: **1240576 bytes**, SHA256 **15818F6EA2178184095C513167988FF58DB0DE15FF3B63C4B51A8837A152035F**.
- Fresh exact-product UI: **143 checks passed**, runtime 18.049 seconds / build+run 22.047 seconds. Seven language switches, light/dark, sensitive-field preservation, minimum-size settings, 1.5× font stress (not actual OS DPI), action alignment, button behavior and Escape/Cancel are covered.
- Source-linked peer-trust fixture: **14 checks passed**. No new physical-two-PC or AWS/native-input test was run for this UI follow-up.
- Previous preview.22 input/crypto/diagnostic evidence is reused **only for unchanged core source**, not reported as fresh preview.28 runtime tests. See [verification details](https://github.com/BC-0217/RemoteBridge/blob/v0.1.0-preview.28/docs/VERIFICATION.md).
- Linux x64 server ZIP is **byte-identical to preview.2**: 106309 bytes, SHA256 **C8ECAE9D6646B565E6ACB87D9052A2D8BFE07BEDECB1ECE816910DB313CE89E5**. Server/Core source and live services were not changed. Requires the .NET 10 ASP.NET Core runtime.
- SHA256SUMS.txt lists both assets. Checksums detect changed downloads; they are not an Authenticode signature, an independent security audit or a signed in-app update bundle.
- This remains a GitHub prerelease, not a stable/latest production release. The source is included in the repository; no project reuse license has been selected.

Historical intermittent input misses and real Win7/physical-two-PC WAN/long-session behavior remain unverified or unresolved. No lossless-input, no-disconnect or universal translation-quality guarantee is made.
