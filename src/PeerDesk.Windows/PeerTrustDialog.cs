using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed class PeerTrustDialog:Form {
        internal PeerTrustDialog(string peer,string fingerprint,string comparisonCode){
            Name="PeerTrustConfirmation";Text="처음 연결하는 PC 확인";ClientSize=new Size(560,310);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),FlowDirection=FlowDirection.TopDown,WrapContents=false};
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(520,0),Text="상대 ID "+peer+"의 보안 키를 처음 확인합니다.\r\n상대방과 전화·메신저 등으로 아래 확인 코드를 비교해 주세요. 서버 이름이나 PW가 같다는 이유만으로 이 PC를 신뢰하지 않습니다."});
            flow.Controls.Add(new TextBox{Name="PairingCode",Text=comparisonCode,ReadOnly=true,Width=510,Font=new Font(Font.FontFamily,16,FontStyle.Bold)});
            flow.Controls.Add(new Label{Text="상대 PC 보안 키 지문 (SHA-256)",AutoSize=true});
            flow.Controls.Add(new TextBox{Name="PeerIdentityFingerprint",Text=fingerprint,ReadOnly=true,Width=510});
            var check=new CheckBox{Name="PeerCodeCompared",Text="상대방과 확인 코드가 같은지 직접 확인했습니다.",Width=510};flow.Controls.Add(check);
            var row=new FlowLayoutPanel{AutoSize=true};var ok=new Button{Name="TrustPeer",Text="이 PC 신뢰",DialogResult=DialogResult.OK,Enabled=false,AutoSize=true};
            var cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel,AutoSize=true};row.Controls.Add(ok);row.Controls.Add(cancel);flow.Controls.Add(row);
            check.CheckedChanged+=(s,e)=>ok.Enabled=check.Checked;Controls.Add(flow);AcceptButton=ok;CancelButton=cancel;
        }
        // Role-independent transcript comparison. This is displayed to both humans; never auto-approved.
        internal static string ComparisonCode(byte[] first,byte[] second){
            if(first==null||second==null||first.Length>4096||second.Length>4096)throw new ArgumentException("Invalid pairing Hello.");
            // Validate both signed public identities before showing a comparison code.
            SecurePeerChannel.CandidateFingerprint(first);SecurePeerChannel.CandidateFingerprint(second);
            string a=Convert.ToBase64String(first),b=Convert.ToBase64String(second);bool order=String.CompareOrdinal(a,b)<0;
            byte[] hash=SecurePeerIdentity.Hash(Encoding.ASCII.GetBytes("RemoteBridge/pair-code/v1|"+(order?a:b)+"|"+(order?b:a)));
            return String.Join("-",Enumerable.Range(0,4).Select(i=>BitConverter.ToString(hash,i*3,3).Replace("-","")));
        }
    }
}
