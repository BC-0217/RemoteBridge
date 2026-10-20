using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        private bool reconnectAvailable,reconnecting;
        private readonly CheckBox reconnectChoice=new CheckBox{Name="RetryNetworkConnection",Text="연결이 끊기면 한 번 다시 요청하기",AutoSize=true};
        internal static bool IsRecoverableNetworkFailure(Exception error){
            bool network=false;
            for(var e=error;e!=null;e=e.InnerException){
                if(e is System.Security.Cryptography.CryptographicException||e is System.Security.Authentication.AuthenticationException||e is UnauthorizedAccessException||e is System.IO.InvalidDataException)return false;
                var web=e as WebException;if(web==null)continue;
                if(web.Status==WebExceptionStatus.TrustFailure||web.Status==WebExceptionStatus.SecureChannelFailure||web.Response!=null||web.Status==WebExceptionStatus.ProtocolError)return false;
                network=web.Status==WebExceptionStatus.ConnectFailure||web.Status==WebExceptionStatus.ConnectionClosed||web.Status==WebExceptionStatus.ReceiveFailure||web.Status==WebExceptionStatus.SendFailure||web.Status==WebExceptionStatus.Timeout||web.Status==WebExceptionStatus.NameResolutionFailure;
            }return network;
        }
        private void AddReconnectOptions(FlowLayoutPanel flow){reconnectChoice.CheckedChanged+=(s,e)=>{if(!reconnectChoice.Checked&&viewer!=null&&!viewer.IsDisposed)viewer.UserIntentCancellation.Cancel();};flow.Controls.Add(reconnectChoice);flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new System.Drawing.Size(620,0),Text="통신이 끊긴 경우에만 새 연결을 한 번 요청합니다. 상대가 다시 승인해야 합니다. 내가 종료했거나 비밀번호·보안 확인에 실패한 경우에는 다시 요청하지 않습니다."});}
        private void ConfigureReconnect(ViewerForm opened,SecureSessionAdapter adapter,ControlPlaneClient c,string sid){
            long epoch=autoEpoch,revision=c.CredentialRevision;string own=c.DeviceId,peer=adapter.Descriptor.TargetDeviceId;
            opened.NetworkInterrupted=()=>TryFreshNetworkRequest(opened,adapter,c,sid,own,peer,epoch,revision);
        }
        private async void TryFreshNetworkRequest(ViewerForm opened,SecureSessionAdapter adapter,ControlPlaneClient c,string sid,string own,string peer,long epoch,long revision){
            if(reconnecting||!reconnectAvailable||!reconnectChoice.Checked||!IsRecoverableNetworkFailure(adapter.Failure)||!ReconnectCurrent(c,epoch,revision)||opened.FrameCount==0)return;
            if(peerTrust.Find(c.ServerUrl,c.CertificateSha256,peer)==null)return;
            reconnectAvailable=false;reconnecting=true;var ct=automationCancellation.Token;
            try{
                // A fresh authoritative live snapshot is mandatory; an EOF alone never proves a network failure.
                using(var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct,opened.UserIntentCancellation.Token)){
                    bounded.CancelAfter(10000);
                    var rows=await c.SnapshotAsync(bounded.Token);
                    if(!ReconnectCurrent(c,epoch,revision)||!reconnectChoice.Checked||opened.UserClosed||bounded.IsCancellationRequested||!rows.Any(x=>x.SessionId==sid&&x.Status=="Connected"&&x.InitiatorDeviceId==own&&x.TargetDeviceId==peer))return;
                    await opened.CloseSessionAsync();if(!ReconnectCurrent(c,epoch,revision)||!reconnectChoice.Checked||opened.UserClosed||bounded.IsCancellationRequested)return;
                    if(!opened.IsDisposed)opened.CloseForReconnect();RemoveSecureChannel(sid,adapter);
                    bounded.Token.ThrowIfCancellationRequested();
                    var item=await c.RequestFreshApprovalAsync(peer,bounded.Token);
                    if(!ReconnectCurrent(c,epoch,revision)||!reconnectChoice.Checked||opened.UserClosed||bounded.IsCancellationRequested)return;
                    if(item.InitiatorDeviceId!=own||item.TargetDeviceId!=peer||item.Status!="Pending"||item.AutoApprovalEligible)throw new InvalidOperationException("새 승인 요청의 응답이 일치하지 않습니다.");
                    Add(item);Record(item);autoRequest=new AutoRequest{SessionId=item.SessionId,OwnId=own,TargetId=peer,Binding=ClientBinding(c),Client=c,Epoch=epoch};
                    status.Text="네트워크 단절 후 새 연결 요청을 한 번 보냈습니다. 상대 PC에서 다시 승인하면 새 보안 연결을 시작합니다.";
                }
            }catch(OperationCanceledException){}
            catch(Exception ex){if(!shutdownBusy)status.Text="자동 재요청을 완료하지 못했습니다. 직접 다시 연결해 주세요: "+ex.Message;}
            finally{reconnecting=false;}
        }
        private bool ReconnectCurrent(ControlPlaneClient c,long epoch,long revision){return !shutdownBusy&&!IsDisposed&&registrationVerified&&ReferenceEquals(client,c)&&autoEpoch==epoch&&c.CredentialRevision==revision&&UseSecure;}
    }
}
