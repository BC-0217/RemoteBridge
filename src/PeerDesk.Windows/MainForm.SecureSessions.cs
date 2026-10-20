using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        private readonly CheckBox secureModeChoice=new CheckBox{Name="UseSecureSession",Text="화면과 입력 내용을 안전하게 주고받기 (권장)",Checked=true,AutoSize=true};
        private readonly CheckBox inputPermissionChoice=new CheckBox{Name="AllowSessionInput",Text="상대가 내 PC의 마우스·키보드를 사용하도록 허용",Checked=true,AutoSize=true};
        private readonly CheckBox textPermissionChoice=new CheckBox{Name="AllowSessionText",Text="상대가 보낸 글 받기 (받기 전에 확인)",AutoSize=true};
        private readonly CheckBox filePermissionChoice=new CheckBox{Name="AllowSessionFiles",Text="상대가 보낸 파일 받기 (파일마다 확인)",AutoSize=true};
        private readonly CheckBox directScreenChoice=new CheckBox{Name="UseDirectScreen",Text="가능하면 화면을 두 PC 사이로 바로 보내기",AutoSize=true};
        private readonly ComboBox directAddressChoice=new ComboBox{Name="DirectLocalAddress",DropDownStyle=ComboBoxStyle.DropDownList,Width=300};
        private readonly Dictionary<string,SecureSessionAdapter> secureChannels=new Dictionary<string,SecureSessionAdapter>();
        private SecurePeerIdentity deviceIdentity;
        private string identityFile;private bool secureChoiceLoading;
        private bool UseSecure {get{return secureModeChoice.Checked;}}
        private int SessionPermissionBits {get{return 1|(inputPermissionChoice.Checked?2:0)|(textPermissionChoice.Checked?4:0)|(filePermissionChoice.Checked?8:0);}}
        private void AddSecureOptions(FlowLayoutPanel flow,string settingsPath){
            AddReconnectOptions(flow);
            identityFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)),"device-identity.dpapi");
            foreach(var box in new[]{secureModeChoice,inputPermissionChoice,textPermissionChoice,filePermissionChoice,directScreenChoice}){
                flow.Controls.Add(box);box.CheckedChanged+=(s,e)=>{
                    if(secureChoiceLoading)return;
                    if(sharing!=null||known.Any(x=>IsLive(x.Status))){secureChoiceLoading=true;box.Checked=!box.Checked;secureChoiceLoading=false;experienceWarning.Text="진행 중인 연결·승인 요청을 종료한 다음 보안·권한 설정을 바꿔 주세요.";}
                };
            }
            foreach(var address in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Select(x=>x.Address).Where(x=>x.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!System.Net.IPAddress.IsLoopback(x)).Distinct())directAddressChoice.Items.Add(address.ToString());
            if(directAddressChoice.Items.Count>0)directAddressChoice.SelectedIndex=0;
            flow.Controls.Add(new Label{Text="직접 연결을 받을 내 PC 주소 (보통 같은 공유기나 VPN에서 사용)",AutoSize=true});flow.Controls.Add(directAddressChoice);
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new System.Drawing.Size(620,0),Text="직접 연결은 보통 같은 공유기나 VPN에서 잘 됩니다. 모든 인터넷 환경에서 가능한 것은 아니며, 연결이 안 되면 서버를 통해 화면을 보냅니다. 상대에게 내 PC의 IP 주소가 보일 수 있습니다. 화면만 두 PC 사이로 보내며, 마우스·키보드 입력과 권한 확인은 서버를 거칩니다. 방화벽 설정은 바꾸지 않습니다."});
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new System.Drawing.Size(620,0),Text="이 연결은 두 PC 사이에서 내용을 암호화해 서버가 화면과 입력 내용을 읽지 못하게 합니다. 처음 연결할 때는 양쪽 PC에 표시된 확인 코드를 직접 비교하세요. 암호화를 끄면 서버가 화면과 입력 내용을 읽을 수 있으며, 연결에 실패해도 암호화를 자동으로 끄지 않습니다."});
            FormClosed+=(s,e)=>{foreach(var sid in featureStates.Keys.ToArray())CloseFeatureChannel(sid);foreach(var channel in secureChannels.Values.ToArray())channel.Dispose();secureChannels.Clear();if(deviceIdentity!=null){deviceIdentity.Dispose();deviceIdentity=null;}};
        }
        private System.Net.IPAddress DirectLocalAddress(){System.Net.IPAddress value;if(!directScreenChoice.Checked)return null;if(!System.Net.IPAddress.TryParse(Convert.ToString(directAddressChoice.SelectedItem),out value))throw new InvalidOperationException("직접 연결을 받을 내 PC 주소를 선택해 주세요.");return value;}
        private SecurePeerIdentity RequireDeviceIdentity(){if(deviceIdentity==null)deviceIdentity=SecurePeerIdentity.LoadOrCreate(identityFile);return deviceIdentity;}
        private Task<string> ConfirmPeerOnUiAsync(string sid,byte[] local,byte[] remote,CancellationToken ct){
            var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action action=()=>{try{result.TrySetResult(ConfirmPeerForSession(sid,local,remote,ct));}catch(Exception ex){result.TrySetException(ex);}};
            if(IsDisposed||ct.IsCancellationRequested){result.TrySetCanceled();return result.Task;}
            if(InvokeRequired)BeginInvoke(action);else action();return result.Task;
        }
        private async Task PrepareSecureTargetAsync(MediaSharingSession candidate,ControlPlaneClient c,string sid,CancellationToken ct){
            try{await candidate.PrepareSecureAsync(ct);}catch(Exception ex)when(DiagPreparationFailure(true,DiagPreparation.Prepare,null,ex)){}
            var transport=new SessionTransportClient(c,sid);SecureSessionAdapter adapter=null;DiagPreparation stage=DiagPreparation.Transport;
            try{
                var descriptor=await transport.ConfigureAsync(SessionPermissionBits,1,1,ct);
                adapter=new SecureSessionAdapter(c,transport,descriptor,RequireDeviceIdentity(),ct);secureChannels.Add(sid,adapter);
                RegisterSessionFeatures(adapter,c,sid);stage=DiagPreparation.Authenticate;
                await adapter.AuthenticateAsync((local,remote)=>ConfirmPeerOnUiAsync(sid,local,remote,ct));stage=DiagPreparation.Direct;
                await adapter.NegotiateDirectScreenAsync(RequireDeviceIdentity(),DirectLocalAddress(),directScreenChoice.Checked);stage=DiagPreparation.Attach;
                ct.ThrowIfCancellationRequested();candidate.AttachSecure(adapter);
            }catch(Exception ex){
                DiagPreparationFailure(true,stage,adapter,ex);
                if(adapter!=null)RemoveSecureChannel(sid,adapter);else transport.Dispose();
                candidate.FailSecurePreparation(ex);try{await candidate.StopAsync();}catch{}throw;
            }
        }
        private async Task<ViewerForm> MakeSecureViewerAsync(ControlPlaneClient c,string sid,CancellationToken ct,Func<bool> isCurrent){
            var transport=new SessionTransportClient(c,sid);SecureSessionAdapter adapter=null;DiagPreparation stage=DiagPreparation.Transport;
            try{
                SessionTransportDescriptor descriptor=null;var wait=System.Diagnostics.Stopwatch.StartNew();
                while(descriptor==null){
                    ct.ThrowIfCancellationRequested();if(!isCurrent())throw new OperationCanceledException();
                    try{descriptor=await transport.GetDescriptorAsync(ct);}
                    catch(InvalidOperationException ex)when(ControlPlaneClient.HttpStatus(ex)==404&&wait.ElapsedMilliseconds<10000){await Task.Delay(500,ct);}
                }
                if(!isCurrent())throw new OperationCanceledException();
                adapter=new SecureSessionAdapter(c,transport,descriptor,RequireDeviceIdentity(),ct);secureChannels.Add(sid,adapter);
                RegisterSessionFeatures(adapter,c,sid);stage=DiagPreparation.Authenticate;
                await adapter.AuthenticateAsync((local,remote)=>ConfirmPeerOnUiAsync(sid,local,remote,ct));stage=DiagPreparation.Direct;
                await adapter.NegotiateDirectScreenAsync(RequireDeviceIdentity(),DirectLocalAddress(),directScreenChoice.Checked);stage=DiagPreparation.Attach;
                ct.ThrowIfCancellationRequested();if(!isCurrent())throw new OperationCanceledException();
                var opened=new ViewerForm(c,sid,adapter);
                opened.FormClosed+=(s,e)=>{RemoveSecureChannel(sid,adapter);};
                ConfigureViewerFeatures(opened,adapter,c,sid);ConfigureReconnect(opened,adapter,c,sid);
                return opened;
            }catch(Exception ex)when(DiagPreparationFailure(false,stage,adapter,ex)){throw;}
            catch{if(adapter!=null)RemoveSecureChannel(sid,adapter);else transport.Dispose();throw;}
        }
        // Diagnostics only: stage enum + exception category; always false so it can be used as a pass-through filter.
        private static bool DiagPreparationFailure(bool target,DiagPreparation stage,SecureSessionAdapter adapter,Exception ex){
            DiagnosticLog.Failure(DiagEvent.SecurePreparationFailure,adapter==null?0:adapter.DiagCorrelation,ex,DiagValue.Of(DiagKey.Stage,stage),DiagValue.Of(DiagKey.Initiator,!target));
            return false;
        }
        private void RemoveSecureChannel(string sid,SecureSessionAdapter expected=null){
            SecureSessionAdapter current;if(secureChannels.TryGetValue(sid,out current)&&(expected==null||ReferenceEquals(current,expected))){secureChannels.Remove(sid);CloseFeatureChannel(sid);current.Dispose();}
        }
        private void RegisterSessionFeatures(SecureSessionAdapter adapter,ControlPlaneClient c,string sid){InitializeFeatureChannel(adapter,c,sid);}
        private void ConfigureViewerFeatures(ViewerForm opened,SecureSessionAdapter adapter,ControlPlaneClient c,string sid){InitializeViewerFeatureActions(opened,adapter,c,sid);}
    }
}
