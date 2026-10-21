using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        private UserExperienceOptions experience;
        private TabControl mainUiTabs;
        private SettingsDialog settingsDialog;
        private ComboBox languageChoice,themeChoice;
        private bool appearanceLoading;
        private void InitializeUiExperience(Control root){
            mainUiTabs=new TabControl{Name="MainNavigation",Dock=DockStyle.Fill,Padding=new Point(14,6)};
            settingsTabs.TabPages.Remove(homePage);settingsTabs.TabPages.Remove(sessionPage);
            mainUiTabs.TabPages.Add(homePage);mainUiTabs.TabPages.Add(sessionPage);
            var panel=root as TableLayoutPanel;if(panel==null)throw new InvalidOperationException("Main layout is missing.");
            panel.Controls.Add(mainUiTabs,0,1);
            var menu=new SoftButton{Name="OpenSettingsMenu",Text="☰",AutoSize=true,AccessibleName=UiLocalization.Text("설정"),Anchor=AnchorStyles.Right};
            var top=new TableLayoutPanel{ColumnCount=2,RowCount=1,AutoSize=true,Dock=DockStyle.Fill};
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));
            Control warning=panel.GetControlFromPosition(0,0);if(warning!=null){
                panel.Controls.Remove(warning);warning.Dock=DockStyle.Top;warning.MinimumSize=Size.Empty;
                Action constrain=()=>{int width=Math.Max(100,panel.ClientSize.Width-panel.Padding.Horizontal-92);var maximum=new Size(width,0);if(warning.MaximumSize!=maximum)warning.MaximumSize=maximum;};
                panel.SizeChanged+=(s,e)=>constrain();top.SizeChanged+=(s,e)=>constrain();constrain();top.Controls.Add(warning,0,0);
            }
            top.Controls.Add(menu,1,0);panel.Controls.Add(top,0,0);
            languageChoice=new ComboBox{Name="UiLanguage",DropDownStyle=ComboBoxStyle.DropDownList,Width=170};
            languageChoice.Items.AddRange(UiLocalization.Languages.Cast<object>().ToArray());
            themeChoice=new ComboBox{Name="UiTheme",DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
            themeChoice.Items.AddRange(new object[]{UiLocalization.Text("기본 (밝게)"),UiLocalization.Text("다크 모드")});
            appearanceLoading=true;
            languageChoice.SelectedIndex=Array.FindIndex(UiLocalization.Languages,x=>x.Code==experience.Language);themeChoice.SelectedIndex=experience.Dark?1:0;
            appearanceLoading=false;
            settingsDialog=new SettingsDialog(settingsTabs,languageChoice,themeChoice);
            menu.Click+=(s,e)=>OpenSettingsDialog(null);
            languageChoice.SelectedIndexChanged+=(s,e)=>ChangeUiAppearance();themeChoice.SelectedIndexChanged+=(s,e)=>ChangeUiAppearance();
            UiLocalization.Select(experience.Language,experience.Dark);
            FormClosed+=(s,e)=>{if(settingsDialog!=null){settingsDialog.Dispose();settingsDialog=null;}};
            RefreshUiAppearance();
        }
        private void ChangeUiAppearance(){
            if(appearanceLoading||experience==null||languageChoice.SelectedIndex<0||themeChoice.SelectedIndex<0)return;
            try{
                var choice=(UiLanguage)languageChoice.SelectedItem;experience.SetAppearance(choice.Code,themeChoice.SelectedIndex==1);
                UiLocalization.Select(experience.Language,experience.Dark);experienceWarning.Text="";
            }catch(Exception){
                appearanceLoading=true;languageChoice.SelectedIndex=Array.FindIndex(UiLocalization.Languages,x=>x.Code==experience.Language);themeChoice.SelectedIndex=experience.Dark?1:0;appearanceLoading=false;
                experienceWarning.Text=UiLocalization.Text("화면 설정을 저장하지 못했습니다. 기존 설정과 원본 파일을 유지합니다.");
            }
        }
        private void OnUiAppearanceChanged(){if(IsDisposed)return;if(InvokeRequired){try{BeginInvoke(new Action(RefreshUiAppearance));}catch(InvalidOperationException){}}else RefreshUiAppearance();}
        private void RefreshUiAppearance(){
            UiLocalization.BindFormat(this,"RemoteBridge — 리모트브리지  v{0}",BuildVersion.Display);
            UiLocalization.Apply(this);if(settingsDialog!=null)UiLocalization.Apply(settingsDialog);
            if(themeChoice!=null){int selected=themeChoice.SelectedIndex;appearanceLoading=true;themeChoice.Items[0]=UiLocalization.Text("기본 (밝게)");themeChoice.Items[1]=UiLocalization.Text("다크 모드");themeChoice.SelectedIndex=selected;appearanceLoading=false;}
        }
        private void OpenSettingsDialog(TabPage page){if(settingsDialog!=null&&!shutdownBusy)settingsDialog.ShowPage(this,settingsTabs,page);}
        private void ShowHomePage(){if(mainUiTabs!=null)mainUiTabs.SelectedTab=homePage;}
        private PeerTrustStore peerTrust;
        private readonly ListBox trustedPeersList=new ListBox{Name="TrustedPeerList",Width=610,Height=100};
        private bool pairingBusy;
        private bool experienceLayoutBusy;
        private void RefreshTrustedPeers(){trustedPeersList.Items.Clear();if(peerTrust.Warning!=null){experienceWarning.Text=peerTrust.Warning;return;}foreach(var peer in peerTrust.List())trustedPeersList.Items.Add(peer);}
        internal string ConfirmPeerForSession(string sid,byte[] localHello,byte[] remoteHello,System.Threading.CancellationToken ct){
            if(InvokeRequired)throw new InvalidOperationException("Peer trust must be confirmed on the UI thread.");
            if(pairingBusy||shutdownBusy||ct.IsCancellationRequested||client==null||!registrationVerified)return null;
            var c=client;long epoch=autoEpoch,credentialRevision=c.CredentialRevision;var session=known.FirstOrDefault(x=>x.SessionId==sid&&x.Status=="Connected");if(session==null)return null;
            string peer=session.TargetDeviceId==c.DeviceId?session.InitiatorDeviceId:session.TargetDeviceId;
            string fingerprint=SecurePeerChannel.CandidateFingerprint(remoteHello),existing=peerTrust.Find(c.ServerUrl,c.CertificateSha256,peer);
            if(existing!=null){if(!String.Equals(existing,fingerprint,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(UiLocalization.Text("상대 PC의 보안 키가 변경되었습니다. 연결을 중지합니다. 상대방에게 확인한 뒤 신뢰 설정을 직접 삭제해야 합니다."));return existing;}
            pairingBusy=true;
            try {using(var dialog=new PeerTrustDialog(peer,fingerprint,PeerTrustDialog.ComparisonCode(localHello,remoteHello))){
                using(ct.Register(()=>{try{if(dialog.IsHandleCreated&&!dialog.IsDisposed)dialog.BeginInvoke(new Action(()=>{if(!dialog.IsDisposed){dialog.DialogResult=DialogResult.Cancel;dialog.Close();}}));}catch(InvalidOperationException){}})){
                    if(ct.IsCancellationRequested||dialog.ShowDialog(this)!=DialogResult.OK)return null;
                }
            }
                if(ct.IsCancellationRequested||shutdownBusy||epoch!=autoEpoch||c.CredentialRevision!=credentialRevision||!ReferenceEquals(c,client)||!known.Any(x=>x.SessionId==sid&&x.Status=="Connected"))return null;
                peerTrust.Trust(c.ServerUrl,c.CertificateSha256,peer,fingerprint);RefreshTrustedPeers();return fingerprint;
            }finally{pairingBusy=false;}
        }
        private readonly ComboBox qualityChoice=new ComboBox{Name="QualityChoice",DropDownStyle=ComboBoxStyle.DropDownList,Width=220};
        private readonly Label experienceWarning=new Label{Name="ExperienceWarning",AutoSize=true,ForeColor=Color.DarkRed,MaximumSize=new Size(640,0)};
        private bool qualityUiLoading;private bool monitorUiLoading;private int selectedMonitor;
        private readonly ComboBox monitorChoice=new ComboBox{Name="SharedMonitorChoice",DropDownStyle=ComboBoxStyle.DropDownList,Width=360};
        private string[] monitorNames;
        private ShareIndicatorForm activeShareIndicator;
        private string indicatorSession;
        private void SyncShareIndicator(){
            if(shutdownBusy||sharing==null||!sharing.IsRunning||!ReferenceEquals(monitoredSharing,sharing)){if(activeShareIndicator!=null){activeShareIndicator.Dismiss();activeShareIndicator=null;indicatorSession=null;}return;}
            if(activeShareIndicator!=null&&!activeShareIndicator.IsDisposed&&indicatorSession==sharing.SessionId)return;
            if(activeShareIndicator!=null)activeShareIndicator.Dismiss();
            var item=known.FirstOrDefault(x=>x.SessionId==sharing.SessionId);string peer=item==null?UiLocalization.Text("상대 PC"):item.InitiatorDeviceId;
            indicatorSession=sharing.SessionId;activeShareIndicator=new ShareIndicatorForm(peer,StopShare,inputPermissionChoice.Checked){SendText=()=>ShareFeatureAction(false),SendFile=()=>ShareFeatureAction(true)};activeShareIndicator.Show();
        }
        private void LayoutExperience(FlowLayoutPanel flow) {
            if(experienceLayoutBusy||flow.IsDisposed||flow.ClientSize.Width<=0)return;
            experienceLayoutBusy=true;
            try {
                int available=Math.Max(1,flow.ClientSize.Width-flow.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth);
                foreach(Control child in flow.Controls) {
                    int width=Math.Max(1,available-child.Margin.Horizontal);
                    if(child is Label||child is CheckBox||child is System.Windows.Forms.Button) {
                        var maximum=new Size(width,0);
                        if(child.MaximumSize!=maximum)child.MaximumSize=maximum;
                        if(!child.AutoSize)child.AutoSize=true;
                        Size wanted=child.GetPreferredSize(new Size(width,0));
                        wanted.Width=Math.Min(width,wanted.Width);
                        if(child.Size!=wanted)child.Size=wanted;
                    } else if(child is ComboBox||child is ListBox) {
                        if(child.Width!=width)child.Width=width;
                    }
                }
                int contentHeight=flow.Padding.Vertical;
                foreach(Control child in flow.Controls)contentHeight+=child.Height+child.Margin.Vertical;
                var minimum=new Size(0,contentHeight);
                if(flow.AutoScrollMinSize!=minimum)flow.AutoScrollMinSize=minimum;
            } finally {experienceLayoutBusy=false;}
        }
        private void InitializeExperience(string settingsPath) {
            experience=new UserExperienceOptions(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)),"experience.json"));
            var page=new TabPage(UiLocalization.Text("화면·편의"));var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(18)};
            AddSecureOptions(flow,settingsPath);
            flow.Controls.Add(new Label{Text=UiLocalization.Text("내 화면을 보내는 화질"),AutoSize=true,Font=new Font(Font,FontStyle.Bold)});
            qualityChoice.Items.AddRange(new object[]{UiLocalization.Text("자동 조절 (권장)"),UiLocalization.Text("글자가 잘 보이게 (화질 우선)"),UiLocalization.Text("가볍게 보내기 (속도 우선)")});
            qualityUiLoading=true;qualityChoice.SelectedIndex=(int)experience.Quality;qualityUiLoading=false;
            flow.Controls.Add(qualityChoice);
            flow.Controls.Add(new Label{Text=UiLocalization.Text("상대에게 보여줄 화면 (연결 전에 선택)"),AutoSize=true});
            var screens=Screen.AllScreens;monitorNames=screens.Select(x=>x.DeviceName).ToArray();monitorChoice.Items.Add(UiLocalization.Text("모든 모니터"));
            foreach(var screen in screens)monitorChoice.Items.Add(screen.DeviceName+" — "+screen.Bounds.Width+"×"+screen.Bounds.Height);
            monitorChoice.SelectedIndex=0;monitorChoice.Enabled=autoCaptureScope.IsDesktop;flow.Controls.Add(monitorChoice);
            monitorChoice.SelectedIndexChanged+=(s,e)=>{
                if(monitorUiLoading||!monitorChoice.Enabled)return;
                if(lifecycleBusy||shutdownBusy||sharing!=null||known.Any(x=>IsLive(x.Status))){monitorUiLoading=true;monitorChoice.SelectedIndex=selectedMonitor;monitorUiLoading=false;experienceWarning.Text=UiLocalization.Text("진행 중인 연결·승인 요청을 종료한 다음 공유 모니터를 바꿔 주세요.");return;}
                try{var next=monitorChoice.SelectedIndex==0?CaptureScope.ForDesktopShare():CaptureScope.ForMonitorShare(monitorNames[monitorChoice.SelectedIndex-1]);InvalidateAutomation();autoCaptureScope=next;selectedMonitor=monitorChoice.SelectedIndex;experienceWarning.Text="";}
                catch(Exception ex){monitorUiLoading=true;monitorChoice.SelectedIndex=selectedMonitor;monitorUiLoading=false;experienceWarning.Text=ex.Message;}
            };
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(620,0),Text=UiLocalization.Text("자동은 전송 지연과 처리 시간을 보고 화질·해상도를 조절합니다. 공유 권한이나 입력 안전 제한은 바뀌지 않습니다. 상대 PC의 품질은 그 PC에서 선택합니다.")});
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(620,0),Text=UiLocalization.Text("최근 연결 목록에서 오른쪽 클릭 → 별명·즐겨찾기 편집. 별명은 내 PC에서만 보이며 상대 ID·접속 권한은 바꾸지 않습니다.")});
            experienceWarning.Text=experience.Warning??"";flow.Controls.Add(experienceWarning);
            peerTrust=new PeerTrustStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)),"trusted-peers.bin"));
            flow.Controls.Add(new Label{Text=UiLocalization.Text("확인한 상대 PC"),AutoSize=true});flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(620,0),Text=UiLocalization.Text("이 목록은 이 PC에 저장된 확인 기록입니다. 서버의 PC 등록이나 접속 권한은 바꾸지 않습니다. 기록을 지우면 다음 연결에서 확인 코드를 다시 비교합니다.")});flow.Controls.Add(trustedPeersList);
            var forget=new System.Windows.Forms.Button{Name="ForgetTrustedPeer",Text=UiLocalization.Text("선택한 PC의 확인 기록 지우기"),AutoSize=true};flow.Controls.Add(forget);
            forget.Click+=(s,e)=>{if(shutdownBusy||known.Any(x=>IsLive(x.Status))){experienceWarning.Text=UiLocalization.Text("진행 중인 연결·승인 요청을 먼저 종료해 주세요.");return;}var selected=trustedPeersList.SelectedItem as TrustedPeer;if(selected==null)return;
                if(MessageBox.Show(this,UiLocalization.Text("이 PC에 저장된 확인 기록을 지울까요? 서버의 PC 등록이나 접속 권한은 바뀌지 않습니다. 다음 연결에서는 양쪽 확인 코드를 다시 비교해야 합니다."),UiLocalization.Text("확인 기록 지우기"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                try{peerTrust.Forget(selected.ServerUrl,selected.ServerPin,selected.PeerId);RefreshTrustedPeers();}catch(Exception ex){experienceWarning.Text=ex.Message;}};
            RefreshTrustedPeers();AddDiagnosticLogOptions(flow);AddUpdateOptions(flow);page.Controls.Add(flow);settingsTabs.TabPages.Add(page);
            flow.SizeChanged+=(s,e)=>LayoutExperience(flow);flow.FontChanged+=(s,e)=>LayoutExperience(flow);flow.Layout+=(s,e)=>LayoutExperience(flow);LayoutExperience(flow);
            qualityChoice.SelectedIndexChanged+=(s,e)=>{
                if(qualityUiLoading||qualityChoice.SelectedIndex<0)return;
                try {var selected=(QualityMode)qualityChoice.SelectedIndex;experience.SetQuality(selected);if(sharing!=null)sharing.QualityMode=selected;experienceWarning.Text="";}
                catch(Exception ex){experienceWarning.Text=ex.Message;qualityUiLoading=true;qualityChoice.SelectedIndex=(int)experience.Quality;qualityUiLoading=false;}
            };
            var menu=new ContextMenuStrip();var edit=menu.Items.Add(UiLocalization.Text("별명·즐겨찾기 편집"));
            edit.Click+=(s,e)=>EditPeerBookmark();
            menu.Opening+=(s,e)=>edit.Enabled=historyList.SelectedItem is ConnectionHistoryRecord&&client!=null&&!lifecycleBusy&&!shutdownBusy;
            historyList.ContextMenuStrip=menu;
            FormClosed+=(s,e)=>{if(activeShareIndicator!=null){activeShareIndicator.Dismiss();activeShareIndicator=null;}};
        }
        // Local diagnostic log folder: opens only the exact fixed folder in Explorer; nothing is uploaded.
        private void AddDiagnosticLogOptions(FlowLayoutPanel flow){
            var open=new System.Windows.Forms.Button{Name="OpenDiagnosticLogs",Text=UiLocalization.Text("\uC9C4\uB2E8 \uB85C\uADF8 \uD3F4\uB354 \uC5F4\uAE30"),AutoSize=true};
            flow.Controls.Add(open);
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(620,0),Text=UiLocalization.Text("\uC9C4\uB2E8 \uAE30\uB85D\uC740 \uC774 PC\uC5D0\uB9CC \uC800\uC7A5\uB418\uACE0 \uC790\uB3D9\uC73C\uB85C \uBCF4\uB0B4\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4. \uD654\uBA74\u00B7\uC785\uB825 \uB0B4\uC6A9, \uC8FC\uC18C, \uBE44\uBC00\uBC88\uD638, \uC5F0\uACB0 ID\uB294 \uB0A8\uAE30\uC9C0 \uC54A\uC73C\uBA70 \uCD5C\uB300 3MiB(1MiB \uD30C\uC77C 3\uAC1C)\uAE4C\uC9C0\uB9CC \uBCF4\uAD00\uD569\uB2C8\uB2E4.")});
            open.Click+=(s,e)=>{
                try{
                    string folder=DiagnosticLog.Directory;if(String.IsNullOrEmpty(folder))throw new InvalidOperationException();
                    Directory.CreateDirectory(folder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName="explorer.exe",Arguments="\""+folder+"\"",UseShellExecute=true});
                }catch{experienceWarning.Text=UiLocalization.Text("\uC9C4\uB2E8 \uB85C\uADF8 \uD3F4\uB354\uB97C \uC5F4 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");}
            };
        }
        private void ApplyExperience(MediaSharingSession candidate){if(experience!=null)candidate.QualityMode=experience.Quality;candidate.AllowRemoteInput=inputPermissionChoice.Checked;}
        private void EditPeerBookmark(){
            var row=historyList.SelectedItem as ConnectionHistoryRecord;if(row==null||client==null||lifecycleBusy||shutdownBusy)return;
            string url=client.ServerUrl,pin=client.CertificateSha256,id=row.TargetDeviceId;
            var old=experience.Find(url,pin,id);
            using(var dialog=new Form{Text=UiLocalization.Text("PC 별명·즐겨찾기"),ClientSize=new Size(390,165),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var title=new Label{Text=UiLocalization.Text("상대 ID: ")+id,Left=16,Top=15,AutoSize=true};
                var alias=new TextBox{Name="PeerAlias",Left=16,Top=45,Width=350,MaxLength=40,Text=old==null?"":old.Alias};
                var favorite=new CheckBox{Name="PeerFavorite",Text=UiLocalization.Text("★ 즐겨찾기로 목록 위에 표시"),Left=16,Top=78,Width=350,Checked=old!=null&&old.Favorite};
                var ok=new System.Windows.Forms.Button{Text=UiLocalization.Text("저장"),Left=210,Top=118,DialogResult=DialogResult.OK};
                var cancel=new System.Windows.Forms.Button{Text=UiLocalization.Text("취소"),Left=290,Top=118,DialogResult=DialogResult.Cancel};
                dialog.Controls.AddRange(new Control[]{title,alias,favorite,ok,cancel});dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try {experience.Set(url,pin,id,alias.Text,favorite.Checked);UpdateHistoryList();}
                catch(Exception ex){ShowError(ex);}
            }
        }
    }
}
