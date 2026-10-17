using System;
using System.Drawing;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    // Non-modal, owned consent prompt for one incoming session. Reject is the default: Escape, the close box and any
    // dismissal without an explicit [승인] click never approve. Decided fires at most once; Dismiss() closes silently
    // when the session ended or the automation authority was invalidated.
    internal sealed class ConnectionConsentForm : Form
    {
        private readonly Button approveButton=new Button { Name="ApproveConnection", Text="승인 (화면·입력 공유 허용)", AutoSize=true, FlatStyle=FlatStyle.Flat };
        private readonly Button rejectButton=new Button { Name="RejectConnection", Text="거절", AutoSize=true, FlatStyle=FlatStyle.Flat };
        private readonly TextBox requesterBox=new TextBox { Name="ConsentRequesterId", ReadOnly=true, Width=300, Font=new Font("Malgun Gothic",14f,FontStyle.Bold), BorderStyle=BorderStyle.None };
        private bool finished;
        internal string SessionId { get; private set; }
        internal string RequesterId { get; private set; }
        internal event Action<ConnectionConsentForm,bool> Decided;

        internal ConnectionConsentForm(string sessionId,string requesterId)
        {
            SessionId=sessionId;RequesterId=requesterId;requesterBox.Text=requesterId;
            Name="ConnectionConsentForm";Text="RemoteBridge — 연결 요청 동의";
            Font=new Font("Malgun Gothic",9.75f);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=true;StartPosition=FormStartPosition.CenterParent;
            AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;KeyPreview=true;
            var layout=new FlowLayoutPanel { FlowDirection=FlowDirection.TopDown, AutoSize=true, WrapContents=false, Padding=new Padding(14) };
            layout.Controls.Add(new Label { Text="다음 장치가 이 PC에 연결을 요청했습니다.", AutoSize=true });
            layout.Controls.Add(requesterBox);
            layout.Controls.Add(new Label { Name="ConsentPermission", Text="승인하면 상대가 연결되는 즉시 이 PC의 전체 바탕화면이 전송되고, 상대가 마우스와 키보드로 이 PC를 제어할 수 있습니다.\r\n공유는 [세션 관리]의 [화면 공유 중지]로 언제든 중지할 수 있습니다. 모르는 요청이면 거절하세요.", AutoSize=true, MaximumSize=new Size(460,0), ForeColor=Color.DarkRed, Font=new Font(Font,FontStyle.Bold), Margin=new Padding(3,8,3,8) });
            var buttons=new FlowLayoutPanel { AutoSize=true, WrapContents=false };
            buttons.Controls.Add(rejectButton);buttons.Controls.Add(approveButton);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            CancelButton=rejectButton; // Escape rejects; there is intentionally no AcceptButton.
            approveButton.Click+=(s,e)=>Finish(true);
            rejectButton.Click+=(s,e)=>Finish(false);
            FormClosing+=(s,e)=>{if(!finished)Raise(false);};
            Shown+=(s,e)=>rejectButton.Focus();
        }
        private void Raise(bool approve){if(finished)return;finished=true;var h=Decided;if(h!=null)h(this,approve);}
        private void Finish(bool approve){Raise(approve);if(!IsDisposed)Close();}
        internal void Dismiss(){finished=true;if(!IsDisposed)Close();}
    }
}
