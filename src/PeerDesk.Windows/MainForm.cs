using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    internal sealed partial class MainForm : Form
    {
        private ControlPlaneClient client;
        private readonly TextBox profileBox=new TextBox { Name="ProfileJson", Multiline=true, ScrollBars=ScrollBars.Vertical, Dock=DockStyle.Fill, MinimumSize=new Size(0,80), Visible=false };
        private readonly TextBox serverNameBox=new TextBox { Name="ServerName", Dock=DockStyle.Fill, MaxLength=64 };
        private readonly TextBox serverAddressBox=new TextBox { Name="ServerAddress", Dock=DockStyle.Fill, MaxLength=2048 };
        private readonly TextBox serverFingerprintBox=new TextBox { Name="ServerFingerprint", Dock=DockStyle.Fill, MaxLength=64 };
        private readonly Label profileStateLabel=new Label { Name="ServerProfileState", AutoSize=true, MaximumSize=new Size(360,0), ForeColor=Color.DimGray };
        private readonly CheckBox advancedToggle=new CheckBox { Name="ShowAdvancedProfile", Text="고급 설정", AutoSize=true, Margin=new Padding(4,8,4,4) };
        private Button healthButton;
        private bool syncingProfile;
        private readonly TextBox inviteBox=new TextBox { Name="InvitationCode", UseSystemPasswordChar=true, Width=220 };
        private readonly TextBox deviceBox=new TextBox { Name="MyDeviceId", ReadOnly=true, Width=280, Font=new Font("Malgun Gothic",18f,FontStyle.Bold), BorderStyle=BorderStyle.None, Margin=new Padding(4,2,4,6) };
        private readonly TextBox ownPasswordBox=new TextBox { Name="TemporaryPassword", ReadOnly=true, UseSystemPasswordChar=false, Width=280, Font=new Font("Malgun Gothic",18f,FontStyle.Bold), BorderStyle=BorderStyle.None, Margin=new Padding(4,2,4,6) };
        private readonly Label expiryLabel=new Label { Name="PasswordExpiry", AutoSize=true };
        private readonly TextBox targetBox=new TextBox { Name="TargetDeviceId", Width=270, Font=new Font("Malgun Gothic",14f), Margin=new Padding(4,2,4,8) };
        private readonly TextBox targetPasswordBox=new TextBox { Name="TargetPassword", UseSystemPasswordChar=true, Width=270, Font=new Font("Malgun Gothic",14f), Margin=new Padding(4,2,4,8) };
        private readonly TabPage homePage=new TabPage("홈") { Name="HomePage" };
        private readonly TabPage sessionPage=new TabPage("세션 관리") { Name="SessionManagementPage" };
        private readonly TabPage serverPage=new TabPage("서버 설정") { Name="ServerSettingsPage" };
        private Button copyIdButton,copyPasswordButton,copyAllButton;
        private readonly ListBox sessions=new ListBox { Name="Sessions", Dock=DockStyle.Fill, HorizontalScrollbar=true };
        private readonly ListBox historyList=new ListBox { Name="ConnectionHistory", Dock=DockStyle.Fill };
        private readonly Label status=new Label { Name="ConnectionStatus", AutoSize=true, MaximumSize=new Size(700,0) };
        private readonly Label historyWarningLabel=new Label { AutoSize=true, MaximumSize=new Size(700,0), ForeColor=Color.DarkOrange };
        private readonly Button importButton,exportButton,resetButton,registerButton,rotateButton,requestButton,approveButton,connectButton,disconnectButton;
        private Button refreshButton,rejectButton,viewerButton,shareStartButton,shareStopButton;
        private readonly TabControl settingsTabs=new TabControl { Name="ServerSettingsTabs", Dock=DockStyle.Fill };
        private readonly List<SessionItem> known=new List<SessionItem>();
        private readonly SemaphoreSlim lifecycleGate=new SemaphoreSlim(1,1);
        private readonly ConnectionHistory history;
        private readonly SettingsStore settingsStore;
        private SettingsState storedSettings;
        private bool settingsInvalid,storedCredentialPending,registrationVerified,registrationRejected;
        private bool passwordSynchronized,passwordPollBusy;
        private readonly System.Windows.Forms.Timer passwordTimer=new System.Windows.Forms.Timer { Interval=1000 };
        private readonly CancellationTokenSource passwordCancellation=new CancellationTokenSource();
        private readonly Stopwatch passwordWatch=Stopwatch.StartNew();
        private long nextPasswordCheckMs,lastPasswordCheckMs=-15000;
        private long passwordDeadlineMs=long.MaxValue;
        private MediaSharingSession sharing;
        private ViewerForm viewer;
        private HostHelpForm hostHelp;
        private bool shutdownComplete,shutdownBusy,showOwnPassword=true,lifecycleBusy;
        private string historyWriteWarning;
        private readonly DirectoryBootstrap directoryBootstrap;
        private readonly ComboBox roleBox=new ComboBox { Name="ServerRole", DropDownStyle=ComboBoxStyle.DropDownList, Width=160 };
        private readonly Panel guestPanel=new Panel { Name="GuestPanel", Dock=DockStyle.Fill, AutoScroll=true };
        private readonly TabControl hostTabs=new TabControl { Name="HostSettingsTabs", Dock=DockStyle.Fill, Visible=false };
        private readonly TextBox guestAliasBox=new TextBox { Name="GuestServerAlias", Width=260, MaxLength=32 };
        private readonly TextBox guestPasswordBox=new TextBox { Name="GuestJoinPassword", UseSystemPasswordChar=true, Width=260, MaxLength=128 };
        private readonly Label guestStateLabel=new Label { Name="GuestState", AutoSize=true, MaximumSize=new Size(600,0), ForeColor=Color.DarkRed };
        private readonly TextBox ownerSecretBox=new TextBox { Name="OwnerSecret", UseSystemPasswordChar=true, Dock=DockStyle.Fill, MaxLength=1024 };
        private readonly TextBox hostDisplayNameBox=new TextBox { Name="HostDisplayName", Dock=DockStyle.Fill, MaxLength=64 };
        private readonly TextBox hostJoinPasswordBox=new TextBox { Name="HostJoinPassword", UseSystemPasswordChar=true, Dock=DockStyle.Fill, MaxLength=128 };
        private readonly TextBox hostPublishGrantBox=new TextBox { Name="HostPublishGrant", UseSystemPasswordChar=true, Dock=DockStyle.Fill, MaxLength=512 };
        private readonly ListBox ownerDevicesList=new ListBox { Name="OwnerDevices", Dock=DockStyle.Fill, Height=96, IntegralHeight=false, HorizontalScrollbar=true };
        private Button guestJoinButton,hostSaveButton,hostPublishButton,ownerListButton,ownerRevokeButton;
        private bool ownerBusy;
        private Button hostLoadButton,verifyAliasButton,copyAliasButton;
        private readonly TextBox currentDisplay=new TextBox { Name="HostCurrentDisplayName",ReadOnly=true,Dock=DockStyle.Fill,Text="미확인" };
        private readonly TextBox passwordState=new TextBox { Name="HostPasswordState",ReadOnly=true,Dock=DockStyle.Fill,Text="미확인" };
        private readonly TextBox publishedAlias=new TextBox { Name="HostPublishedAlias",ReadOnly=true,Dock=DockStyle.Fill,Text="확인 불가" };
        private readonly TextBox aliasInput=new TextBox { Name="HostAliasInput",Dock=DockStyle.Fill,MaxLength=32 };
        private readonly Label aliasMirror=new Label { Name="HostAliasSummary",AutoSize=true,Text="접속 이름: 확인 불가",MaximumSize=new Size(460,0) };
        private string verifiedAlias,aliasBinding;
        private static string ProfileBinding(ServerProfile p){return p.RendezvousUrl+"\\n"+p.RelayUrl+"\\n"+p.CertificateSha256.ToUpperInvariant();}
        private void ClearOwnerSummary(){currentDisplay.Text="미확인";passwordState.Text="미확인";}
        private void ClearAlias(){verifiedAlias=null;aliasBinding=null;publishedAlias.Text="확인 불가";aliasMirror.Text="접속 이름: 확인 불가";if(copyAliasButton!=null)copyAliasButton.Enabled=false;}
        private readonly TextBox roomServerIdBox=new TextBox { Name="RoomServerId",Dock=DockStyle.Fill,MaxLength=32 };
        private readonly TextBox roomJoinPasswordBox=new TextBox { Name="RoomJoinPassword",UseSystemPasswordChar=true,Dock=DockStyle.Fill,MaxLength=128 };
        private readonly TextBox roomStatusBox=new TextBox { Name="RoomStatus",ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Height=96,Text="미확인 — 서버 관리키를 입력하고 [불러오기]를 누르세요." };
        private Button roomSaveButton,roomLoadButton,roomCopyButton;
        // Room state verified by a real owner-room response bound to the current profile. The shared PW is memory-only and
        // kept only when this window successfully set it, so the copy text never contains a stale or guessed PW.
        private string roomVerifiedId,roomBinding,roomSharedPassword;
        private bool? roomSelfPublish;
        private void ClearRoom(){roomVerifiedId=null;roomBinding=null;roomSharedPassword=null;roomSelfPublish=null;roomStatusBox.Text="미확인 — 서버 관리키를 입력하고 [불러오기]를 누르세요.";if(roomCopyButton!=null)roomCopyButton.Enabled=false;if(roomSaveButton!=null)roomSaveButton.Enabled=!ownerBusy&&!lifecycleBusy&&!shutdownBusy;}
        private void InvalidateHostState(){ClearOwnerSummary();ClearAlias();ClearRoom();}
        // Owner key is kept masked in memory (optionally persisted only as an encrypted UNVERIFIED draft; see MainForm.SavedInputs.cs) and is bound to the profile (HTTPS URLs +
        // full cert pin) that was valid when the user typed it. Any binding change, reset, guest join or close clears it, and
        // every owner API call re-checks the binding so the key is never sent to a different server.
        private string ownerKeyBinding;
        private static string OwnerBinding(ServerProfile p){return new Uri(p.RendezvousUrl).AbsoluteUri+"\\n"+new Uri(p.RelayUrl).AbsoluteUri+"\\n"+p.CertificateSha256.ToUpperInvariant();}
        private string CurrentBindingOrNull(){try{return OwnerBinding(ReadProfile());}catch{return null;}}
        private void ClearOwnerKey(){ownerSecretBox.Clear();ownerKeyBinding=null;}
        private void OnOwnerSecretChanged(object sender,EventArgs e){ownerKeyBinding=ownerSecretBox.TextLength==0?null:CurrentBindingOrNull();}
        private void ReconcileOwnerKey(){if(ownerSecretBox.TextLength==0){ownerKeyBinding=null;return;}string now=CurrentBindingOrNull();if(ownerKeyBinding==null||now==null||now!=ownerKeyBinding)ClearOwnerKey();}
        private bool OwnerKeyBoundToCurrent(){string now=CurrentBindingOrNull();return ownerKeyBinding!=null&&now!=null&&now==ownerKeyBinding;}
        private void ApplyOwnerSettings(Dictionary<string,object> value){string name=(string)value["displayName"];bool configured=(bool)value["joinPasswordConfigured"];hostDisplayNameBox.Text=name;currentDisplay.Text=name;passwordState.Text=configured?"설정됨 (원문 조회 불가)":"미설정"; }
        private void SetVerifiedAlias(string alias,ServerProfile profile){verifiedAlias=alias;aliasBinding=ProfileBinding(profile);publishedAlias.Text=alias;aliasMirror.Text="접속 이름: "+alias;}
        private void CopyHostAlias(){if(lifecycleBusy||shutdownBusy||verifiedAlias==null)return;try{if(aliasBinding!=ProfileBinding(ReadProfile())){ClearAlias();return;}CopyText(verifiedAlias,"접속 이름을 복사했습니다.");}catch(Exception ex){ClearAlias();ShowError(ex);}}


        public MainForm() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","history.json"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","settings.json")) { }
        internal MainForm(string historyPath) : this(historyPath,Path.Combine(Path.GetDirectoryName(historyPath)??".","settings.json")) { }
        internal MainForm(string historyPath,string settingsPath) : this(historyPath,settingsPath,DirectoryBootstrap.LoadDefault()) { }
        internal MainForm(string historyPath,string settingsPath,DirectoryBootstrap bootstrap) : this(historyPath,settingsPath,bootstrap,CaptureScope.ForDesktopShare()) { }
        // autoCaptureScope only selects WHAT an explicitly approved session shares; it never grants consent by itself.
        internal MainForm(string historyPath,string settingsPath,DirectoryBootstrap bootstrap,CaptureScope autoCaptureScope)
        {
            if(autoCaptureScope==null)throw new ArgumentNullException("autoCaptureScope");
            this.autoCaptureScope=autoCaptureScope;
            try { if(bootstrap!=null)bootstrap.Validate();directoryBootstrap=bootstrap; } catch { directoryBootstrap=null; }
            history=new ConnectionHistory(historyPath);
            settingsStore=new SettingsStore(settingsPath);
            try { storedSettings=settingsStore.Load();profileBox.Text=storedSettings.ProfileJson??"";storedCredentialPending=storedSettings.IsRegistered; }
            catch(Exception ex) { settingsInvalid=true;storedSettings=new SettingsState { ProfileJson="" };historyWriteWarning="설정 오류: "+ex.Message; }
            Text="RemoteBridge — 리모드브리지";
            Font=new Font("Malgun Gothic",9.75f);
            Width=900; Height=720; MinimumSize=new Size(760,620);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=3, Padding=new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var warning=new Label { Text="PW 자동 접근이 켜져 있으면 올바른 PW로 화면·입력 공유가 시작됩니다. 공란 요청은 직접 승인해야 합니다.", AutoSize=true, ForeColor=Color.DarkRed, Font=new Font(Font,FontStyle.Bold), Padding=new Padding(0,0,0,8) };
            root.Controls.Add(warning,0,0);
            var homeLayout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=2, Padding=new Padding(10), BackColor=Color.FromArgb(247,249,252) };
            homeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); homeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            homeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,318)); homeLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var leftFlow=Card("내 연결 정보");
            leftFlow.Controls.Add(Caption("내 ID"));
            leftFlow.Controls.Add(deviceBox);
            leftFlow.Controls.Add(Caption("임시 비밀번호 (대소문자 구분)"));
            leftFlow.Controls.Add(ownPasswordBox);
            leftFlow.Controls.Add(expiryLabel);
            var copyRow=new FlowLayoutPanel { AutoSize=true, WrapContents=true, Margin=new Padding(0,4,0,0), MaximumSize=new Size(300,0) };
            copyIdButton=Button("ID 복사",CopyId);copyIdButton.Name="CopyDeviceId";copyRow.Controls.Add(copyIdButton);
            copyPasswordButton=Button("PW 복사",CopyPassword);copyPasswordButton.Name="CopyTemporaryPassword";copyRow.Controls.Add(copyPasswordButton);
            copyAllButton=Button("모두 복사",CopyAll);copyAllButton.Name="CopyAllCredentials";copyRow.Controls.Add(copyAllButton);
            leftFlow.Controls.Add(copyRow);
            var passwordRow=new FlowLayoutPanel { AutoSize=true, WrapContents=true, Margin=new Padding(0) };
            var togglePasswordButton=Button("표시/숨김",ToggleOwnPassword);togglePasswordButton.Name="TogglePassword";passwordRow.Controls.Add(togglePasswordButton);
            rotateButton=Button("새 비밀번호",RotatePassword); rotateButton.Name="RotatePassword"; passwordRow.Controls.Add(rotateButton);
            leftFlow.Controls.Add(passwordRow);

            var rightFlow=Card("다른 PC에 연결");
            rightFlow.Controls.Add(Caption("상대 ID")); rightFlow.Controls.Add(targetBox);
            rightFlow.Controls.Add(Caption("상대 임시/고정 PW (공란: 승인 요청)")); rightFlow.Controls.Add(targetPasswordBox);
            requestButton=Button("연결 요청",RequestSession); requestButton.Name="RequestConnection"; requestButton.Font=new Font(Font.FontFamily,11f,FontStyle.Bold); requestButton.Padding=new Padding(16,4,16,4); rightFlow.Controls.Add(requestButton);
            rightFlow.Controls.Add(new Label { Text="PW가 맞으면 자동 연결됩니다(상대가 허용한 경우). PW 공란은 승인 요청입니다. 중지는 [세션 관리]에서 할 수 있습니다.",AutoSize=true,MaximumSize=new Size(290,0),ForeColor=Color.DimGray,Margin=new Padding(4,8,4,0) });
            homeLayout.Controls.Add(leftFlow.Parent,0,0); homeLayout.Controls.Add(rightFlow.Parent,1,0);
            var historyGroup=new GroupBox { Text="최근 연결 기록 (더블클릭하면 상대 ID 입력)",Dock=DockStyle.Fill,Padding=new Padding(8) };
            historyList.BorderStyle=BorderStyle.None;historyList.IntegralHeight=false;
            historyGroup.Controls.Add(historyList);homeLayout.Controls.Add(historyGroup,0,1);homeLayout.SetColumnSpan(historyGroup,2);
            historyList.DoubleClick+=HistoryDoubleClick;
            homePage.Controls.Add(homeLayout);

            var connectLayout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Padding=new Padding(10), BackColor=Color.FromArgb(247,249,252) };
            connectLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); connectLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var sessionGroup=new GroupBox { Text="대기·진행 세션", Dock=DockStyle.Fill, Padding=new Padding(8) };
            sessions.IntegralHeight=false;
            sessionGroup.Controls.Add(sessions); connectLayout.Controls.Add(sessionGroup,0,0);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(0,6,0,0) };
            refreshButton=Button("대기 새로고침",RefreshPending);
            approveButton=Button("승인",delegate { Decide(true); }); approveButton.Name="ApproveSession";
            rejectButton=Button("거절",delegate { Decide(false); });
            connectButton=Button("연결",Connect); connectButton.Name="ConnectSession";
            viewerButton=Button("화면 보기",OpenViewer);viewerButton.Name="OpenViewer";
            shareStartButton=Button("화면 공유 시작",StartShare);
            shareStopButton=Button("화면 공유 중지",StopShare);
            disconnectButton=Button("연결 해제/취소",Revoke); disconnectButton.Name="DisconnectSession";
            refreshButton.Name="RefreshPending";rejectButton.Name="RejectSession";shareStartButton.Name="StartShare";shareStopButton.Name="StopShare";
            actions.Controls.Add(refreshButton);actions.Controls.Add(approveButton);actions.Controls.Add(rejectButton);actions.Controls.Add(connectButton);
            actions.Controls.Add(viewerButton);actions.Controls.Add(shareStartButton);actions.Controls.Add(shareStopButton);actions.Controls.Add(disconnectButton);
            connectLayout.Controls.Add(actions,0,1);
            sessionPage.Controls.Add(connectLayout);

            var settingsPage=serverPage;
            var settingsLayout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=5, Padding=new Padding(8), AutoScroll=true };
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var fields=new TableLayoutPanel { Dock=DockStyle.Fill, AutoSize=true, ColumnCount=2, RowCount=5 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            fields.ColumnCount=3;fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,44));
            var localNameLabel=new Label { Text="로컬 프로필 이름 (고급)",AutoSize=true,Padding=new Padding(0,6,0,0) };
            fields.Controls.Add(localNameLabel,0,0); fields.Controls.Add(serverNameBox,1,0);
            fields.Controls.Add(new Label { Text="서버 주소",AutoSize=true,Padding=new Padding(0,6,0,0) },0,1); fields.Controls.Add(serverAddressBox,1,1);
            fields.Controls.Add(CreateHostHelpButton("HelpServerAddress","?",HostHelpForm.ServerAddress),2,1);
            fields.Controls.Add(new Label { Text="인증서 지문\r\nSHA-256 64자리",AutoSize=true,Padding=new Padding(0,6,0,0) },0,2); fields.Controls.Add(serverFingerprintBox,1,2);
            fields.Controls.Add(CreateHostHelpButton("HelpCertificateFingerprint","?",HostHelpForm.CertificateFingerprint),2,2);
            var sourceHint=new Label { Text="주소 예: https://remote.example.com:8443 — 실행 중인 본인 서버 IP/도메인 + 포트.\r\n지문: 본인 서버 인증서에서 확인하거나 그 서버 주인에게 받으세요. 참가자는 ID/PW만 입력합니다. 자세한 방법은 [?]를 누르세요.",AutoSize=true,MaximumSize=new Size(520,0),ForeColor=Color.DimGray };fields.Controls.Add(sourceHint,1,3);
            fields.Controls.Add(profileStateLabel,1,4);
            var setupRow=new FlowLayoutPanel { AutoSize=true,WrapContents=true,Dock=DockStyle.Fill };
            var setupButton=Button("서버 설치 정보 불러오기",ImportProfile);setupButton.Name="ImportSetupFile";setupRow.Controls.Add(setupButton);
            setupRow.Controls.Add(CreateHostHelpButton("HelpSetupFile","?",HostHelpForm.SetupFile));
            fields.Controls.Add(setupRow,1,5);
            fields.SizeChanged+=(s,e)=>{int w=Math.Max(80,fields.ClientSize.Width-216);sourceHint.MaximumSize=new Size(w,0);profileStateLabel.MaximumSize=new Size(w,0);};
            settingsLayout.Controls.Add(fields,0,0);
            var registerActions=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,WrapContents=true };
            inviteBox.Width=200; registerActions.Controls.Add(new Label { Text="서버 PW / 이전 서버 참가 비밀값",AutoSize=true,Padding=new Padding(0,6,0,0) }); registerActions.Controls.Add(inviteBox);
            registerActions.Controls.Add(CreateHostHelpButton("HelpLegacyInvitation","?",HostHelpForm.LegacyInvitation));
            registerButton=Button("서버 등록",Register);registerButton.Name="RegisterDevice";registerActions.Controls.Add(registerButton);
            healthButton=Button("서버 연결 확인",CheckServerConnection);healthButton.Name="CheckServerConnection";setupRow.Controls.Add(healthButton);
            settingsLayout.Controls.Add(registerActions,0,1);
            var trust=new Label { Text="등록 전 인증서 지문을 확인하세요(본인 서버는 직접, 다른 서버는 그 서버 주인에게 별도 경로로). 프로필은 신뢰 증명이 아닙니다. 임시 비밀번호는 영문 대·소문자 9자리(대소문자 구분)이며 1시간마다 갱신됩니다. 연결 성공 후에도 새 PW로 바뀌지만 기존 연결은 유지됩니다.",AutoSize=true,MaximumSize=new Size(620,0),ForeColor=Color.DarkOrange,Padding=new Padding(0,6,0,0) };
            settingsLayout.Controls.Add(trust,0,2);
            var profileActions=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,WrapContents=true };
            importButton=Button("프로필 가져오기",ImportProfile);exportButton=Button("프로필 내보내기",ExportProfile);resetButton=Button("프로필/등록 초기화",ResetProfile);resetButton.Name="ResetProfile";
            profileActions.Controls.Add(advancedToggle);profileActions.Controls.Add(importButton);profileActions.Controls.Add(exportButton);profileActions.Controls.Add(resetButton);
            profileActions.Controls.Add(CreateHostHelpButton("HelpHostConnection","? 연결 설정 도움말",HostHelpForm.HostConnection));
            var jsonHelp=CreateHostHelpButton("HelpProfileJson","? 프로필 JSON",HostHelpForm.ProfileJson);setupRow.Controls.Add(jsonHelp);
            settingsLayout.Controls.Add(profileActions,0,3);
            settingsLayout.Controls.Add(profileBox,0,4);
            BuildRolePages(settingsPage,settingsLayout);
            settingsTabs.TabPages.Add(homePage);settingsTabs.TabPages.Add(sessionPage);InitializeAccessPolicy();settingsTabs.TabPages.Add(serverPage);
            settingsTabs.Padding=new Point(14,5);
            settingsTabs.SelectedTab=storedCredentialPending?homePage:serverPage;
            root.Controls.Add(settingsTabs,0,1);

            var bottom=new TableLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Padding=new Padding(0,6,0,0) };
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            historyWarningLabel.Text=history.Warning??"";
            status.Text="서버 프로필을 설정하고 등록하세요.";
            bottom.Controls.Add(historyWarningLabel,0,0); bottom.Controls.Add(status,0,1); root.Controls.Add(bottom,0,2);
            Controls.Add(root);
            BackColor=Color.FromArgb(244,247,251); root.BackColor=BackColor; homePage.BackColor=Color.FromArgb(247,249,252); sessionPage.BackColor=Color.FromArgb(247,249,252); settingsPage.BackColor=Color.FromArgb(247,249,252);
            ApplyTheme(this);
            SyncFieldsFromProfileJson();
            if(String.IsNullOrWhiteSpace(serverNameBox.Text)){syncingProfile=true;try{serverNameBox.Text="PeerDesk 서버";}finally{syncingProfile=false;}}
            advancedToggle.Checked=!String.IsNullOrWhiteSpace(profileBox.Text)&&!IsProfileValid(profileBox.Text);
            profileBox.Visible=advancedToggle.Checked;serverNameBox.Visible=advancedToggle.Checked;localNameLabel.Visible=advancedToggle.Checked;registerActions.Visible=advancedToggle.Checked;importButton.Visible=advancedToggle.Checked;exportButton.Visible=advancedToggle.Checked;jsonHelp.Visible=advancedToggle.Checked;
            advancedToggle.CheckedChanged+=(s,e)=>{profileBox.Visible=advancedToggle.Checked;serverNameBox.Visible=advancedToggle.Checked;localNameLabel.Visible=advancedToggle.Checked;registerActions.Visible=advancedToggle.Checked;importButton.Visible=advancedToggle.Checked;exportButton.Visible=advancedToggle.Checked;jsonHelp.Visible=advancedToggle.Checked;};
            serverNameBox.TextChanged+=OnSimpleFieldChanged;serverAddressBox.TextChanged+=OnSimpleFieldChanged;serverFingerprintBox.TextChanged+=OnSimpleFieldChanged;
            profileBox.TextChanged+=OnProfileJsonChanged;
            ownerSecretBox.TextChanged+=OnOwnerSecretChanged;
            UpdateHistoryList();
            SetRegistered(false);
            SetOwnerEnabled();
            registerButton.Enabled=!storedCredentialPending&&!settingsInvalid;
            resetButton.Enabled=storedCredentialPending&&!settingsInvalid;
            deviceBox.Text=storedCredentialPending?(storedSettings.DeviceId??""):"(미등록)";
            expiryLabel.Text=storedCredentialPending?"":"등록 후 표시";
            status.Text=settingsInvalid?historyWriteWarning:(storedCredentialPending?"저장된 장치 등록을 서버에서 확인합니다.":"미등록 — 서버 설정 후 등록하세요.");
            InitializeSavedInputs();
            FormClosing+=OnClosing;
            FormClosed+=(s,e)=>{ClearOwnerKey();roomSharedPassword=null;roomVerifiedId=null;if(hostHelp!=null&&!hostHelp.IsDisposed)hostHelp.Close();hostHelp=null;};
            Shown+=OnShown;
            passwordTimer.Tick+=async (s,e)=>{if(passwordWatch.ElapsedMilliseconds>=nextPasswordCheckMs)await RefreshPasswordStateAsync();};
            Activated+=(s,e)=>{if(!passwordPollBusy&&passwordWatch.ElapsedMilliseconds-lastPasswordCheckMs>=5000)nextPasswordCheckMs=passwordWatch.ElapsedMilliseconds;};
            passwordTimer.Start();
            FormClosed+=(s,e)=>{passwordTimer.Stop();passwordTimer.Dispose();passwordCancellation.Cancel();passwordCancellation.Dispose();};
            snapshotTimer.Tick+=async (s,e)=>{CheckSharingStopped();AccessPolicyTick();if(passwordWatch.ElapsedMilliseconds>=nextSnapshotMs)await PollSnapshotAsync();};
            snapshotTimer.Start();
            FormClosed+=(s,e)=>{snapshotTimer.Stop();snapshotTimer.Dispose();InvalidateAutomation();};
        }

        // ---- Phase 19: consent-gated automation ------------------------------------------------------------------------
        // A (requester): only the sid created by THIS registration's RequestSession may auto-connect once on Approved and
        // auto-open the viewer once on Connected. B (target): only a sid approved through an explicit ConnectionConsentForm
        // click may auto-start sharing once on Connected, bound to URL+pin, epoch, own ID, requester ID and capture scope.
        // STOP/reset/close/profile change bump the epoch and drop every authority synchronously, before any await.
        private sealed class AutoRequest { public string SessionId,Binding,OwnId,TargetId; public ControlPlaneClient Client; public long Epoch; public bool ConnectAttempted,ViewerOpened; }
        private sealed class ConsentGrant { public string SessionId,Binding,OwnId,RequesterId; public ControlPlaneClient Client; public long Epoch; public CaptureScope Scope; public bool StartAttempted,Activated; public string PasswordEpoch; public CancellationTokenSource StartCancellation; }
        private readonly CaptureScope autoCaptureScope;
        private readonly System.Windows.Forms.Timer snapshotTimer=new System.Windows.Forms.Timer { Interval=250 };
        private const int SnapshotIntervalMs=2000,SnapshotMaxBackoffMs=60000; // 30/min nominal + 4/min PW checks stays under the 60/min quota.
        private readonly Random snapshotJitter=new Random();
        private long nextSnapshotMs;
        private int snapshotFailures;
        private bool snapshotBusy;
        private long autoEpoch=1;
        private CancellationTokenSource automationCancellation=new CancellationTokenSource();
        private AutoRequest autoRequest;
        private readonly Dictionary<string,ConsentGrant> consentGrants=new Dictionary<string,ConsentGrant>(StringComparer.Ordinal);
        private readonly HashSet<string> promptedSessions=new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<SessionItem> consentQueue=new Queue<SessionItem>();
        private ConnectionConsentForm activeConsent;
        internal bool SharingIsRunning { get { return sharing!=null&&sharing.IsRunning; } }
        // Only a session whose StartAsync has returned (and passed its post-start checks) is monitored, so the transient
        // running=false during startup is never mistaken for a stop. A stopped runner is never restarted automatically.
        private MediaSharingSession monitoredSharing;
        private bool sharingStopCleanupBusy;
        private string shareStopDiagnostic;
        internal string ShareStopDiagnostic { get { return shareStopDiagnostic; } }
        private void CheckSharingStopped()
        {
            var watched=monitoredSharing;
            if(watched==null||sharingStopCleanupBusy||shutdownBusy||IsDisposed||watched.IsRunning)return;
            if(!ReferenceEquals(sharing,watched)){monitoredSharing=null;return;}
            CleanupStoppedSharing(watched);
        }
        private async void CleanupStoppedSharing(MediaSharingSession stopped)
        {
            sharingStopCleanupBusy=true;bool acquired=false;string error=null,diagnostic=null,sid=null,own=null,peer=null;
            ControlPlaneClient bound=null;long epoch=0;CancellationToken token=default(CancellationToken);
            try
            {
                await lifecycleGate.WaitAsync();acquired=true;
                if(ReferenceEquals(monitoredSharing,stopped))monitoredSharing=null;
                if(!ReferenceEquals(sharing,stopped)||stopped.IsRunning)return;
                sharing=null;error=stopped.LastError;sid=stopped.SessionId;
                string endReason=stopped.EndReason;
                try{await stopped.StopAsync();}catch{}finally{stopped.Dispose();}
                if(shutdownBusy||IsDisposed)return;
                if(!String.IsNullOrEmpty(error))
                {
                    shareStopDiagnostic=endReason=="expired"?"연결 유효 시간이 만료되어 화면 공유가 종료되었습니다.":"⚠ 화면 공유가 오류로 중단되었습니다(자동 재시작 안 함): "+error;
                    status.Text=shareStopDiagnostic;diagnostic=shareStopDiagnostic;
                    // Sharer is the session target; the expected peer is the initiator recorded for this sid.
                    var c=client;var rec=sid==null?null:known.FirstOrDefault(x=>x.SessionId==sid);
                    if(c!=null&&registrationVerified&&rec!=null&&rec.TargetDeviceId==c.DeviceId)
                    {bound=c;own=c.DeviceId;peer=rec.InitiatorDeviceId;epoch=autoEpoch;token=automationCancellation.Token;}
                }
                else { shareStopDiagnostic=null; status.Text=endReason=="expired"?"연결 유효 시간이 만료되어 화면 공유가 종료되었습니다.":endReason=="user-stopped"?"화면 공유가 정상적으로 중지되었습니다.":endReason=="session-ended"?"연결이 정상적으로 종료되었습니다.":"화면 공유가 중단되었습니다."; }
            }
            catch(Exception ex){if(!shutdownBusy&&!IsDisposed)status.Text="공유 중단 정리 오류: "+ex.Message;}
            finally{if(acquired)lifecycleGate.Release();sharingStopCleanupBusy=false;}
            if(bound!=null)await RefineShareStopMessageAsync(bound,epoch,token,sid,own,peer,diagnostic);
        }
        // Runs outside the lifecycle gate. Only an authoritative fresh snapshot row (same client/binding/epoch, matching
        // sid/own/peer, terminal Revoked/Rejected) replaces the error diagnostic with a normal end message. Expired,
        // live, missing rows and any snapshot failure keep the original diagnostic untouched.
        private async Task RefineShareStopMessageAsync(ControlPlaneClient c,long epoch,CancellationToken token,string sid,string own,string peer,string diagnostic)
        {
            string message=await GetSessionEndMessageAsync(c,epoch,token,sid,own,peer);
            if(message==null||shutdownBusy||IsDisposed||epoch!=autoEpoch||!ReferenceEquals(client,c)||!registrationVerified)return;
            if(!String.Equals(shareStopDiagnostic,diagnostic,StringComparison.Ordinal)||sharing!=null)return;
            shareStopDiagnostic=null;status.Text=message;
        }
        // Stable helper (reusable by the viewer path): bounded fresh snapshot, null when not authoritative or on any error.
        internal async Task<string> GetSessionEndMessageAsync(ControlPlaneClient c,long epoch,CancellationToken token,string sid,string own,string peer)
        {
            if(c==null||String.IsNullOrEmpty(sid))return null;
            try
            {
                using(var bounded=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    bounded.CancelAfter(5000);
                    var rows=await c.SnapshotAsync(bounded.Token);
                    if(shutdownBusy||IsDisposed||epoch!=autoEpoch||!ReferenceEquals(client,c)||c.DeviceId!=own||!registrationVerified)return null;
                    return SessionFeedback.SessionEndMessage(rows,sid,own,peer);
                }
            }
            catch(Exception){return null;}
        }
        internal ConnectionConsentForm ActiveConsentDialog { get { return activeConsent; } }
        internal int QueuedConsentCount { get { return consentQueue.Count; } }
        internal long AutomationEpoch { get { return autoEpoch; } }
        private static bool IsLive(string status){return status=="Pending"||status=="Approved"||status=="Connected";}
        private static string ClientBinding(ControlPlaneClient c){return c.ServerUrl+"\n"+(c.CertificateSha256??"").ToUpperInvariant();}
        // Synchronous: callers invoke this before awaiting anything so in-flight automation observes the new epoch.
        private void InvalidateAutomation()
        {
            InvalidateAccessRuntime();
            foreach(var accessGrant in consentGrants.Values)if(accessGrant.StartCancellation!=null){accessGrant.StartCancellation.Cancel();accessGrant.StartCancellation.Dispose();}
            autoEpoch++;
            var old=automationCancellation;automationCancellation=new CancellationTokenSource();
            try{old.Cancel();}catch{}old.Dispose();
            autoRequest=null;consentGrants.Clear();consentQueue.Clear();
            var dialog=activeConsent;activeConsent=null;
            if(dialog!=null&&!dialog.IsDisposed)dialog.Dismiss();
        }
        private bool RequestValid(AutoRequest r)
        {
            var c=client;
            return r!=null&&ReferenceEquals(r,autoRequest)&&r.Epoch==autoEpoch&&!shutdownBusy&&registrationVerified&&c!=null&&ReferenceEquals(c,r.Client)&&c.DeviceId==r.OwnId&&ClientBinding(c)==r.Binding;
        }
        private bool GrantValid(ConsentGrant g)
        {
            ConsentGrant current;var c=client;
            return g!=null&&consentGrants.TryGetValue(g.SessionId,out current)&&ReferenceEquals(current,g)&&g.Epoch==autoEpoch&&!shutdownBusy&&registrationVerified
                &&c!=null&&ReferenceEquals(c,g.Client)&&c.DeviceId==g.OwnId&&ClientBinding(c)==g.Binding&&ReferenceEquals(g.Scope,autoCaptureScope)&&(g.PasswordEpoch==null||g.Activated||AccessRuntimeValid(g.Client,g.PasswordEpoch));
        }
        private void ScheduleSnapshot(bool failed)
        {
            snapshotFailures=failed?Math.Min(snapshotFailures+1,10):0;
            long delay=failed?Math.Min(SnapshotMaxBackoffMs,(long)SnapshotIntervalMs<<snapshotFailures):SnapshotIntervalMs;
            nextSnapshotMs=passwordWatch.ElapsedMilliseconds+delay+snapshotJitter.Next(0,failed?1000:250);
        }
        // Single-flight, UI-context poll. Never takes the lifecycle gate (no deadlock with password refresh/reset/close).
        internal async Task PollSnapshotAsync()
        {
            if(snapshotBusy||lifecycleBusy||shutdownBusy||!registrationVerified||client==null)return;
            snapshotBusy=true;var current=client;long epoch=autoEpoch;var token=automationCancellation.Token;bool failed=false;
            nextSnapshotMs=long.MaxValue;
            try
            {
                var rows=await current.SnapshotAsync(token);
                if(shutdownBusy||IsDisposed||epoch!=autoEpoch||!ReferenceEquals(client,current)||!registrationVerified)return;
                ApplySnapshot(current,rows);
            }
            catch(OperationCanceledException){ }
            catch(Exception){failed=true;} // Missing/failed snapshot is never terminal; active sessions survive.
            finally{snapshotBusy=false;if(!shutdownBusy)ScheduleSnapshot(failed);}
        }
        private void ApplySnapshot(ControlPlaneClient c,List<SessionItem> rows)
        {
            string own=c.DeviceId;
            foreach(var item in rows)
            {
                if(item.TargetDeviceId!=own&&item.InitiatorDeviceId!=own)continue;
                var before=known.FirstOrDefault(x=>x.SessionId==item.SessionId);
                if(before==null||before.Status!=item.Status){Add(item);Record(item);} // history only on state change
                if(!IsLive(item.Status)){DropAuthority(item.SessionId,item.Status=="Revoked"?"session-ended":item.Status=="Expired"?"expired":null);continue;}
                if(item.TargetDeviceId==own&&item.Status=="Pending"&&item.InitiatorDeviceId!=own&&promptedSessions.Add(item.SessionId)) {
                    if(item.AutoApprovalEligible&&item.AuthorizationKind!="manual"&&AccessRuntimeValid(c,item.RuntimeEpoch))AutoApprovePassword(c,item);
                    else consentQueue.Enqueue(item);
                }
                if(item.Status!="Pending"&&activeConsent!=null&&activeConsent.SessionId==item.SessionId){var d=activeConsent;activeConsent=null;d.Dismiss();}
                var r=autoRequest;
                if(r!=null&&r.SessionId==item.SessionId&&item.InitiatorDeviceId==own&&r.TargetId==item.TargetDeviceId&&RequestValid(r))
                {
                    if(item.Status=="Approved"&&!r.ConnectAttempted)AutoConnect(r);
                    else if(item.Status=="Connected"&&r.ConnectAttempted&&!r.ViewerOpened)AutoOpenViewer(r);
                }
                ConsentGrant g;
                if(item.Status=="Connected"&&item.TargetDeviceId==own&&consentGrants.TryGetValue(item.SessionId,out g)&&!g.StartAttempted&&item.InitiatorDeviceId==g.RequesterId&&GrantValid(g))
                    AutoStartShare(g);
            }
            ShowNextConsent();
        }
        private void DropAuthority(string sid,string confirmedEndReason=null)
        {
            if(sharing!=null&&sharing.SessionId==sid){sharing.CancelLocal(confirmedEndReason);CleanupTerminalSession(sid);}
            else if(viewer!=null&&!viewer.IsDisposed&&viewer.SessionId==sid)CleanupTerminalSession(sid);
            ConsentGrant removedGrant;if(consentGrants.TryGetValue(sid,out removedGrant)&&removedGrant.StartCancellation!=null){removedGrant.StartCancellation.Cancel();removedGrant.StartCancellation.Dispose();}
            consentGrants.Remove(sid);
            if(autoRequest!=null&&autoRequest.SessionId==sid)autoRequest=null;
            if(activeConsent!=null&&activeConsent.SessionId==sid){var d=activeConsent;activeConsent=null;d.Dismiss();}
            if(consentQueue.Any(x=>x.SessionId==sid)){var keep=consentQueue.Where(x=>x.SessionId!=sid).ToList();consentQueue.Clear();foreach(var x in keep)consentQueue.Enqueue(x);}
        }
        private async void CleanupTerminalSession(string sid)
        {
            await lifecycleGate.WaitAsync();
            try {
                if(sharing!=null&&sharing.SessionId==sid)await StopShareCore();
                if(viewer!=null&&!viewer.IsDisposed&&viewer.SessionId==sid){
                    var old=viewer;viewer=null;await old.CloseSessionAsync();if(!old.IsDisposed)old.Close();
                }
            } catch(Exception ex){if(!shutdownBusy&&!IsDisposed)status.Text="종료된 연결 정리: "+ex.Message;}
            finally{lifecycleGate.Release();}
        }
        private void ShowNextConsent()
        {
            if(activeConsent!=null||shutdownBusy||IsDisposed)return;
            while(consentQueue.Count>0)
            {
                var item=consentQueue.Dequeue();
                var latest=known.FirstOrDefault(x=>x.SessionId==item.SessionId);
                if(latest==null||latest.Status!="Pending")continue;
                var dialog=new ConnectionConsentForm(item.SessionId,item.InitiatorDeviceId);
                activeConsent=dialog;
                dialog.Decided+=OnConsentDecided;
                dialog.FormClosed+=(s,e)=>{if(ReferenceEquals(activeConsent,dialog))activeConsent=null;if(!shutdownBusy&&!IsDisposed)BeginInvoke(new Action(ShowNextConsent));};
                dialog.Show(this);
                return;
            }
        }
        private async void OnConsentDecided(ConnectionConsentForm dialog,bool approve)
        {
            if(!ReferenceEquals(activeConsent,dialog))return; // dismissed/invalidated: no decision is sent
            var c=client;long epoch=autoEpoch;var token=automationCancellation.Token;
            if(c==null||!registrationVerified||shutdownBusy)return;
            var grant=approve?new ConsentGrant { SessionId=dialog.SessionId,RequesterId=dialog.RequesterId,OwnId=c.DeviceId,Binding=ClientBinding(c),Client=c,Epoch=epoch,Scope=autoCaptureScope }:null;
            try
            {
                var updated=await c.DecideAsync(dialog.SessionId,approve,token);
                if(epoch!=autoEpoch||!ReferenceEquals(client,c)||shutdownBusy||IsDisposed)return;
                if(updated.SessionId!=dialog.SessionId||updated.InitiatorDeviceId!=dialog.RequesterId||updated.TargetDeviceId!=c.DeviceId)throw new InvalidOperationException("서버 승인 응답이 요청과 일치하지 않습니다.");
                var before=known.FirstOrDefault(x=>x.SessionId==updated.SessionId);
                if(before==null||before.Status!=updated.Status){Add(updated);Record(updated);}
                if(approve&&(updated.Status=="Approved"||updated.Status=="Connected"))
                {
                    consentGrants[grant.SessionId]=grant;
                    status.Text="승인했습니다. 상대가 연결하면 전체 화면·입력 공유가 자동으로 시작됩니다. [화면 공유 중지]로 언제든 중지할 수 있습니다.";
                    if(updated.Status=="Connected"&&GrantValid(grant))AutoStartShare(grant);
                }
                else status.Text=approve?"서버 상태: "+updated.Status:"연결 요청을 거절했습니다.";
            }
            catch(OperationCanceledException){ }
            // Lost/failed response: fail closed (no grant) and never retry the decision blindly.
            catch(Exception ex){if(!shutdownBusy&&!IsDisposed)status.Text=(approve?"승인 응답을 확인하지 못해 자동 공유를 하지 않습니다: ":"거절 전송 실패: ")+ex.Message;}
        }
        private async void AutoConnect(AutoRequest r)
        {
            r.ConnectAttempted=true;var c=r.Client;var token=automationCancellation.Token;
            try
            {
                var updated=await c.ConnectAsync(r.SessionId,token);
                if(!RequestValid(r)||updated.SessionId!=r.SessionId||updated.InitiatorDeviceId!=r.OwnId||updated.TargetDeviceId!=r.TargetId||IsDisposed)return;
                var before=known.FirstOrDefault(x=>x.SessionId==updated.SessionId);
                if(before==null||before.Status!=updated.Status){Add(updated);Record(updated);}
                if(updated.Status=="Connected")AutoOpenViewer(r);else status.Text="서버 상태: "+updated.Status;
            }
            catch(OperationCanceledException){ }
            catch(Exception ex){if(!shutdownBusy&&!IsDisposed)status.Text="자동 연결 실패 (수동 [연결] 가능): "+ex.Message;}
        }
        private void AutoOpenViewer(AutoRequest r)
        {
            if(!RequestValid(r)||r.ViewerOpened)return;
            r.ViewerOpened=true; // once: an explicitly closed viewer is never reopened automatically
            if(viewer!=null&&!viewer.IsDisposed)return;
            try{viewer=new ViewerForm(r.Client,r.SessionId);var opened=viewer;viewer.FormClosed+=(a,b)=>{if(ReferenceEquals(viewer,opened))viewer=null;};viewer.Show(this);status.Text="연결되었습니다. 상대가 공유를 시작하면 화면이 표시됩니다.";}
            catch(Exception ex){status.Text="화면 보기 열기 실패: "+ex.Message;}
        }
        private async void AutoStartShare(ConsentGrant g)
        {
            if(g.StartAttempted)return;g.StartAttempted=true; // once, never restarted
            var token=g.StartCancellation==null?automationCancellation.Token:g.StartCancellation.Token;bool acquired=false;
            try
            {
                await lifecycleGate.WaitAsync(token);acquired=true;
                if(!GrantValid(g)||sharing!=null||IsDisposed)return;
                var candidate=new MediaSharingSession(g.Client,g.SessionId,g.Scope){RequestStatefulInput=true};sharing=candidate;
                try{await candidate.StartAsync(token);}
                catch(Exception ex){if(ReferenceEquals(sharing,candidate))sharing=null;candidate.Dispose();if(!shutdownBusy&&!IsDisposed)status.Text="자동 공유 시작 실패: "+ex.Message;return;}
                if(!GrantValid(g))
                {
                    // Stopped/reset/closed while starting: tear down the late start instead of leaving it running.
                    if(ReferenceEquals(sharing,candidate))sharing=null;
                    try{await candidate.StopAsync();}catch{}finally{candidate.Dispose();}
                    return;
                }
                g.Activated=true;monitoredSharing=candidate;shareStopDiagnostic=null;
                status.Text="● 승인한 요청("+g.RequesterId+")에 화면·마우스·키보드 공유 중 — 언제든 중지할 수 있습니다.";
            }
            catch(OperationCanceledException){ }
            catch(Exception ex){if(!shutdownBusy&&!IsDisposed)status.Text="자동 공유 오류: "+ex.Message;}
            finally{if(acquired)lifecycleGate.Release();}
        }

        private void BuildRolePages(TabPage settingsPage,Control settingsLayout)
        {
            var outer=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2 };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var roleRow=new FlowLayoutPanel { Dock=DockStyle.Fill, AutoSize=true, MinimumSize=new Size(0,42), WrapContents=true, Padding=new Padding(8,4,8,0) };
            roleRow.Controls.Add(new Label { Text="역할",AutoSize=true,Padding=new Padding(0,6,0,0) });
            roleBox.Items.Add("서버 참가"); roleBox.Items.Add("서버 만들기"); roleBox.SelectedIndex=0;
            roleRow.Controls.Add(roleBox);
            // Role selection itself grants no server authority; the help explains authentication separately.
            AddSavedInputsControls(roleRow);
            outer.Controls.Add(roleRow,0,0);
            var content=new Panel { Dock=DockStyle.Fill };

            var guest=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2, Padding=new Padding(8) };
            guest.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); guest.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            guest.Controls.Add(new Label { Text="서버 ID",AutoSize=true,Padding=new Padding(0,6,0,0) },0,0); guest.Controls.Add(guestAliasBox,1,0);
            guest.Controls.Add(new Label { Text="서버 PW",AutoSize=true,Padding=new Padding(0,6,0,0) },0,1); guest.Controls.Add(guestPasswordBox,1,1);
            guestJoinButton=Button("참가",GuestJoin); guestJoinButton.Name="GuestJoin"; guest.Controls.Add(guestJoinButton,1,2);
            guest.Controls.Add(new Label { Text="서버를 만든 사람에게 받은 서버 ID와 서버 PW를 입력하세요. ID의 영문 대문자는 조회 전에 소문자로 바뀝니다(PW는 그대로).",AutoSize=true,MaximumSize=new Size(600,0),ForeColor=Color.DarkOrange,Padding=new Padding(0,6,0,0) },1,3);
            guestStateLabel.Text=directoryBootstrap==null?"공통 서버 ID 조회 설정을 읽을 수 없어 참가할 수 없습니다. 기본 설정은 EXE에 내장됩니다. 옆의 "+DirectoryBootstrap.SidecarFileName+"은 고급 덮어쓰기용이며, 잘못된 파일은 무시하지 않습니다.":"";
            guest.Controls.Add(guestStateLabel,1,4);
            guestPanel.Controls.Add(guest);

            var legacyPage=new TabPage("1단계: 서버 연결") { Name="HostConnectionPage" }; legacyPage.Controls.Add(settingsLayout);
            var ownerPage=new TabPage("2·3단계: 서버 ID·PW") { Name="HostOwnerPage" };
            var ownerRoot=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(6) };
            ownerRoot.RowStyles.Add(new RowStyle(SizeType.Absolute,42));ownerRoot.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var authRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=4,RowCount=1 };
            authRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));authRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));authRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160));authRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            authRow.Controls.Add(new Label {Text="서버 관리키",AutoSize=true,Padding=new Padding(0,6,0,0)},0,0);
            authRow.Controls.Add(ownerSecretBox,1,0);
            hostLoadButton=Button("인증 / 현재 설정 조회",HostLoadSettings);hostLoadButton.Name="HostLoadSettings";authRow.Controls.Add(hostLoadButton,2,0);
            authRow.Controls.Add(CreateHostHelpButton("HelpOwnerKey","? 관리키",HostHelpForm.OwnerKey),3,0);
            ownerRoot.Controls.Add(authRow,0,0);
            var management=new TabControl { Name="OwnerManagementTabs",Dock=DockStyle.Fill };
            var roomPage=new TabPage("서버 ID / PW") {Name="RoomSettingsPage"};
            var settings=new TabPage("표시 이름 (고급)") {Name="OwnerSettingsPage"};
            var publishing=new TabPage("외부 게시 (고급)") {Name="OwnerPublishingPage"};
            var devicesPage=new TabPage("등록 장치 (고급)") {Name="OwnerDevicesPage"};
            var room=OwnerGrid();
            room.ColumnCount=3;room.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var roomSteps=new Label {Text="2단계: 위에 서버 관리키를 입력하고 [불러오기]로 서버의 실제 상태를 확인합니다.\r\n3단계: 서버 ID와 서버 PW를 정해 [저장]한 뒤 [복사]해 참가자에게 전달합니다. (관리키·서버 ID·PW는 [입력 기억]이 켜져 있으면 이 Windows 사용자 계정에 암호화되어 미확인 초안으로 저장됩니다)",AutoSize=true,MaximumSize=new Size(600,0)};
            room.Controls.Add(roomSteps,0,0);room.SetColumnSpan(roomSteps,3);
            AddOwnerRow(room,"서버 ID",roomServerIdBox,1);
            roomLoadButton=Button("불러오기",RoomLoad);roomLoadButton.Name="RoomLoad";room.Controls.Add(roomLoadButton,2,1);
            AddOwnerRow(room,"서버 PW",roomJoinPasswordBox,2);
            roomSaveButton=Button("저장",RoomSave);roomSaveButton.Name="RoomSave";room.Controls.Add(roomSaveButton,2,2);
            AddOwnerRow(room,"상태",roomStatusBox,3);
            roomCopyButton=Button("복사",RoomCopy);roomCopyButton.Name="RoomCopy";roomCopyButton.Enabled=false;room.Controls.Add(roomCopyButton,2,3);
            var roomNote=new Label {Text="서버 ID: 영문/숫자/하이픈 3~32자(대문자는 저장 전 소문자로 변환). 서버 PW: 8~128자, 비우면 기존 PW 유지(저장된 PW는 조회 불가).\r\n기존 ID도 계속 사용 가능, 최대 8개; 삭제/이동 미지원.\r\n이 서버와 같은 곳의 디렉터리에 직접 게시하는 경우에만 바로 저장됩니다. 외부 서버(예: 별도 AWS)는 중앙 승인 코드가 필요하므로 [외부 게시 (고급)] 탭을 사용하세요.",AutoSize=true,MaximumSize=new Size(600,0),ForeColor=Color.DimGray};
            room.Controls.Add(roomNote,0,4);room.SetColumnSpan(roomNote,3);
            var roomHelp=HelpRow(CreateHostHelpButton("HelpRoomSettings","? 서버 ID / PW",HostHelpForm.RoomSettings));
            room.Controls.Add(roomHelp,0,5);room.SetColumnSpan(roomHelp,3);
            roomServerIdBox.Leave+=(s,e)=>{string n=OwnerRoomInfo.Normalize(roomServerIdBox.Text);if(n!=roomServerIdBox.Text)roomServerIdBox.Text=n;};
            roomPage.Controls.Add(room);
            var owner=OwnerGrid();
            AddOwnerRow(owner,"현재 표시 이름",currentDisplay,0);
            AddOwnerRow(owner,"참가 비밀번호 상태",passwordState,1);
            AddOwnerRow(owner,"새 표시 이름",hostDisplayNameBox,2);
            AddOwnerRow(owner,"새 참가 비밀번호",hostJoinPasswordBox,3);
            hostSaveButton=Button("설정 저장",HostSaveSettings);hostSaveButton.Name="HostSaveSettings";owner.Controls.Add(hostSaveButton,1,4);
            owner.Controls.Add(new Label {Text="새 비밀번호를 비우면 기존 값 유지 · 입력 시 8~128자로 변경",AutoSize=true,MaximumSize=new Size(450,0)},1,5);
            owner.Controls.Add(aliasMirror,1,6);
            owner.Controls.Add(new Label {Text="표시 이름은 게스트 접속 이름 및 공개 프로필 이름과 다릅니다.",AutoSize=true,MaximumSize=new Size(450,0)},1,7);
            owner.Controls.Add(HelpRow(CreateHostHelpButton("HelpDisplayName","? 표시 이름",HostHelpForm.DisplayName),CreateHostHelpButton("HelpJoinPassword","? 참가 비밀번호",HostHelpForm.JoinPassword)),1,8);
            settings.Controls.Add(owner);
            var publish=OwnerGrid();
            publish.ColumnCount=3;publish.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));
            AddOwnerRow(publish,"확인할 접속 이름",aliasInput,0);
            verifyAliasButton=Button("접속 이름 확인",VerifyHostAlias);verifyAliasButton.Name="VerifyHostAlias";publish.Controls.Add(verifyAliasButton,2,0);
            AddOwnerRow(publish,"확인된 접속 이름",publishedAlias,1);
            copyAliasButton=Button("접속 이름 복사",CopyHostAlias);copyAliasButton.Name="CopyHostAlias";copyAliasButton.Enabled=false;publish.Controls.Add(copyAliasButton,2,1);
            AddOwnerRow(publish,"게시 승인 코드",hostPublishGrantBox,2);
            hostPublishButton=Button("서버 이름 게시",HostPublish);hostPublishButton.Name="HostPublish";publish.Controls.Add(hostPublishButton,2,2);
            var publishHelp=HelpRow(CreateHostHelpButton("HelpAliasInput","? 접속 이름",HostHelpForm.AliasInput),CreateHostHelpButton("HelpVerifiedAlias","? 확인된 이름",HostHelpForm.VerifiedAlias),CreateHostHelpButton("HelpPublishGrant","? 승인 코드 / 최초 게시",HostHelpForm.PublishGrant));
            publishHelp.MinimumSize=new Size(0,42);publish.Controls.Add(publishHelp,0,3);publish.SetColumnSpan(publishHelp,3);
            var publishNote=new Label {Text="최초 게시에는 공통 서버 ID 조회 서비스 관리자의 승인이 필요하며 자동 승인되지 않습니다. 기존 접속 이름은 위에서 확인할 수 있습니다.",AutoSize=true,MaximumSize=new Size(560,0)};
            publish.Controls.Add(publishNote,0,4);publish.SetColumnSpan(publishNote,3);
            publishing.Controls.Add(publish);
            var deviceLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(8)};
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            deviceLayout.Controls.Add(ownerDevicesList,0,0);
            var deviceActions=new FlowLayoutPanel {Dock=DockStyle.Fill};
            ownerListButton=Button("장치 목록",HostListDevices);ownerListButton.Name="OwnerListDevices";deviceActions.Controls.Add(ownerListButton);
            ownerRevokeButton=Button("선택 장치 해제",HostRevokeDevice);ownerRevokeButton.Name="RevokeDevice";deviceActions.Controls.Add(ownerRevokeButton);
            deviceActions.Controls.Add(CreateHostHelpButton("HelpRegisteredDevices","? 등록 장치 / 해제",HostHelpForm.RegisteredDevices));
            deviceLayout.Controls.Add(deviceActions,0,1);devicesPage.Controls.Add(deviceLayout);
            management.TabPages.Add(roomPage);management.TabPages.Add(settings);management.TabPages.Add(publishing);management.TabPages.Add(devicesPage);
            ownerRoot.Controls.Add(management,0,1);ownerPage.Controls.Add(ownerRoot);
            hostTabs.TabPages.Add(legacyPage); hostTabs.TabPages.Add(ownerPage);
            legacyPage.BackColor=Color.FromArgb(247,249,252); ownerPage.BackColor=Color.FromArgb(247,249,252);

            content.Controls.Add(hostTabs); content.Controls.Add(guestPanel);
            outer.Controls.Add(content,0,1);
            settingsPage.Controls.Add(outer);
            roleBox.SelectedIndexChanged+=(s,e)=>{bool host=roleBox.SelectedIndex==1;hostTabs.Visible=host;guestPanel.Visible=!host;};
        }
        private static TableLayoutPanel OwnerGrid(){var p=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(8),AutoScroll=true};p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return p;}
        private static void AddOwnerRow(TableLayoutPanel p,string caption,Control control,int row){p.Controls.Add(new Label {Text=caption,AutoSize=true,Padding=new Padding(0,5,0,0)},0,row);p.Controls.Add(control,1,row);}
        private Button CreateHostHelpButton(string name,string text,string topic){var b=Button(text,()=>ShowHostHelp(topic));b.Name=name;b.TabStop=true;return b;}
        private static FlowLayoutPanel HelpRow(params Control[] buttons){var p=new FlowLayoutPanel {AutoSize=true,WrapContents=true,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,0)};p.Controls.AddRange(buttons);return p;}
        private void ShowHostHelp(string topic)
        {
            if(IsDisposed||Disposing)return;
            if(hostHelp==null||hostHelp.IsDisposed){hostHelp=new HostHelpForm();hostHelp.FormClosed+=(s,e)=>hostHelp=null;hostHelp.ShowTopic(topic);hostHelp.Show(this);return;}
            hostHelp.ShowTopic(topic);
            if(hostHelp.WindowState==FormWindowState.Minimized)hostHelp.WindowState=FormWindowState.Normal;
            hostHelp.Activate();
        }
        private static FlowLayoutPanel Card(string title)
        {
            var card=new Panel { Dock=DockStyle.Fill, BackColor=Color.White, Padding=new Padding(16,12,16,12), Margin=new Padding(6) };
            var flow=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoScroll=true, BackColor=Color.White };
            flow.Controls.Add(new Label { Text=title, AutoSize=true, MaximumSize=new Size(290,0), Font=new Font("Malgun Gothic",11f,FontStyle.Bold), ForeColor=Color.FromArgb(34,65,105), Margin=new Padding(4,0,4,10) });
            card.Controls.Add(flow);
            return flow;
        }
        private static Label Caption(string text) { return new Label { Text=text,AutoSize=true,ForeColor=Color.FromArgb(96,108,124),Margin=new Padding(4,4,4,0) }; }
        private static FlowLayoutPanel Row(string label) { var p=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,WrapContents=true };p.Controls.Add(new Label { Text=label,AutoSize=true });return p; }
        private static Button Button(string text,Action action) { var b=new Button { Text=text,AutoSize=true,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(226,237,250),ForeColor=Color.FromArgb(34,65,105),Margin=new Padding(4) };b.FlatAppearance.BorderColor=Color.FromArgb(175,198,226);b.Click+=(s,e)=>action();return b; }
        private static void ApplyTheme(Control parent)
        {
            foreach(Control child in parent.Controls)
            {
                if(child is GroupBox) child.BackColor=Color.White;
                if(child is TextBox) child.BackColor=Color.White;
                ApplyTheme(child);
            }
        }
        private void OpenServerSettings(){settingsTabs.SelectedTab=serverPage;}
        private async void OnShown(object sender,EventArgs e)
        {
            if(settingsInvalid){settingsTabs.SelectedTab=serverPage;status.Text=historyWriteWarning;SetRegistered(false);return;}
            if(!storedCredentialPending){settingsTabs.SelectedTab=serverPage;status.Text=directoryBootstrap!=null?"미등록 — 서버 주인에게 받은 서버 ID와 서버 PW를 입력하세요.":(String.IsNullOrWhiteSpace(profileBox.Text)?"미등록 — 참가자는 서버 주인에게 받은 서버 ID/PW를, 서버 주인은 서버 주소·지문을 먼저 입력하세요.":"미등록 — 서버 주인은 [서버 연결 확인] 후 서버 관리키로 인증하고 서버 ID/PW를 정하세요.");SetRegistered(false);return;}
            await ResumeStoredRegistrationAsync();
        }
        private async Task ResumeStoredRegistrationAsync()
        {
            if(lifecycleBusy||shutdownBusy||storedSettings==null||!storedSettings.IsRegistered)return;
            lifecycleBusy=true;bool acquired=false;
            SetRegistered(false);profileBox.ReadOnly=true;deviceBox.Text=storedSettings.DeviceId??"";ownPasswordBox.Clear();expiryLabel.Text="";
            status.Text="저장된 장치 등록을 서버에서 확인하고 새 임시 비밀번호를 발급합니다.";
            try
            {
                await lifecycleGate.WaitAsync();acquired=true;
                var profile=ServerProfile.Parse(storedSettings.ProfileJson);
                var candidate=new ControlPlaneClient(profile);
                candidate.RestoreCredentials(storedSettings.DeviceId,storedSettings.BearerToken,storedSettings.TemporaryPassword,storedSettings.PasswordExpiresAt);
                DeviceIdentity identity=await candidate.ValidateDeviceAsync();
                if(!String.Equals(identity.DeviceId,storedSettings.DeviceId,StringComparison.Ordinal))
                    throw new InvalidDataException("서버의 장치 ID가 저장된 ID와 다릅니다.");
                if(shutdownBusy)return;
                await Task.Run(()=>candidate.RotatePassword());
                if(shutdownBusy)return;
                var updated=BuildSettingsState(candidate);
                settingsStore.Save(updated);
                storedSettings=updated;client=candidate;registrationVerified=true;storedCredentialPending=true;registrationRejected=false;
                ShowPassword(candidate);
                status.Text="온라인 · 등록 확인 완료. 재실행 시에도 새 임시 비밀번호가 발급됩니다.";
            }
            catch(UnauthorizedAccessException ex)
            {
                client=null;registrationVerified=false;registrationRejected=true;ownPasswordBox.Clear();expiryLabel.Text="";
                status.Text="재등록 필요: "+ex.Message;
            }
            catch(Exception ex)
            {
                client=null;registrationVerified=false;ownPasswordBox.Clear();expiryLabel.Text="";
                status.Text="미연결/검증 전 — 저장된 ID만 표시합니다. "+ex.Message;
            }
            finally
            {
                if(acquired)lifecycleGate.Release();
                lifecycleBusy=false;
                if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);
            }
        }
        private SettingsState BuildSettingsState(ControlPlaneClient c) { return BuildSettingsState(c,profileBox.Text); }
        private SettingsState BuildSettingsState(ControlPlaneClient c,string profileJson)
        {
            return new SettingsState { ProfileJson=profileJson,IsRegistered=true,DeviceId=c.DeviceId,BearerToken=c.Token,
                TemporaryPassword=c.TemporaryPassword,PasswordExpiresAt=c.PasswordExpiresAt,
                BoundServerUrl=c.ServerUrl,BoundCertificateSha256=c.CertificateSha256,PasswordAutoAccess=passwordAutoAccess.Checked,SavedInputs=CloneStoredSavedInputs() };
        }
        private void PersistPublicProfile(string json)
        {
            if(settingsInvalid)throw new InvalidDataException("기존 설정 파일이 손상되어 덮어쓰지 않습니다.");
            var next=new SettingsState { ProfileJson=json??"",SavedInputs=CloneStoredSavedInputs() };
            settingsStore.Save(next);
            storedSettings=new SettingsState { ProfileJson=json??"",SavedInputs=CloneStoredSavedInputs() };
        }

        private void ImportProfile()
        {
            try { using(var d=new OpenFileDialog { Filter="JSON profile|*.json|All files|*.*" }) if(d.ShowDialog()==DialogResult.OK) {
                byte[] bytes; using(var fs=new FileStream(d.FileName,FileMode.Open,FileAccess.Read,FileShare.Read)) { if(fs.Length>16384)throw new InvalidOperationException("프로필 파일은 16KB 이하여야 합니다.");bytes=new byte[(int)fs.Length];int read=0;while(read<bytes.Length){int n=fs.Read(bytes,read,bytes.Length-read);if(n==0)throw new EndOfStreamException();read+=n;} }
                string text=new UTF8Encoding(false,true).GetString(bytes);ServerProfile.Parse(text);profileBox.Text=text;
                if(!storedCredentialPending)PersistPublicProfile(text);
            }} catch(Exception ex){ShowError(ex);}
        }
        private void ExportProfile() { try { ServerProfile.Parse(profileBox.Text);using(var d=new SaveFileDialog { Filter="JSON profile|*.json",FileName="server-profile.json" })if(d.ShowDialog()==DialogResult.OK)File.WriteAllText(d.FileName,profileBox.Text,new UTF8Encoding(false)); } catch(Exception ex){ShowError(ex);} }
        private ServerProfile ReadProfile(){return ServerProfile.Parse(profileBox.Text);}
        private static bool IsProfileValid(string text){try{ServerProfile.Parse(text);return true;}catch{return false;}}
        private void OnSimpleFieldChanged(object sender,EventArgs e)
        {
            if(syncingProfile||profileBox.ReadOnly)return;
            PreserveHostBeforeProfileChange(true);
            InvalidateAutomation();
            InvalidateHostState();
            string address=serverAddressBox.Text.Trim();
            var data=new Dictionary<string,object> { {"Version",1},{"Name",serverNameBox.Text.Trim()},{"RendezvousUrl",address},{"RelayUrl",address},{"CertificateSha256",serverFingerprintBox.Text.Trim()} };
            syncingProfile=true;
            try{profileBox.Text=new JavaScriptSerializer().Serialize(data);}
            finally{syncingProfile=false;}
            ReconcileOwnerKey();
            OnHostContextChanged();
            UpdateServerSettingsState();
        }
        private void OnProfileJsonChanged(object sender,EventArgs e)
        {
            if(syncingProfile)return;
            PreserveHostBeforeProfileChange();
            InvalidateAutomation();
            InvalidateHostState();
            SyncFieldsFromProfileJson();
            ReconcileOwnerKey();
            OnHostContextChanged();
            UpdateServerSettingsState();
        }
        private void SyncFieldsFromProfileJson()
        {
            ServerProfile p;
            try{p=ServerProfile.Parse(profileBox.Text);}catch{return;}
            syncingProfile=true;
            try{serverNameBox.Text=p.Name;serverAddressBox.Text=p.RendezvousUrl;serverFingerprintBox.Text=p.CertificateSha256;}
            finally{syncingProfile=false;}
        }
        private void UpdateServerSettingsState()
        {
            if(healthButton==null)return;
            string error=null;
            if(String.IsNullOrWhiteSpace(profileBox.Text))error="서버 주소와 인증서 지문을 입력하세요.";
            else try{ServerProfile.Parse(profileBox.Text);}catch(Exception ex){error=ex.Message;}
            profileStateLabel.ForeColor=error==null?Color.DimGray:Color.DarkRed;
            profileStateLabel.Text=error!=null?"설정 확인 필요: "+error:(storedCredentialPending||registrationVerified?"등록에 사용 중인 서버 설정입니다. 변경하려면 프로필/등록 초기화가 필요합니다.":"설정 형식 확인됨 (연결 확인 전)");
            healthButton.Enabled=error==null&&!registrationVerified&&!storedCredentialPending&&!settingsInvalid&&!lifecycleBusy&&!shutdownBusy;
        }
        private async void CheckServerConnection()
        {
            if(lifecycleBusy||shutdownBusy||settingsInvalid||storedCredentialPending||registrationVerified)return;
            ServerProfile profile;
            try{profile=ReadProfile();}catch(Exception ex){status.Text="서버 설정 확인 필요: "+ex.Message;UpdateServerSettingsState();return;}
            lifecycleBusy=true;bool acquired=false;SetRegistered(false);
            status.Text="서버 연결을 확인하는 중입니다...";
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                if(shutdownBusy)return;
                await new ControlPlaneClient(profile).CheckHealthAsync();
                if(shutdownBusy)return;
                status.Text="서버 연결 확인됨 — 다음: 서버 관리키로 인증한 뒤 [서버 ID / PW]를 정해 저장하세요.";
            } catch(Exception ex){if(!shutdownBusy)status.Text="서버 연결 확인 실패: "+ex.Message;}
            finally {if(acquired)lifecycleGate.Release();lifecycleBusy=false;if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);}
        }
        private ControlPlaneClient RegisteredClient(){if(client==null||String.IsNullOrEmpty(client.DeviceId)||!registrationVerified)throw new InvalidOperationException("장치 등록을 서버에서 확인해야 합니다.");return client;}
        private async void Register()
        {
            if(lifecycleBusy||shutdownBusy||settingsInvalid)return;
            lifecycleBusy=true;bool acquired=false;SetRegistered(false);
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                if(String.IsNullOrWhiteSpace(inviteBox.Text))throw new InvalidOperationException("초대 코드를 입력하십시오.");
                var candidate=new ControlPlaneClient(ReadProfile());string invitation=inviteBox.Text;
                await Task.Run(()=>candidate.Register(invitation));
                DeviceIdentity identity=await candidate.ValidateDeviceAsync();
                if(!String.Equals(identity.DeviceId,candidate.DeviceId,StringComparison.Ordinal))
                    throw new InvalidDataException("서버가 확인한 장치 ID가 등록 응답과 다릅니다.");
                var saved=BuildSettingsState(candidate);
                settingsStore.Save(saved);
                storedSettings=saved;storedCredentialPending=true;registrationRejected=false;client=candidate;registrationVerified=true;
                inviteBox.Clear();deviceBox.Text=candidate.DeviceId;ShowPassword(candidate);UpdateHistoryList();
                status.Text="등록 완료. ID와 임시 비밀번호가 이 Windows 사용자 계정에 암호화되어 보관됩니다.";
            } catch(Exception ex){ShowError(ex);}
            finally {if(acquired)lifecycleGate.Release();lifecycleBusy=false;if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);}
        }
        private async void GuestJoin()
        {
            if(lifecycleBusy||shutdownBusy||settingsInvalid)return;
            string alias=OwnerRoomInfo.Normalize(guestAliasBox.Text),password=guestPasswordBox.Text;
            if(alias!=guestAliasBox.Text)guestAliasBox.Text=alias;
            lifecycleBusy=true;bool acquired=false;SetRegistered(false);
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                if(directoryBootstrap==null)throw new InvalidOperationException(guestStateLabel.Text);
                if(registrationVerified||(storedCredentialPending&&!registrationRejected))throw new InvalidOperationException("이미 등록된 서버가 있습니다. 먼저 프로필/등록 초기화를 수행하세요.");
                if(!DirectoryClient.IsValidAlias(alias))throw new InvalidOperationException(OwnerRoomInfo.ServerIdRuleMessage);
                if(password.Length<8||password.Length>128)throw new InvalidOperationException("참가 비밀번호는 8~128자여야 합니다.");
                long inputRevision;var previousTarget=CaptureGuestBeforeLookup(alias,password,out inputRevision);
                var profile=await new DirectoryClient(directoryBootstrap).LookupAsync(alias);
                if(shutdownBusy)return;
                RequireGuestTarget(previousTarget,profile);
                string profileJson=DirectoryClient.ProfileJson(profile);
                var candidate=new ControlPlaneClient(profile);
                await candidate.CheckHealthAsync();
                if(shutdownBusy)return;
                await Task.Run(()=>candidate.Register(password));
                DeviceIdentity identity=await candidate.ValidateDeviceAsync();
                if(!String.Equals(identity.DeviceId,candidate.DeviceId,StringComparison.Ordinal))
                    throw new InvalidDataException("서버가 확인한 장치 ID가 등록 응답과 다릅니다.");
                var saved=BuildSettingsState(candidate,profileJson);
                saved.SavedInputs=GuestJoinSavedInputs(alias,password,profile,inputRevision);
                settingsStore.Save(saved);
                storedSettings=saved;storedCredentialPending=true;registrationRejected=false;client=candidate;registrationVerified=true;
                profileBox.Text=profileJson;ClearOwnerKey();
                SetVerifiedAlias(alias,profile);
                deviceBox.Text=candidate.DeviceId;ShowPassword(candidate);UpdateHistoryList();
                status.Text="참가 완료. ID와 임시 비밀번호가 이 Windows 사용자 계정에 암호화되어 보관됩니다.";
            } catch(Exception ex){ShowError(ex);}
            finally {password=null;guestPasswordBox.Clear();if(acquired)lifecycleGate.Release();lifecycleBusy=false;if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);}
        }
        private async Task OwnerActionAsync(Func<ControlPlaneClient,string,Task<string>> action,params TextBox[] secrets)
        {
            if(ownerBusy||lifecycleBusy||shutdownBusy)return;
            if(ownerSecretBox.Text.Length<32||ownerSecretBox.Text.Length>1024){ClearOwnerSummary();ClearRoom();status.Text=ControlPlaneClient.OwnerSecretRequiredMessage;ownerSecretBox.Focus();return;}
            if(!OwnerKeyBoundToCurrent()){ClearOwnerKey();ClearOwnerSummary();ClearRoom();status.Text=OwnerKeyRebindMessage;ownerSecretBox.Focus();return;}
            string draftError;CaptureHostDraft(false,out draftError);if(draftError!=null)SetSavedInputsStatus(draftError,true);
            string secret=ownerSecretBox.Text,binding=ownerKeyBinding;long inputRevision=savedInputsRevision;
            ownerBusy=true;lifecycleBusy=true;bool acquired=false;SetRegistered(registrationVerified);SetOwnerEnabled();
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                var target=ReadProfile();
                if(OwnerBinding(target)!=binding||ownerKeyBinding!=binding||!String.Equals(ownerSecretBox.Text,secret,StringComparison.Ordinal)){ClearOwnerKey();throw new InvalidOperationException(OwnerKeyRebindMessage);}
                var host=new ControlPlaneClient(target);
                string done=await action(host,secret);
                RecordOwnerSuccess(secret,binding,inputRevision);
                if(!shutdownBusy&&done!=null)status.Text=done;
            } catch(Exception ex){ClearOwnerSummary();string inline=roomStatusBox.Text;bool roomFailed=roomFailureActive;ClearRoom();if(roomFailed)roomStatusBox.Text=inline;if(!shutdownBusy)status.Text="실패: "+ex.Message;}
            finally {
                roomFailureActive=false;
                secret=null;binding=null;hostJoinPasswordBox.Clear();hostPublishGrantBox.Clear();foreach(var box in secrets)box.Clear();
                if(acquired)lifecycleGate.Release();ownerBusy=false;lifecycleBusy=false;if(!IsDisposed&&!shutdownBusy){SetRegistered(registrationVerified);SetOwnerEnabled();}
            }
        }
        private void SetOwnerEnabled()
        {
            bool on=!ownerBusy&&!lifecycleBusy&&!shutdownBusy;
            if(hostLoadButton!=null)hostLoadButton.Enabled=on;
            if(verifyAliasButton!=null)verifyAliasButton.Enabled=on&&directoryBootstrap!=null;
            if(copyAliasButton!=null)copyAliasButton.Enabled=on&&verifiedAlias!=null;
            ownerSecretBox.ReadOnly=!on;hostDisplayNameBox.ReadOnly=!on;hostJoinPasswordBox.ReadOnly=!on;hostPublishGrantBox.ReadOnly=!on;aliasInput.ReadOnly=!on;
            roomServerIdBox.ReadOnly=!on;roomJoinPasswordBox.ReadOnly=!on;
            if(roomLoadButton!=null)roomLoadButton.Enabled=on;
            if(roomSaveButton!=null)roomSaveButton.Enabled=on&&roomSelfPublish!=false;
            if(roomCopyButton!=null)roomCopyButton.Enabled=on&&roomVerifiedId!=null;
            hostSaveButton.Enabled=on;hostPublishButton.Enabled=on&&directoryBootstrap!=null;ownerListButton.Enabled=on;ownerRevokeButton.Enabled=on;
            UpdateSavedInputsEnabled();
        }
        private async void HostLoadSettings(){await OwnerActionAsync(async (host,secret)=>{ClearOwnerSummary();var d=await host.OwnerGetSettingsAsync(secret);ApplyOwnerSettings(d);return "관리키 인증 후 현재 설정을 확인했습니다.";});}
        private const string OwnerKeyRebindMessage="서버 관리키가 현재 연결 설정(주소·전체 지문)에 묶여 있지 않아 보내지 않았습니다. 올바른 연결 설정을 확인한 뒤 관리키를 다시 입력하세요.";
        private bool roomFailureActive;
        private static void RequireRoomProfile(OwnerRoomInfo info,ServerProfile expected)
        {
            ServerProfile actual;
            try{actual=ServerProfile.Parse(info.ProfileJson);}catch(Exception ex){throw new InvalidOperationException("서버가 보낸 프로필이 올바르지 않습니다: "+ex.Message);}
            if(ProfileBinding(actual)!=ProfileBinding(expected))throw new InvalidOperationException("서버가 보낸 프로필의 주소·지문이 현재 연결 설정과 다릅니다.");
        }
        private const string RoomDirectoryMismatch="이 앱의 공통 조회 서버와 다른 서버입니다. 외부 게시(고급)에서 최초 승인 코드가 필요합니다. 조회 상태만으로 공유할 수 없습니다.";
        private bool IsRoomDirectoryTrusted(ServerProfile profile)
        {
            if(directoryBootstrap==null||profile==null)return false;
            try {
                directoryBootstrap.Validate();
                return new Uri(directoryBootstrap.RegistryUrl).Equals(new Uri(profile.RendezvousUrl))
                    &&String.Equals(directoryBootstrap.CertificateSha256,profile.CertificateSha256,StringComparison.OrdinalIgnoreCase);
            }catch{return false;}
        }
        private void ApplyRoom(OwnerRoomInfo info,ServerProfile expected,string sharedPassword,string action)
        {
            roomSelfPublish=info.SelfPublishEnabled;
            roomVerifiedId=info.ServerId;roomBinding=info.ServerId==null?null:ProfileBinding(expected);
            roomSharedPassword=info.ServerId==null?null:sharedPassword;
            bool discoverable=IsRoomDirectoryTrusted(expected);
            if(!discoverable){roomVerifiedId=null;roomBinding=null;roomSharedPassword=null;}
            if(info.ServerId!=null)roomServerIdBox.Text=info.ServerId;
            var sb=new StringBuilder();
            sb.Append(action).Append("\r\n현재 서버 ID: ").Append(info.ServerId??"없음");
            sb.Append("\r\n서버 PW: ").Append(info.JoinPasswordConfigured?"설정됨 (원문 조회 불가)":"미설정");
            sb.Append("\r\n유지 중인 ID (").Append(info.ServerIds.Count).Append("/8): ").Append(info.ServerIds.Count==0?"없음":String.Join(", ",info.ServerIds));
            sb.Append("\r\n직접 게시: ").Append(info.SelfPublishEnabled?"사용 가능":"이 서버에서 사용 불가 — 외부 게시(고급)에서 중앙 승인 코드 필요");
            if(info.ServerId!=null&&discoverable)sb.Append("\r\n복사: ").Append(roomSharedPassword!=null?"서버 ID와 이번에 설정한 서버 PW":"서버 ID만 (이 창에서 PW를 새로 설정하지 않아 PW는 복사되지 않음)");
            if(!discoverable)sb.Append("\r\n주의: ").Append(RoomDirectoryMismatch);
            roomStatusBox.Text=sb.ToString();
        }
        private async void RoomLoad()
        {
            await OwnerActionAsync(async (host,secret)=>{
                ClearRoom();roomFailureActive=true;
                roomStatusBox.Text="서버에서 서버 ID 상태를 확인하는 중...";
                var expected=ReadProfile();
                try{
                    var info=await host.OwnerGetRoomAsync(secret);
                    RequireRoomProfile(info,expected);
                    ApplyRoom(info,expected,null,"서버에서 확인한 현재 상태입니다.");
                    roomFailureActive=false;
                    return "관리키 인증 후 서버 ID 상태를 확인했습니다.";
                }catch(Exception ex){roomStatusBox.Text="불러오기 실패: "+ex.Message;throw;}
            });
        }
        private async void RoomSave()
        {
            if(ownerBusy||lifecycleBusy||shutdownBusy)return;
            try{if(!IsRoomDirectoryTrusted(ReadProfile())){ClearRoom();roomStatusBox.Text=RoomDirectoryMismatch;status.Text=RoomDirectoryMismatch;return;}}
            catch(Exception ex){ClearRoom();roomStatusBox.Text="연결 설정 확인 필요: "+ex.Message;return;}
            string id=OwnerRoomInfo.Normalize(roomServerIdBox.Text);
            if(id!=roomServerIdBox.Text)roomServerIdBox.Text=id;
            if(!OwnerRoomInfo.IsValidServerId(id)){ClearRoom();roomStatusBox.Text=OwnerRoomInfo.ServerIdRuleMessage;roomServerIdBox.Focus();return;}
            // A restored PW draft is not dirty: it is sent only after the user edits the PW box (ID edits never resend it).
            string join=roomPasswordDirty?roomJoinPasswordBox.Text:"";long inputRevision=savedInputsRevision;
            if(join.Length>0&&(join.Length<8||join.Length>128)){join=null;ClearRoom();roomStatusBox.Text="서버 PW는 8~128자여야 합니다. 기존 PW를 유지하려면 비워 두세요.";roomJoinPasswordBox.Focus();return;}
            if(join.Length==0)join=null;
            if(ownerSecretBox.Text.Length<32||ownerSecretBox.Text.Length>1024){join=null;roomStatusBox.Text=ControlPlaneClient.OwnerSecretRequiredMessage;}
            await OwnerActionAsync(async (host,secret)=>{
                try {
                    ClearRoom();roomFailureActive=true;
                    roomStatusBox.Text="저장 중...";
                    var expected=ReadProfile();
                    var info=await host.OwnerSaveRoomAsync(secret,id,join);
                    RequireRoomProfile(info,expected);
                    if(info.ServerId==null)throw new InvalidOperationException("서버 응답에 현재 서버 ID가 없습니다.");
                    ApplyRoom(info,expected,join,"저장했습니다. 아래는 서버가 확인한 값입니다.");
                    RecordHostJoinSuccess(info.ServerId,join,inputRevision);
                    roomFailureActive=false;
                    return "서버 ID를 저장했습니다: "+info.ServerId;
                } catch(Exception ex) {
                    roomStatusBox.Text="저장이 확인되지 않았습니다(일부만 저장되는 경우는 없습니다). [불러오기]로 현재 상태를 확인하세요.\r\n사유: "+ex.Message;
                    throw;
                } finally {join=null;}
            },roomJoinPasswordBox);
        }
        private void RoomCopy()
        {
            if(ownerBusy||lifecycleBusy||shutdownBusy||roomVerifiedId==null)return;
            try{
                if(!IsRoomDirectoryTrusted(ReadProfile())||roomBinding!=ProfileBinding(ReadProfile())){ClearRoom();status.Text="연결 설정이 바뀌어 다시 [불러오기]가 필요합니다.";return;}
                if(roomSharedPassword!=null)CopyText("서버 ID : "+roomVerifiedId+"\r\n서버 PW : "+roomSharedPassword,"서버 ID와 서버 PW를 복사했습니다.");
                else CopyText(roomVerifiedId,"서버 ID만 복사했습니다. 저장된 PW는 조회할 수 없으므로 직접 전달하거나 새 PW를 저장하세요.");
            }catch(Exception ex){ClearRoom();ShowError(ex);}
        }
        private async void VerifyHostAlias()
        {
            if(lifecycleBusy||shutdownBusy)return;
            ClearRoom();string alias=aliasInput.Text.Trim();lifecycleBusy=true;bool acquired=false;SetRegistered(registrationVerified);ClearAlias();
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                var expected=ReadProfile();
                var found=await new DirectoryClient(directoryBootstrap).LookupAsync(alias);
                if(ProfileBinding(expected)!=ProfileBinding(found))throw new InvalidOperationException("접속 이름의 서버 주소·지문이 현재 서버와 다릅니다.");
                if(shutdownBusy)return;
                SetVerifiedAlias(alias,expected);status.Text="접속 이름과 현재 서버의 일치를 확인했습니다.";
            }catch(Exception ex){ClearAlias();if(!shutdownBusy)status.Text="실패: "+ex.Message;}
            finally {hostJoinPasswordBox.Clear();hostPublishGrantBox.Clear();if(acquired)lifecycleGate.Release();lifecycleBusy=false;if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);}
        }
        private async void HostSaveSettings()
        {
            ClearRoom();string name=hostDisplayNameBox.Text,join=hostJoinPasswordBox.Text;long inputRevision=savedInputsRevision;
            await OwnerActionAsync(async (host,secret)=>{
                try {
                    var d=await host.OwnerSettingsAsync(secret,name,join);
                    ApplyOwnerSettings(d);
                    if(!String.IsNullOrEmpty(join))RecordHostJoinSuccess(null,join,inputRevision);
                    return "서버 설정을 저장하고 현재 상태를 확인했습니다.";
                } finally {join=null;}
            },hostJoinPasswordBox);
        }
        private async void HostPublish()
        {
            string grant=hostPublishGrantBox.Text;
            await OwnerActionAsync(async (host,secret)=>{
                try {
                    if(directoryBootstrap==null)throw new InvalidOperationException("디렉터리 부트스트랩이 없어 게시할 수 없습니다.");
                    string alias=await new DirectoryClient(directoryBootstrap).PublishAsync(grant,host,secret);
                    SetVerifiedAlias(alias,ReadProfile());
                    return "디렉터리에 게시했습니다: "+alias;
                } finally {grant=null;}
            },hostPublishGrantBox);
        }
        private async void HostListDevices()
        {
            await OwnerActionAsync(async (host,secret)=>{
                var devices=await host.OwnerListDevicesAsync(secret);
                ownerDevicesList.Items.Clear();foreach(var d in devices)ownerDevicesList.Items.Add(d);
                return "등록 장치 "+devices.Count+"개";
            });
        }
        private async void HostRevokeDevice()
        {
            var selected=ownerDevicesList.SelectedItem as OwnerDevice;
            if(selected==null){status.Text="해제할 장치를 선택하세요.";return;}
            await OwnerActionAsync(async (host,secret)=>{
                await host.OwnerRevokeDeviceAsync(secret,selected.DeviceId);
                ownerDevicesList.Items.Remove(selected);
                return "장치 등록을 해제했습니다.";
            });
        }
        private void SetRegistered(bool value)
        {
            roleBox.Enabled=!lifecycleBusy&&!shutdownBusy;
            SetOwnerEnabled();
            bool active=value&&registrationVerified&&!lifecycleBusy&&!shutdownBusy;
            bool joinable=!active&&!settingsInvalid&&!lifecycleBusy&&!shutdownBusy&&(!storedCredentialPending||registrationRejected);
            guestJoinButton.Enabled=joinable&&directoryBootstrap!=null;guestAliasBox.Enabled=joinable;guestPasswordBox.Enabled=joinable;
            profileBox.ReadOnly=active||storedCredentialPending||settingsInvalid||lifecycleBusy||shutdownBusy;
            serverNameBox.ReadOnly=profileBox.ReadOnly;serverAddressBox.ReadOnly=profileBox.ReadOnly;serverFingerprintBox.ReadOnly=profileBox.ReadOnly;
            UpdateServerSettingsState();
            importButton.Enabled=!active&&!storedCredentialPending&&!settingsInvalid&&!lifecycleBusy;
            exportButton.Enabled=!settingsInvalid;
            resetButton.Enabled=(active||storedCredentialPending)&&!lifecycleBusy&&!settingsInvalid;
            registerButton.Enabled=!active&&!settingsInvalid&&!lifecycleBusy&&(!storedCredentialPending||registrationRejected);
            inviteBox.Enabled=!active&&!lifecycleBusy&&!settingsInvalid&&(!storedCredentialPending||registrationRejected);
            rotateButton.Enabled=active;
            copyIdButton.Enabled=active;copyPasswordButton.Enabled=active&&passwordSynchronized;copyAllButton.Enabled=active&&passwordSynchronized;
            requestButton.Enabled=active&&passwordSynchronized;targetBox.Enabled=active&&passwordSynchronized;targetPasswordBox.Enabled=active&&passwordSynchronized;
            refreshButton.Enabled=active;approveButton.Enabled=active;rejectButton.Enabled=active;connectButton.Enabled=active;
            viewerButton.Enabled=active;shareStartButton.Enabled=active;shareStopButton.Enabled=active;disconnectButton.Enabled=active;
        }
        private void ShowPassword(ControlPlaneClient c)
        {
            passwordSynchronized=true;
            DateTimeOffset deadline;
            passwordDeadlineMs=ControlPlaneClient.TryParseUtc(c.PasswordExpiresAt,out deadline)?passwordWatch.ElapsedMilliseconds+(long)Math.Max(0,(deadline-DateTimeOffset.UtcNow).TotalMilliseconds):passwordWatch.ElapsedMilliseconds;
            nextPasswordCheckMs=passwordWatch.ElapsedMilliseconds+15000;
            ownPasswordBox.Text=c.TemporaryPassword??"";
            DateTimeOffset expires;
            expiryLabel.Text=DateTimeOffset.TryParse(c.PasswordExpiresAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expires)
                ? "만료: "+expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm",CultureInfo.GetCultureInfo("ko-KR")) : "만료 시간 확인 불가";
            ownPasswordBox.UseSystemPasswordChar=!showOwnPassword;
        }
        private void ToggleOwnPassword(){showOwnPassword=!showOwnPassword;ownPasswordBox.UseSystemPasswordChar=!showOwnPassword;}
        private void HideUnsynchronizedPassword(string message)
        {
            passwordSynchronized=false;ownPasswordBox.Clear();expiryLabel.Text="임시 비밀번호 재확인 필요";status.Text=message;
        }
        private async Task RejectDeviceAuthenticationAsync()
        {
            registrationVerified=false;registrationRejected=true;
            HideUnsynchronizedPassword("장치 인증이 거부되었습니다. 다시 참가해야 합니다.");
            InvalidateAutomation();
            try{await StopShareCore();}catch{}
            if(viewer!=null&&!viewer.IsDisposed){try{await viewer.CloseSessionAsync();viewer.Close();viewer=null;}catch{}}
        }
        // Single UI-context coordinator: bounded requests, no overlap, profile/reset serialized by the same lifecycle gate.
        internal async Task RefreshPasswordStateAsync()
        {
            if(passwordPollBusy||lifecycleBusy||shutdownBusy||!registrationVerified||client==null)return;
            passwordPollBusy=true;lifecycleBusy=true;bool acquired=false;var current=client;
            lastPasswordCheckMs=passwordWatch.ElapsedMilliseconds;
            nextPasswordCheckMs=passwordWatch.ElapsedMilliseconds+15000;
            SetRegistered(true);
            try
            {
                await lifecycleGate.WaitAsync(passwordCancellation.Token);acquired=true;
                if(shutdownBusy||!ReferenceEquals(client,current))return;
                var identity=await current.ValidateDeviceAsync(passwordCancellation.Token);
                if(identity.DeviceId!=current.DeviceId)throw new UnauthorizedAccessException("장치 ID가 일치하지 않습니다.");
                if(shutdownBusy||!ReferenceEquals(client,current))return;
                double remaining=(identity.PasswordExpiresAtUtc-identity.ServerTimeUtc).TotalMilliseconds;
                passwordDeadlineMs=passwordWatch.ElapsedMilliseconds+(long)Math.Max(0,Math.Min(3600000,remaining));
                if(identity.RotationRequired||remaining<=0)
                {
                    HideUnsynchronizedPassword("새 임시 비밀번호를 갱신하는 중입니다. 기존 연결은 유지됩니다.");
                    await Task.Run(()=>current.RotatePassword(identity.PasswordGeneration,passwordCancellation.Token));
                    if(shutdownBusy||!ReferenceEquals(client,current))return;
                    var saved=BuildSettingsState(current);settingsStore.Save(saved);storedSettings=saved;
                    ShowPassword(current);status.Text="임시 비밀번호를 자동 변경했습니다. 기존 연결은 유지됩니다.";
                }
                else if(!current.HasPasswordGeneration||current.PasswordGeneration!=identity.PasswordGeneration)
                {
                    HideUnsynchronizedPassword("다른 창에서 임시 비밀번호가 변경되었습니다. 해당 창을 사용하거나 새 비밀번호를 직접 발급하세요.");
                    // Never steal another instance's freshly issued password by force-rotating.
                }
                else if(!passwordSynchronized)
                {
                    // A prior local save failure may be recovered without another server rotation.
                    var saved=BuildSettingsState(current);settingsStore.Save(saved);storedSettings=saved;ShowPassword(current);
                    status.Text="임시 비밀번호 저장 및 확인을 완료했습니다.";
                }
                nextPasswordCheckMs=passwordWatch.ElapsedMilliseconds+(long)Math.Max(1000,Math.Min(15000,remaining>0?remaining:15000));
            }
            catch(UnauthorizedAccessException){if(!shutdownBusy)await RejectDeviceAuthenticationAsync();}
            catch(PasswordGenerationConflictException){if(!shutdownBusy)HideUnsynchronizedPassword("다른 창이 먼저 변경했습니다. 다음 상태 확인을 기다립니다.");}
            catch(OperationCanceledException){ }
            catch(Exception){if(!shutdownBusy)HideUnsynchronizedPassword("임시 비밀번호 확인/저장 실패. 기존 연결은 유지하며 잠시 후 재확인합니다.");}
            finally
            {
                if(acquired)lifecycleGate.Release();passwordPollBusy=false;lifecycleBusy=false;
                if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);
                // Keep an unrecovered sharing-stop diagnostic visible across password cycles.
                if(!shutdownBusy&&!IsDisposed&&shareStopDiagnostic!=null&&!status.Text.StartsWith(shareStopDiagnostic,StringComparison.Ordinal))status.Text=shareStopDiagnostic+" | "+status.Text;
            }
        }
        private async void RotatePassword()
        {
            if(lifecycleBusy||shutdownBusy||!registrationVerified)return;
            lifecycleBusy=true;bool acquired=false;HideUnsynchronizedPassword("새 임시 비밀번호 발급 중입니다.");SetRegistered(true);
            try
            {
                await lifecycleGate.WaitAsync();acquired=true;var c=RegisteredClient();
                await Task.Run(()=>c.RotatePassword());if(shutdownBusy)return;
                var saved=BuildSettingsState(c);settingsStore.Save(saved);storedSettings=saved;ShowPassword(c);status.Text="새 임시 비밀번호가 발급되었습니다.";
            }
            catch(UnauthorizedAccessException){if(!shutdownBusy)await RejectDeviceAuthenticationAsync();}
            catch(Exception){if(!shutdownBusy)HideUnsynchronizedPassword("서버 변경/로컬 저장 실패로 재확인이 필요합니다. 기존 연결은 유지됩니다.");}
            finally{if(acquired)lifecycleGate.Release();lifecycleBusy=false;if(!shutdownBusy&&!IsDisposed)SetRegistered(registrationVerified);}
        }
        private bool CopyAllowed(){return registrationVerified&&client!=null&&!String.IsNullOrWhiteSpace(client.DeviceId)&&!lifecycleBusy&&!shutdownBusy;}
        private bool FreshPassword()
        {
            if(passwordSynchronized&&passwordWatch.ElapsedMilliseconds<passwordDeadlineMs&&!String.IsNullOrEmpty(client.TemporaryPassword)&&CredentialFormat.IsUnexpired(client.PasswordExpiresAt))return true;
            status.Text="임시 비밀번호가 만료되었습니다. 새 비밀번호를 눌러 다시 발급하세요.";return false;
        }
        private void CopyText(string text,string done){try{Clipboard.SetText(text);status.Text=done;}catch(Exception ex){ShowError(ex);}}
        private void CopyId(){if(!CopyAllowed())return;CopyText("ID : "+client.DeviceId,"장치 ID를 복사했습니다.");}
        private void CopyPassword(){if(!CopyAllowed()||!FreshPassword())return;CopyText("PW : "+client.TemporaryPassword,"임시 비밀번호를 복사했습니다.");}
        private void CopyAll(){if(!CopyAllowed()||!FreshPassword())return;CopyText("ID : "+client.DeviceId+"\r\nPW : "+client.TemporaryPassword,"ID와 임시 비밀번호를 복사했습니다.");}
        private async void ResetProfile()
        {
            if(lifecycleBusy||shutdownBusy)return;
            InvalidateAutomation();
            lifecycleBusy=true;Enabled=false;bool acquired=false;
            try {
                await lifecycleGate.WaitAsync();acquired=true;
                if(viewer!=null&&!viewer.IsDisposed){await viewer.CloseSessionAsync();viewer.Close();viewer=null;}
                await StopShareCore();RevokeKnownWithin(2000);
                settingsStore.ClearCredentials();
                InvalidateHostState();ClearOwnerKey();
                storedSettings=settingsStore.Load();storedCredentialPending=false;registrationVerified=false;registrationRejected=false;passwordSynchronized=false;
                client=null;known.Clear();sessions.Items.Clear();promptedSessions.Clear();deviceBox.Text="(미등록)";ownPasswordBox.Clear();expiryLabel.Text="등록 후 표시";targetBox.Clear();targetPasswordBox.Clear();inviteBox.Clear();guestPasswordBox.Clear();
                UpdateHistoryList();settingsTabs.SelectedTab=serverPage;status.Text="로컬 등록 정보를 초기화했습니다. 공개 서버 프로필과 연결 기록은 유지했습니다.";
            } catch(Exception ex){ShowError(ex);}
            finally {
                if(acquired)lifecycleGate.Release();
                lifecycleBusy=false;
                if(!shutdownBusy&&!IsDisposed){Enabled=true;SetRegistered(client!=null);}
            }
        }
        private void RequestSession()
        {
            if(lifecycleBusy||shutdownBusy)return;
            if(!registrationVerified||client==null){status.Text="서버에 먼저 참가해야 합니다.";return;}
            string target=targetBox.Text.Trim(),password=targetPasswordBox.Text;
            Run(()=>{try{var c=RegisteredClient();var item=c.Request(target,password);Add(item);Record(item);
                if(item.InitiatorDeviceId==c.DeviceId&&item.Status=="Pending")autoRequest=new AutoRequest { SessionId=item.SessionId,OwnId=c.DeviceId,TargetId=item.TargetDeviceId,Binding=ClientBinding(c),Client=c,Epoch=autoEpoch };
                status.Text=item.AutoApprovalEligible?"비밀번호가 확인되었습니다. 상대 PC의 자동 연결을 기다립니다.":"연결 요청을 보냈습니다. 상대의 승인을 기다립니다.";}finally{targetPasswordBox.Clear();}});
        }
        private void RefreshPending(){if(lifecycleBusy||shutdownBusy)return;Run(()=>{var c=RegisteredClient();foreach(var item in c.Pending()){bool wasKnown=known.Any(x=>x.SessionId==item.SessionId);Add(item);string other=item.TargetDeviceId==c.DeviceId?item.InitiatorDeviceId:item.TargetDeviceId;if(!wasKnown||!history.Contains(c.ServerUrl,c.CertificateSha256,other,item.Status))Record(item);}status.Text="대기 세션을 새로 고쳤습니다.";});}
        private void Decide(bool approve){if(lifecycleBusy||shutdownBusy)return;var s=Selected();if(s==null)return;Run(()=>{var updated=RegisteredClient().Decide(s.SessionId,approve);Add(updated);Record(updated);status.Text=approve?"승인했습니다. 대상 장치에서 별도로 화면 공유를 시작해야 합니다.":"요청을 거절했습니다.";});}
        private void Connect()
        {
            if(lifecycleBusy||shutdownBusy)return;
            var s=Selected();if(s==null)return;
            if(s.InitiatorDeviceId!=deviceBox.Text){MessageBox.Show(this,"연결은 요청한 장치에서 시작해야 합니다.");return;}
            Run(()=>{var updated=RegisteredClient().Connect(s.SessionId);Add(updated);Record(updated);status.Text=updated.Status=="Connected"?"서버가 연결됨 상태를 확인했습니다. 화면 보기는 별도로 열 수 있으며, 대상이 공유를 시작해야 합니다.":"서버 상태: "+updated.Status;});
        }
        private async void StartShare()
        {
            if(lifecycleBusy||shutdownBusy)return;
            long epoch=autoEpoch;var token=automationCancellation.Token;bool acquired=false;
            try {
                await lifecycleGate.WaitAsync(token);acquired=true;
                if(epoch!=autoEpoch||shutdownBusy||token.IsCancellationRequested)return;
                var s=Selected();if(s==null)return;var c=RegisteredClient();
                if(s.TargetDeviceId!=c.DeviceId){MessageBox.Show(this,"화면 공유는 대상 장치에서 시작해야 합니다.");return;}
                if(sharing!=null){MessageBox.Show(this,"이미 화면 공유가 있습니다. 먼저 중지하십시오.");return;}
                if(MessageBox.Show(this,"전체 화면이 전송되며 상대가 마우스와 키보드를 제어할 수 있습니다. 계속하시겠습니까?","화면·입력 공유 동의",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                if(epoch!=autoEpoch||shutdownBusy||token.IsCancellationRequested)return;
                var candidate=new MediaSharingSession(c,s.SessionId,autoCaptureScope){RequestStatefulInput=true};sharing=candidate;
                try {
                    await candidate.StartAsync(token);
                    if(epoch!=autoEpoch||shutdownBusy||token.IsCancellationRequested){
                        if(ReferenceEquals(sharing,candidate))sharing=null;
                        try{await candidate.StopAsync();}finally{candidate.Dispose();}return;
                    }
                    monitoredSharing=candidate;shareStopDiagnostic=null;
                    status.Text="● 화면·마우스·키보드 공유 중 — 언제든 중지할 수 있습니다.";
                }catch(Exception ex){if(ReferenceEquals(sharing,candidate))sharing=null;candidate.Dispose();if(!(ex is OperationCanceledException)&&!shutdownBusy)ShowError(ex);}
            }catch(OperationCanceledException){}
            catch(Exception ex){if(!shutdownBusy)ShowError(ex);}finally{if(acquired)lifecycleGate.Release();}
        }
        private async void StopShare(){if(shutdownBusy)return;InvalidateAutomation();await lifecycleGate.WaitAsync();try{await StopShareCore();}finally{lifecycleGate.Release();}}
        private async Task StopShareCore()
        {
            var active=sharing;sharing=null;
            if(ReferenceEquals(monitoredSharing,active))monitoredSharing=null;
            if(active==null)return;
            shareStopDiagnostic=null;string failure=null;
            try{await active.StopAsync();failure=active.LastError;}
            catch(Exception ex){failure=ex.Message;}
            finally{active.Dispose();}
            if(!shutdownComplete) {
                if(!String.IsNullOrEmpty(failure)){shareStopDiagnostic="화면·입력 전송은 중지했지만 서버 종료 확인에 실패했습니다: "+failure;status.Text=shareStopDiagnostic;}
                else status.Text=active.EndReason=="session-ended"?"상대방이 연결을 종료했습니다.":active.EndReason=="expired"?"연결 유효 시간이 만료되어 화면 공유가 종료되었습니다.":"화면 공유를 중지했습니다.";
            }
        }
        private void OpenViewer()
        {
            if(lifecycleBusy||shutdownBusy||lifecycleGate.CurrentCount==0)return;var s=Selected();if(s==null)return;
            try{var c=RegisteredClient();if(s.InitiatorDeviceId!=c.DeviceId||s.Status!="Connected"){MessageBox.Show(this,"연결한 장치의 연결 완료 세션만 볼 수 있습니다.");return;}
                if(viewer!=null&&!viewer.IsDisposed){viewer.Activate();return;}viewer=new ViewerForm(c,s.SessionId);viewer.FormClosed+=(a,b)=>viewer=null;viewer.Show(this);
            }catch(Exception ex){ShowError(ex);}
        }
        private void Revoke(){if(lifecycleBusy||shutdownBusy)return;var s=Selected();if(s==null)return;DropAuthority(s.SessionId);Run(()=>{var item=RegisteredClient().Revoke(s.SessionId);Add(item);Record(item);status.Text="서버가 세션 취소를 확인했습니다.";});}
        private void Add(SessionItem item){int i=known.FindIndex(x=>x.SessionId==item.SessionId);if(i>=0)known[i]=item;else known.Add(item);Rebuild();}
        private void Rebuild(){var selected=Selected();sessions.BeginUpdate();sessions.Items.Clear();foreach(var item in known)sessions.Items.Add(item);if(selected!=null)for(int i=0;i<sessions.Items.Count;i++)if(((SessionItem)sessions.Items[i]).SessionId==selected.SessionId)sessions.SelectedIndex=i;sessions.EndUpdate();}
        private SessionItem Selected(){return sessions.SelectedItem as SessionItem;}
        private void Record(SessionItem item)
        {
            try{var c=RegisteredClient();string other=item.TargetDeviceId==c.DeviceId?item.InitiatorDeviceId:item.TargetDeviceId;
                history.Add(c.ServerUrl,c.CertificateSha256,other,item.Status);UpdateHistoryList();}
            catch(Exception ex){historyWriteWarning="상태는 서버 응답 기준이며 기록 저장 실패: "+ex.Message;historyWarningLabel.Text=historyWriteWarning;}
        }
        private void UpdateHistoryList()
        {
            historyList.Items.Clear();historyWarningLabel.Text=history.Warning??historyWriteWarning??"";if(client==null)return;
            foreach(var item in history.ForServer(client.ServerUrl,client.CertificateSha256))historyList.Items.Add(item);
        }
        private void HistoryDoubleClick(object sender,EventArgs e)
        {
            var item=historyList.SelectedItem as ConnectionHistoryRecord;if(item==null)return;
            targetBox.Text=item.TargetDeviceId;targetPasswordBox.Clear();settingsTabs.SelectedTab=homePage;
            status.Text="최근 연결한 상대 ID를 불러왔습니다. 임시 PW 또는 고정 PW를 입력하면 바로 연결하며, 비워 두면 상대에게 승인을 요청합니다.";
        }
        private void Run(Action action){try{action();}catch(Exception ex){ShowError(ex);}}
 
        private void ShowError(Exception ex){status.Text="실패: "+ex.Message;MessageBox.Show(this,ex.Message,"오류",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        private void RevokeKnownWithin(int budgetMs)
        {
            if(client==null)return;var watch=Stopwatch.StartNew();
            foreach(var s in known.Where(x=>x.Status=="Pending"||x.Status=="Approved"||x.Status=="Connected"))
            {int remaining=budgetMs-(int)watch.ElapsedMilliseconds;if(remaining<=0)break;try{client.Revoke(s.SessionId,remaining);}catch{}}
        }
        private async void OnClosing(object sender,FormClosingEventArgs e)
        {
            if(shutdownComplete)return;e.Cancel=true;if(shutdownBusy)return;if(!PersistInputsOnClose())return;shutdownBusy=true;InvalidateAutomation();snapshotTimer.Stop();ClearOwnerKey();Enabled=false;passwordTimer.Stop();passwordCancellation.Cancel();
            await lifecycleGate.WaitAsync();
            try{if(viewer!=null&&!viewer.IsDisposed){await viewer.CloseSessionAsync();viewer.Close();viewer=null;}await StopShareCore();RevokeKnownWithin(2000);shutdownComplete=true;}
            finally{lifecycleGate.Release();}
            if(!IsDisposed&&IsHandleCreated)BeginInvoke(new Action(Close));
        }
    }
}
