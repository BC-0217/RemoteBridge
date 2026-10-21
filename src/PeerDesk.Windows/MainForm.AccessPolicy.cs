using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace PeerDesk.Windows {
internal sealed partial class MainForm {
 private readonly CheckBox passwordAutoAccess=new CheckBox{Name="PasswordAutoAccess",Text=UiLocalization.Text("PW가 맞으면 승인 없이 화면·입력 공유 허용"),AutoSize=true,Checked=true};
 private readonly TextBox fixedPasswordBox=new TextBox{Name="FixedPassword",UseSystemPasswordChar=true,MaxLength=128,Width=260};
 private readonly Label fixedPasswordState=new Label{Name="FixedPasswordState",Text=UiLocalization.Text("서버 확인 전"),AutoSize=true};
 private readonly Label accessPolicyState=new Label{Name="AccessPolicyState",AutoSize=true,MaximumSize=new Size(620,0)};
 private Button setFixedButton,clearFixedButton,refreshFixedButton;
 private string accessNonce=Guid.NewGuid().ToString("N"),accessRuntimeEpoch;
 private long accessDeadline,accessNextUpdate,accessLocalVersion;
 private bool accessBusy,changingAccessPolicy;
 private ControlPlaneClient accessClient;
 private Task accessDisableTask=Task.CompletedTask;
 private void InitializeAccessPolicy() {
  changingAccessPolicy=true;passwordAutoAccess.Checked=storedSettings==null||storedSettings.PasswordAutoAccess;changingAccessPolicy=false;
  var page=new TabPage(UiLocalization.Text("PC 접근 설정")){Name="AccessPolicyPage"};
  var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(16)};
  flow.Controls.Add(new Label{Text=UiLocalization.Text("이 PC에 연결할 때 쓰는 고정 비밀번호"),AutoSize=true,Font=new Font(Font,FontStyle.Bold)});
  flow.Controls.Add(new Label{Text=UiLocalization.Text("서버 참가 PW와 다릅니다. 임시 PW 또는 고정 PW 중 하나가 맞으면 연결할 수 있습니다.\r\n고정 PW는 8~128자이며 서버에는 검증값만 저장합니다. 원문은 이 PC에 저장하지 않습니다."),AutoSize=true,MaximumSize=new Size(620,0)});
  flow.Controls.Add(fixedPasswordBox);
  var row=new FlowLayoutPanel{AutoSize=true,WrapContents=true};
  setFixedButton=Button(UiLocalization.Text("고정 PW 설정"),()=>ChangeFixedPassword(false));setFixedButton.Name="SetFixedPassword";
  clearFixedButton=Button(UiLocalization.Text("고정 PW 삭제"),()=>ChangeFixedPassword(true));clearFixedButton.Name="ClearFixedPassword";
  refreshFixedButton=Button(UiLocalization.Text("설정 상태 확인"),()=>RefreshFixedPassword());refreshFixedButton.Name="RefreshFixedPassword";
  row.Controls.Add(setFixedButton);row.Controls.Add(clearFixedButton);row.Controls.Add(refreshFixedButton);flow.Controls.Add(row);flow.Controls.Add(fixedPasswordState);
  flow.Controls.Add(passwordAutoAccess);
  flow.Controls.Add(new Label{Text=UiLocalization.Text("켜짐: 올바른 PW를 아는 사람이 승인창 없이 화면을 보고 마우스·키보드를 제어할 수 있습니다.\r\n꺼짐 또는 PW 공란: 직접 승인해야 합니다. 이미 연결된 세션은 [공유 중지]로 종료하세요."),AutoSize=true,MaximumSize=new Size(620,0),ForeColor=Color.DarkRed});
  flow.Controls.Add(accessPolicyState);page.Controls.Add(flow);settingsTabs.TabPages.Add(page);
  passwordAutoAccess.CheckedChanged+=(s,e)=>{if(!changingAccessPolicy)ChangeAccessPolicy();};
  accessPolicyState.Text=UiLocalization.Text("저장된 접근 설정 — 서버 등록 확인 후 적용합니다.");
 }
 private bool AccessRuntimeValid(ControlPlaneClient c,string epoch) {
  return !shutdownBusy&&!IsDisposed&&registrationVerified&&passwordAutoAccess.Checked&&ReferenceEquals(client,c)&&ReferenceEquals(accessClient,c)
   &&epoch!=null&&epoch==accessRuntimeEpoch&&passwordWatch.ElapsedMilliseconds<accessDeadline;
 }
 private void CancelPendingPasswordGrants() {
  foreach(var g in consentGrants.Values.ToArray())if(g.PasswordEpoch!=null&&!g.Activated) {
   if(g.StartCancellation!=null)g.StartCancellation.Cancel();
   consentGrants.Remove(g.SessionId);
  }
 }
 private void InvalidateAccessRuntime() {
  accessLocalVersion++;accessRuntimeEpoch=null;accessDeadline=0;accessNextUpdate=0;CancelPendingPasswordGrants();
  var old=accessClient;accessClient=null;string nonce=accessNonce;accessNonce=Guid.NewGuid().ToString("N");
  if(old!=null)accessDisableTask=DisableRuntime(old,nonce);
 }
 private async Task DisableRuntime(ControlPlaneClient c,string nonce) {
  try{using(var stop=new CancellationTokenSource(2000))await c.UpdateAccessRuntimeAsync(false,nonce,stop.Token);}catch{}
 }
 private void UpdateAccessButtons() {
  bool ready=registrationVerified&&client!=null&&!shutdownBusy&&!accessBusy&&!lifecycleBusy;
  if(setFixedButton!=null){setFixedButton.Enabled=ready;clearFixedButton.Enabled=ready;refreshFixedButton.Enabled=ready;}
 }
 private void AccessPolicyTick() {
  if(accessRuntimeEpoch!=null&&passwordWatch.ElapsedMilliseconds>=accessDeadline){accessRuntimeEpoch=null;CancelPendingPasswordGrants();}
  bool ready=registrationVerified&&client!=null&&!shutdownBusy;
  UpdateAccessButtons();
  if(!ready||accessBusy||lifecycleBusy||!accessDisableTask.IsCompleted)return;
  if(!ReferenceEquals(accessClient,client)){accessClient=client;accessRuntimeEpoch=null;accessNextUpdate=0;fixedPasswordState.Text=UiLocalization.Text("서버 확인 전");}
  if(passwordWatch.ElapsedMilliseconds>=accessNextUpdate)UpdateAccessRuntime();
 }
 private async void ChangeAccessPolicy() {
  InvalidateAccessRuntime(); // cancel pending authority before any await; active sharing is not stopped
  if(!registrationVerified||client==null)return;
  bool acquired=false;var c=client;
  try{await lifecycleGate.WaitAsync();acquired=true;if(shutdownBusy||!ReferenceEquals(c,client))return;var saved=BuildSettingsState(c);settingsStore.Save(saved);storedSettings=saved;accessPolicyState.Text=UiLocalization.Text("접근 설정을 이 Windows 사용자 계정에 저장했습니다.");}
  catch(Exception ex){changingAccessPolicy=true;passwordAutoAccess.Checked=false;changingAccessPolicy=false;accessPolicyState.Text=UiLocalization.Text("접근 설정 저장 실패 — 자동 접근 비활성: ")+ex.Message;}
  finally{if(acquired)lifecycleGate.Release();}
 }
 private async void UpdateAccessRuntime() {
  if(accessBusy)return;accessBusy=true;UpdateAccessButtons();var c=client;long epoch=autoEpoch,version=accessLocalVersion,start=passwordWatch.ElapsedMilliseconds;bool acquired=false;
  accessNextUpdate=start+20000;
  try {
   await lifecycleGate.WaitAsync(automationCancellation.Token);acquired=true;
   if(shutdownBusy||!registrationVerified||!ReferenceEquals(c,client)||epoch!=autoEpoch||version!=accessLocalVersion)return;
   // Commit the visible local policy before advertising the server runtime capability.
   var saved=BuildSettingsState(c);settingsStore.Save(saved);storedSettings=saved;
   var response=await c.UpdateAccessRuntimeAsync(passwordAutoAccess.Checked,accessNonce,automationCancellation.Token);
   if(shutdownBusy||epoch!=autoEpoch||version!=accessLocalVersion||!ReferenceEquals(c,client))return;
   accessClient=c;accessRuntimeEpoch=response.RuntimeEpoch;accessDeadline=start+Math.Max(0,(long)Math.Min(60000,(response.ExpiresAt-DateTimeOffset.UtcNow).TotalMilliseconds));
   accessPolicyState.Text=response.Enabled?UiLocalization.Text("켜짐 · PW 확인 시 승인창 없이 화면·입력 공유 (이 창이 실행 중일 때)"):UiLocalization.Text("꺼짐 · 모든 연결은 직접 승인해야 합니다.");
   var fixedState=await c.GetFixedPasswordStateAsync(automationCancellation.Token);
   if(epoch==autoEpoch&&ReferenceEquals(c,client)&&!shutdownBusy)fixedPasswordState.Text=fixedState.Configured?UiLocalization.Text("고정 PW 설정됨 (원문 조회 불가)"):UiLocalization.Text("고정 PW 없음 — 임시 PW 사용");
  }catch(OperationCanceledException){}
  catch(Exception ex){accessRuntimeEpoch=null;accessDeadline=0;CancelPendingPasswordGrants();accessNextUpdate=passwordWatch.ElapsedMilliseconds+60000;accessPolicyState.Text=UiLocalization.Text("자동 접근 확인 실패 — 직접 승인 방식: ")+ex.Message;}
  finally{if(acquired)lifecycleGate.Release();accessBusy=false;UpdateAccessButtons();}
 }
 private async void RefreshFixedPassword() {
  if(accessBusy||lifecycleBusy||!registrationVerified||client==null)return;accessBusy=true;UpdateAccessButtons();bool acquired=false;var c=client;long epoch=autoEpoch;
  try{await lifecycleGate.WaitAsync(automationCancellation.Token);acquired=true;if(shutdownBusy||epoch!=autoEpoch||!ReferenceEquals(c,client))return;var result=await c.GetFixedPasswordStateAsync(automationCancellation.Token);if(epoch==autoEpoch&&ReferenceEquals(c,client))fixedPasswordState.Text=result.Configured?UiLocalization.Text("고정 PW 설정됨 (원문 조회 불가)"):UiLocalization.Text("고정 PW 없음");}
  catch(Exception ex){if(!shutdownBusy)fixedPasswordState.Text=UiLocalization.Text("설정 상태 확인 실패: ")+ex.Message;}
  finally{if(acquired)lifecycleGate.Release();accessBusy=false;UpdateAccessButtons();}
 }
 private async void ChangeFixedPassword(bool remove) {
  if(accessBusy||lifecycleBusy||!registrationVerified||client==null)return;
  string value=fixedPasswordBox.Text;if(!remove&&(value.Length<8||value.Length>128)){fixedPasswordState.Text=UiLocalization.Text("고정 비밀번호는 8~128자여야 합니다.");return;}
  accessBusy=true;UpdateAccessButtons();bool acquired=false;var c=client;long epoch=autoEpoch;
  try{await lifecycleGate.WaitAsync(automationCancellation.Token);acquired=true;if(shutdownBusy||epoch!=autoEpoch||!ReferenceEquals(c,client))return;
   var result=remove?await c.ClearFixedPasswordAsync(automationCancellation.Token):await c.SetFixedPasswordAsync(value,automationCancellation.Token);
   if(epoch!=autoEpoch||!ReferenceEquals(c,client)||shutdownBusy)return;
   CancelPendingPasswordGrants();fixedPasswordState.Text=result.Configured?UiLocalization.Text("고정 PW 설정됨 (서버 저장 완료)"):UiLocalization.Text("고정 PW 삭제 완료");
  }catch(Exception ex){if(!shutdownBusy)fixedPasswordState.Text=UiLocalization.Text("고정 PW 변경 실패: ")+ex.Message;}
  finally{fixedPasswordBox.Clear();if(acquired)lifecycleGate.Release();accessBusy=false;UpdateAccessButtons();}
 }
 private async void AutoApprovePassword(ControlPlaneClient c,SessionItem item) {
  string runtime=item.RuntimeEpoch;long epoch=autoEpoch;var token=automationCancellation.Token;
  if(!AccessRuntimeValid(c,runtime)||item.TargetDeviceId!=c.DeviceId||item.InitiatorDeviceId==c.DeviceId||!item.AutoApprovalEligible)return;
  var grant=new ConsentGrant{SessionId=item.SessionId,RequesterId=item.InitiatorDeviceId,OwnId=c.DeviceId,Binding=ClientBinding(c),Client=c,Epoch=epoch,Scope=autoCaptureScope,PasswordEpoch=runtime,StartCancellation=CancellationTokenSource.CreateLinkedTokenSource(token)};
  consentGrants[item.SessionId]=grant;
  try {
   var response=await c.DecidePasswordAsync(item.SessionId,runtime,grant.StartCancellation.Token);
   if(!GrantValid(grant)||!AccessRuntimeValid(c,runtime))return;
   if(response.SessionId!=item.SessionId||response.TargetDeviceId!=c.DeviceId||response.InitiatorDeviceId!=item.InitiatorDeviceId||response.RuntimeEpoch!=runtime||response.AuthorizationKind!=item.AuthorizationKind||response.AuthorizationRevision!=item.AuthorizationRevision||!response.AutoApprovalEligible)throw new InvalidOperationException(UiLocalization.Text("자동 승인 응답이 요청과 일치하지 않습니다."));
   Add(response);Record(response);status.Text=UiLocalization.Text("PW가 확인된 상대(")+item.InitiatorDeviceId+UiLocalization.Text(")의 연결을 자동 승인했습니다. 언제든 공유를 중지할 수 있습니다.");
   if(response.Status=="Connected")AutoStartShare(grant);
  }catch(OperationCanceledException){}
  catch(Exception ex){if(!shutdownBusy)status.Text=UiLocalization.Text("자동 승인 확인 실패 — 자동 공유하지 않습니다: ")+ex.Message;}
  finally{SessionItem current=known.FirstOrDefault(x=>x.SessionId==item.SessionId);if(current==null||(current.Status!="Approved"&&current.Status!="Connected")){ConsentGrant existing;if(consentGrants.TryGetValue(item.SessionId,out existing)&&ReferenceEquals(existing,grant)){consentGrants.Remove(item.SessionId);grant.StartCancellation.Cancel();grant.StartCancellation.Dispose();}}}
 }
}
}
