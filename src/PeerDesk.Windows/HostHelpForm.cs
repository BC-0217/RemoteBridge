using System;
using System.Drawing;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    // Nonmodal explanatory help. Static text only: no credentials, profile values, network, storage or clipboard access.
    internal sealed class HostHelpForm : Form
    {
        internal const string HostConnection="HostConnection",OwnerKey="OwnerKey",DisplayName="DisplayName",JoinPassword="JoinPassword",AliasInput="AliasInput",VerifiedAlias="VerifiedAlias",PublishGrant="PublishGrant",RegisteredDevices="RegisteredDevices";
        internal const string ServerAddress="ServerAddress",CertificateFingerprint="CertificateFingerprint",SetupFile="SetupFile",LegacyInvitation="LegacyInvitation",ProfileJson="ProfileJson",RoomSettings="RoomSettings";
        private readonly TextBox helpText=new TextBox { Name="HostHelpText", Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, WordWrap=true, Dock=DockStyle.Fill, BackColor=Color.White, TabStop=false };
        private string topic;

        internal HostHelpForm()
        {
            Name="HostHelpForm";
            Font=new Font("Malgun Gothic",9.75f);
            StartPosition=FormStartPosition.CenterParent;
            ShowInTaskbar=false; MinimizeBox=false; MaximizeBox=false;
            Width=520; Height=440; MinimumSize=new Size(380,300);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Padding=new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(helpText,0,0);
            var close=new Button { Name="CloseHostHelp", Text=UiLocalization.Text("닫기"), AutoSize=true, Anchor=AnchorStyles.Right, Margin=new Padding(4,8,0,0) };
            close.Click+=(s,e)=>Close();
            root.Controls.Add(close,0,1);
            Controls.Add(root);
            CancelButton=close;UiLocalization.Attach(this);UiLocalization.Observe(this,()=>{if(topic!=null){int start=helpText.SelectionStart;ShowTopic(topic);helpText.SelectionStart=Math.Min(start,helpText.TextLength);}});
        }

        public string Topic { get { return topic; } }

        internal void ShowTopic(string value)
        {
            string title,body;
            Content(value,out title,out body);
            topic=value;
            Text=UiLocalization.Text("도움말 — ")+title;
            UiLocalization.Bind(helpText,body.Replace("\n",Environment.NewLine));
            helpText.SelectionStart=0; helpText.SelectionLength=0; helpText.ScrollToCaret();
        }

        private static void Content(string value,out string title,out string body)
        {
            switch(value)
            {
                case HostConnection:
                    title=UiLocalization.Text("서버 만들기 — 연결 설정");
                    body=UiLocalization.Text("이 화면은 서버 주인(직접 서버를 설치한 사람)이 사용합니다. 서버에 참가만 하는 사람은 이 화면이 필요 없고, 서버 주인에게 받은 서버 ID와 서버 PW만 [서버 참가]에 입력하면 됩니다.\n\n1) 이미 실행 중인 RemoteBridge 서버의 HTTPS 주소와 전체 인증서 지문(64자리)을 입력합니다. 직접 설치한 서버라면 본인 서버의 값을, 다른 사람의 서버라면 그 서버 주인에게 받은 값을 넣습니다. AWS 인스턴스 생성만으로 서버가 설치되지는 않습니다.\n\n2) [서버 연결 확인] 후, 서버 설치 때 본인이 정한 관리키(다른 서버라면 그 서버 주인에게 받은 키)로 인증합니다. 서버 관리는 PC 등록 없이 가능합니다.\n\n3) [서버 ID / PW]에서 공유할 서버 ID와 서버 PW를 정해 저장합니다. 같은 서버의 이름 조회 기능이 직접 게시를 허용한 경우에만 바로 저장됩니다. 그렇지 않으면 공통 서버 ID 조회 서비스 관리자의 승인 코드가 필요합니다. 앱 배포자나 서버 주인이라는 이유만으로 승인 권한이 생기지는 않으며, 자동 승인되지 않습니다.\n\n4) 저장이 확인된 서버 ID와 서버 PW를 상대에게 전달합니다. 본인도 원격 연결을 사용하려면 [서버 참가]에서 PC를 등록하세요.\n\n주소는 서버가 설치된 컴퓨터(예: Lightsail 인스턴스 상세)의 공인 IP 또는 도메인과 설치 포트(예: 8443)입니다. 지문은 [인증서 지문] 도움말의 openssl 명령으로 본인 서버에서 직접 확인하거나, 다른 서버라면 그 서버 주인에게 전화·대면 등 별도 경로로 받으세요. 공개 프로필 파일 자체는 신뢰 증명이 아닙니다.\n\n로컬 프로필 이름, JSON 원문, 이전 서버 초대 코드는 고급 설정입니다. 새 서버는 추가 초대 코드 없이 서버 PW를 사용합니다. 새 버전 서버는 PC 등록 정보를 파일에 보관하므로 서버나 앱을 다시 시작해도 같은 ID를 사용합니다. 처음 새 버전으로 바꾼 뒤에는 한 번 다시 참가해야 할 수 있습니다. 진행 중인 원격 연결은 서버 재시작 시 종료되며 임시 비밀번호는 새로 발급됩니다.");
                    break;
                case OwnerKey:
                    title=UiLocalization.Text("서버 관리키");
                    body=UiLocalization.Text("■ 의미\n서버 주인(직접 서버를 설치한 사람)이 서버를 설치할 때 정한 관리용 키입니다. 서버 주인이 직접 입력하며, 다른 사람의 서버라면 그 서버 주인에게 받아야 합니다. 소유자 관리(현재 설정 조회·저장, 접속 이름 게시, 등록 장치 조회·해제)에 사용됩니다.\n\n")+
                        UiLocalization.Text("■ 다른 값과의 구분\n- AWS 계정 비밀번호나 SSH 비밀번호가 아닙니다.\n- 참가자가 입력하는 참가 비밀번호와도 다릅니다.\n\n")+
                        UiLocalization.Text("■ 관리키 얻는 방법\n1) 서버에 참가만 하는 사람: 관리키가 필요 없습니다. 서버 주인에게 받은 서버 ID와 서버 PW만 [서버 참가]에 입력하세요.\n")+
                        UiLocalization.Text("2) 서버를 직접 설치한 서버 주인: 설치할 때 본인이 정해 따로 보관한 관리키를 사용합니다.\n")+
                        UiLocalization.Text("3) 다른 사람이 서버를 설치한 경우: 설치한 사람에게 'RemoteBridge 서버 관리키'를 비공개 경로(전화·대면 등)로 요청하세요. AWS 계정 정보나 SSH 접속 정보를 달라고 할 필요는 없습니다.\n")+
                        UiLocalization.Text("4) 서버 주인이 SSH/관리자 권한이 있고 서버 설정 파일이 있는 경우: 서버의 PEERDESK_OWNER_CONFIG 환경 변수가 가리키는 설정 파일에서 ownerSecret 값을 본인만 확인합니다. 설정 파일 위치는 설치 방식마다 다르므로 설치 기록이나 서버를 시작하는 서비스의 환경 설정에서 PEERDESK_OWNER_CONFIG 값을 확인하세요. 이것은 공개 서버 설치 정보(JSON)와 다른 비공개 설정 파일이며, 앱에서 키 원문을 조회할 수는 없습니다. 이 파일이나 화면 캡처, 로그를 다른 사람에게 보내거나 공개 프로필에 넣지 마세요.\n")+
                        UiLocalization.Text("5) 관리키를 분실한 경우: 먼저 안전한 보관본, 설치한 사람, 서버의 비공개 설정 원본을 확인하세요. 서버 PW나 참가 비밀번호로는 복구할 수 없습니다. 기존 키를 확인할 수 없다면 서버 관리 권한이 있는 사람이 별도의 승인된 재설정 절차를 진행해야 합니다(설정 변경과 서비스 재시작이 필요할 수 있습니다). 이 앱에는 관리키를 자동 발급하거나 복구하는 화면이 없습니다.\n\n")+
                        UiLocalization.Text("■ 입력 방법\n서버 설치 시 설정된 관리키(32자 이상)를 그대로 입력하세요. 화면에서는 가려져 표시됩니다.\n\n")+
                        UiLocalization.Text("■ 보관 방식 (입력 기억)\n[입력 기억]은 기본으로 켜져 있습니다. 켜져 있으면 관리키·서버 ID·서버 PW·참가용 서버 ID/PW가 이 PC의 현재 Windows 사용자 계정에서만 풀 수 있게 암호화되어 저장됩니다. 저장된 값은 정확히 같은 연결 설정(주소·전체 지문, 참가는 같은 서버 ID)에서만 다시 채워지며 항상 미확인 초안입니다. 저장된 입력은 인증·소유 증명이 아니며, 자동 참가·자동 조회·자동 서버 호출은 하지 않습니다. 복원된 서버 PW는 직접 고치지 않는 한 다시 전송되지 않습니다.\n[저장 입력 삭제] 또는 [입력 기억] 끄기는 현재 역할의 현재 연결 설정 항목만 지웁니다(장치 등록·연결 기록·다른 역할은 유지). 내보내기 기능은 없으며 외부 문서나 다른 PC로 옮겨지지 않습니다. 참가했던 서버 ID가 다른 서버를 가리키면 PW를 보내지 않으며, 새 서버가 맞다면 [저장 입력 삭제] 후 PW를 다시 입력하세요.\n화면의 관리키는 입력한 시점의 연결 설정에만 사용되며 연결 설정이 바뀌면 화면에서 지워집니다.\n\n")+
                        UiLocalization.Text("관리키는 서버 소유 권한입니다. PC 장치 등록이나 상대 PC의 원격 연결 승인은 서버 소유 권한을 주지 않습니다. 관리키를 참가자에게 보내지 마세요.");
                    break;
                case DisplayName:
                    title=UiLocalization.Text("표시 이름");
                    body=UiLocalization.Text("■ 의미\n서버 주인이 저장하고 관리 화면에서 조회하는 표시 이름입니다. 현재 버전에서는 이 값을 바꿔도 디렉터리 프로필이나 참가자 화면 이름이 자동 변경되지 않습니다.\n\n")+
                        UiLocalization.Text("■ 다른 이름과의 구분\n- 표시 이름: 서버 주인이 서버에 저장하는 보기용 이름\n- 서버 이름(연결 설정): 이 PC에만 있는 로컬 프로필 이름\n- 접속 이름(별칭): 참가자가 참가할 때 입력하는 디렉터리 별칭\n세 값은 서로 다르며 하나를 바꿔도 다른 값은 바뀌지 않습니다. 표시 이름은 계정 ID도 아닙니다.\n\n")+
                        UiLocalization.Text("■ 변경 절차\n1) 서버 관리키를 입력하고 [인증 / 현재 설정 조회]를 누릅니다.\n2) 새 표시 이름을 입력합니다.\n3) 같은 서버라면 관리키는 이 창에 가려진 채 유지되므로(비어 있으면 다시 입력) [설정 저장]을 누릅니다.");
                    break;
                case JoinPassword:
                    title=UiLocalization.Text("새 참가 비밀번호");
                    body=UiLocalization.Text("■ 의미\n참가자가 접속 이름과 함께 입력하여 서버에 참가하는 비밀번호입니다. 서버 관리키 및 PC 홈 화면의 임시 비밀번호와는 다릅니다.\n\n")+
                        UiLocalization.Text("■ 입력 규칙\n- 8~128자\n- 비워 두고 저장하면 기존 비밀번호가 그대로 유지됩니다.\n\n")+
                        UiLocalization.Text("■ 설정 절차\n1) 새 참가 비밀번호를 8~128자로 입력합니다.\n2) 상단 서버 관리키가 비어 있으면 입력합니다(같은 서버에서는 이 창에 가려진 채 유지됨).\n3) [설정 저장]을 누르고 하단 성공 문구를 확인합니다.\n서버에 제출한 뒤에는 보안을 위해 입력칸이 비워지며, 제출 후 실패해도 비워질 수 있습니다. 값이 사라졌다는 이유만으로 성공으로 판단하지 마세요. 관리키 누락·형식 오류로 제출 전 차단되면 새 참가 비밀번호는 유지됩니다.\n\n")+
                        UiLocalization.Text("■ 저장 방식\n서버에는 해시만 저장되므로 기존 비밀번호 원문은 다시 조회할 수 없습니다. 상태 칸에는 설정 여부만 표시됩니다.\n\n")+
                        UiLocalization.Text("■ 주의\n비밀번호를 바꿔도 이미 등록된 장치는 해제되지 않습니다. 특정 장치를 차단하려면 [등록 장치] 탭에서 해제하세요.");
                    break;
                case AliasInput:
                    title=UiLocalization.Text("확인할 접속 이름");
                    body=UiLocalization.Text("■ 의미\n참가자가 [참가] 화면의 '서버 ID' 칸에 입력하는 디렉터리 별칭입니다. 계정 ID, 표시 이름, 로컬 프로필 이름이 아닙니다.\n\n")+
                        UiLocalization.Text("■ 형식\n- 영문 소문자(a-z), 숫자(0-9), 하이픈(-)만 사용\n- 3~32자\n- 첫 글자와 마지막 글자는 영문 소문자 또는 숫자\n\n")+
                        UiLocalization.Text("■ 이미 게시된 접속 이름 확인\n별도 승인 코드 없이 [접속 이름 확인]을 누르면 디렉터리에 등록된 프로필 주소와 전체 인증서 지문이 현재 연결 설정과 일치하는지 확인합니다.");
                    break;
                case VerifiedAlias:
                    title=UiLocalization.Text("확인된 접속 이름");
                    body=UiLocalization.Text("■ 의미\n디렉터리 확인을 통과한 접속 이름입니다. 디렉터리의 프로필 주소와 전체 인증서 지문이 현재 연결 설정과 일치할 때만 표시됩니다.\n\n")+
                        UiLocalization.Text("■ 사용 방법\n[접속 이름 복사]를 눌러 복사한 접속 이름과 참가 비밀번호를 참가자에게 별도로 전달하면 참가자가 [참가]할 수 있습니다.\n\n")+
                        UiLocalization.Text("■ 주의\n연결 설정(주소·지문)이 바뀌면 확인 결과는 무효가 되며 다시 확인해야 합니다.");
                    break;
                case PublishGrant:
                    title=UiLocalization.Text("게시 승인 코드 / 최초 게시 절차");
                    body=UiLocalization.Text("■ 의미\n중앙 디렉터리는 접속 이름으로 서버 주소를 찾아주는 공통 이름 조회 서비스입니다. 그 서비스의 관리자(서버 ID로 주소를 찾아 주는 서비스를 관리하는 별도 역할)가 미리 승인해 발급하는 1회용 토큰입니다. 자동으로 승인·발급되지 않습니다. 정확한 접속 이름, 프로필, 전체 인증서 지문에 묶여 있어 다른 값에는 사용할 수 없습니다.\n\n")+
                        UiLocalization.Text("■ 승인 코드가 아닌 것\n임의의 문구, 서버 관리키, 참가 비밀번호, 관리자 키가 아닙니다.\n\n")+
                        UiLocalization.Text("■ 최초 게시 절차\n1) 사용할 접속 이름과 연결 설정(주소·전체 지문)을 정합니다.\n2) 공통 이름 조회 서비스 관리자에게 새 접속 이름 승인을 요청합니다. 이 버전에는 승인 코드를 자동 발급하는 화면이 없습니다.\n3) 서버 주인이 받은 승인 코드를 입력하고 서버 관리키를 입력한 뒤 [서버 이름 게시]를 누릅니다.\n4) [접속 이름 확인]으로 게시 결과를 확인합니다.\n5) 확인된 접속 이름과 참가 비밀번호를 참가자에게 별도로 전달합니다.\n\n")+
                        UiLocalization.Text("이미 게시된 접속 이름을 확인할 때는 새 승인 코드가 필요하지 않습니다.");
                    break;
                case RegisteredDevices:
                    title=UiLocalization.Text("등록 장치 / 해제");
                    body=UiLocalization.Text("■ 의미\n이 서버에 참가해 등록된 PC 목록입니다. 현재 온라인 PC나 진행 중인 세션 목록이 아닙니다. 목록의 ID를 지인의 홈 화면 [내 ID]와 대조해 PC를 구분하세요.\n\n")+
                        UiLocalization.Text("■ 조회\n서버 관리키를 입력하고 [장치 목록]을 누릅니다.\n\n")+
                        UiLocalization.Text("■ 해제\n목록에서 장치를 선택하고 서버 관리키가 입력된 상태(비어 있으면 다시 입력)에서 [선택 장치 해제]를 누르면 그 장치의 인증 토큰이 무효화되고 진행 중인 연결도 끊어집니다. 다시 사용하려면 장치를 새로 등록해야 합니다. 본인 PC를 해제하면 본인도 다시 참가해야 합니다.\n\n")+
                        UiLocalization.Text("■ 참고\n- 참가 비밀번호를 바꿔도 등록 장치는 자동으로 해제되지 않습니다.\n- 새 버전 서버는 장치 등록을 파일에 보관합니다. 서버나 앱을 다시 시작해도 같은 PC ID를 유지하며, 임시 비밀번호만 새로 발급됩니다.\n- 예전 서버에서 처음 업그레이드할 때는 한 번 다시 참가해야 합니다. 서버의 등록 파일이나 이 PC의 등록 정보를 삭제·초기화하거나 Windows 계정을 바꾸면 같은 ID가 보장되지 않습니다. MAC 주소로 ID를 계산하는 방식은 아닙니다.");
                    break;
                case ServerAddress:
                    title=UiLocalization.Text("서버 주소");
                    body=UiLocalization.Text("■ 의미\n실행 중인 RemoteBridge 서버의 HTTPS 주소입니다. 예: https://203.0.113.10:8443 또는 https://remote.example.com:8443\n\n")+
                        UiLocalization.Text("■ 어디서 얻나요\n직접 설치한 서버라면 서버 컴퓨터(예: Lightsail 인스턴스 상세)의 공인 IP 또는 도메인에 설치 포트(예: 8443)를 붙입니다. 다른 사람의 서버라면 그 서버 주인에게 주소를 받으세요. 서버 주인(만드는 사람)만 입력하며, 참가만 하는 사람은 입력하지 않습니다. AWS 인스턴스를 만들기만 해서는 사용할 수 없으며, 그 인스턴스에서 RemoteBridge 서버가 실행 중이고 해당 포트가 열려 있어야 합니다.\n\n")+
                        UiLocalization.Text("■ 주의\nhttps:// 주소만 허용됩니다. 이 앱은 주소를 자동으로 찾아 주지 않습니다.");
                    break;
                case CertificateFingerprint:
                    title=UiLocalization.Text("인증서 지문 (SHA-256)");
                    body=UiLocalization.Text("서버 주인(직접 서버를 설치한 사람)만 입력합니다. 참가만 하는 사람은 서버 ID/PW만 입력하므로 지문을 따로 받을 필요가 없습니다. 자기 서버의 지문은 자기 서버에서 확인하며 앱을 만든 사람에게 요청하는 값이 아닙니다.\n\n■ 의미\n접속 대상이 내가 확인한 서버인지 확인하는 확인번호이며, 비밀번호가 아닙니다. 기술적으로 서버 TLS 인증서의 SHA-256 지문 64자리(16진수)입니다. 예: 3F2A…(총 64자)\n\n")+
                        UiLocalization.Text("■ 어디서 얻나요\n- 직접 설치한 서버: 서버 컴퓨터에서 본인 서버의 공개 인증서로 다음 명령을 실행해 확인합니다.\n- 다른 사람의 서버: 그 서버 주인(직접 서버를 설치한 사람)에게 전화·대면 등으로 받습니다.\n서버 주인이 확인하는 명령:\nopenssl x509 -in /etc/peerdesk/server.crt -noout -fingerprint -sha256\n출력의 콜론을 제거한 64자리 값을 사용합니다.\n\n")+
                        UiLocalization.Text("■ 주의\n이 앱은 접속한 서버의 인증서를 자동으로 신뢰하지 않습니다. 지문은 전화·대면 등 별도 경로로 확인하세요.");
                    break;
                case SetupFile:
                    title=UiLocalization.Text("서버 설치 정보 불러오기");
                    body=UiLocalization.Text("■ 의미\n서버 주소와 확인번호(인증서 지문)가 적힌 설정 파일입니다. 서버 주인이 직접 만들어 전달한 공개 서버 설치 정보 파일(설치 과정에서 자동으로 만들어지지 않습니다. Version, Name, RendezvousUrl, RelayUrl, CertificateSha256 5개 항목의 JSON)을 불러와 주소와 지문을 채웁니다.\n\n")+
                        UiLocalization.Text("■ 주의\n이 파일에는 비밀값이 없어야 합니다. 파일 자체는 신뢰 증명이 아니므로 지문은 별도 경로로 다시 확인하세요.");
                    break;
                case LegacyInvitation:
                    title=UiLocalization.Text("이전 서버 참가 비밀값 (고급)");
                    body=UiLocalization.Text("■ 의미\n소유자 기능이 없는 이전 서버에서 서버 주인이 따로 나눠 주던 참가용 비밀값(초대 코드)입니다.\n\n")+
                        UiLocalization.Text("■ 소유자 기능이 켜진 서버\n추가 비밀값이 없습니다. [서버 ID / PW]에서 정한 서버 PW(참가 비밀번호)를 그대로 입력하면 됩니다.");
                    break;
                case ProfileJson:
                    title=UiLocalization.Text("프로필 JSON (고급)");
                    body=UiLocalization.Text("■ 의미\n설정 내용을 글자로 풀어놓은 것입니다(JSON). 보통 직접 편집할 필요가 없습니다. 주소·지문 입력값으로 자동 생성되는 공개 서버 프로필 원문이며, 직접 편집은 문제 해결용입니다.\n\n")+
                        UiLocalization.Text("■ 주의\n프로필에는 비밀번호나 관리키를 넣지 마세요. 프로필은 신뢰 증명이 아닙니다.");
                    break;
                case RoomSettings:
                    title=UiLocalization.Text("서버 ID / 서버 PW");
                    body=UiLocalization.Text("먼저 서버 주인이 관리키로 인증한 뒤 서버 ID와 PW를 저장하세요. 성공 문구를 확인하고 [복사]로 전달합니다. 참가자는 받은 ID/PW만 [서버 참가]에 입력합니다.\n\n■ 서버 ID\n참가자가 [서버 참가]에서 입력하는 이름입니다. 영문 소문자/숫자/하이픈 3~32자이며 처음과 끝은 영문 또는 숫자입니다. 대문자는 저장 전에 소문자로 바뀝니다.\n\n")+
                        UiLocalization.Text("■ 서버 PW\n서버에 들어올 때 쓰는 비밀번호입니다(8~128자, 앞뒤 공백도 그대로 사용). 비워 두면 기존 PW를 유지합니다. 서버에 저장된 PW는 다시 조회할 수 없습니다.\n[입력 기억]이 켜져 있으면 서버 ID·PW 초안이 이 Windows 사용자 계정에 암호화 저장됩니다(미확인 초안, 권한 증명 아님). 복원된 PW는 직접 고치기 전엔 전송되지 않으며, 복사 가능 상태는 복원되지 않습니다. 삭제는 [저장 입력 삭제](현재 역할만).\n\n")+
                        UiLocalization.Text("■ 기존 ID\n기존 ID도 계속 사용 가능하며 최대 8개까지 유지됩니다. ID 삭제나 다른 서버로의 이동은 지원하지 않습니다.\n\n")+
                        UiLocalization.Text("■ 게시 범위\n이 앱이 사용하는 공통 조회 서버와 주소·인증서 지문이 같은 서버이며, 그 서버가 직접 게시를 허용한 경우에만 바로 저장됩니다. 그 밖의 경우에는 공통 서버 ID 조회 서비스 관리자의 승인 코드가 필요하며 자동 승인되지 않습니다. [고급] 접속 이름/게시 탭을 사용합니다.");
                    break;
                default:
                    title=UiLocalization.Text("도움말");
                    body=UiLocalization.Text("도움말 항목을 찾을 수 없습니다.");
                    break;
            }
        }
    }
}
