using System;
using System.Linq;
using System.Text;

namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        private Action roomDisplayRefresh;
        private void SetDeviceDisplay(string id){
            if(String.IsNullOrEmpty(id))UiLocalization.Bind(deviceBox,"(미등록)");
            else{UiLocalization.Unbind(deviceBox);deviceBox.Text=id;}
        }
        private void InitializeSemanticStatusLocalization(){
            UiLocalization.Bind(currentDisplay,"미확인");
            UiLocalization.Bind(passwordState,"미확인");
            UiLocalization.Bind(publishedAlias,"확인 불가");
            UiLocalization.Bind(aliasMirror,"접속 이름: 확인 불가");
            UiLocalization.Bind(roomStatusBox,"미확인 — 서버 관리키를 입력하고 [불러오기]를 누르세요.");
            UiLocalization.Observe(roomStatusBox,()=>{var refresh=roomDisplayRefresh;if(refresh!=null)refresh();});
        }
        // Rendering only: never re-run owner requests or credential/profile binding on a cosmetic change.
        private void SetRoomStatusRenderer(Func<string> render){
            UiLocalization.Unbind(roomStatusBox);
            roomDisplayRefresh=()=>roomStatusBox.Text=render();
            roomDisplayRefresh();
        }
        private static string RenderRoomSummary(string action,string id,bool passwordConfigured,int count,string ids,bool selfPublish,bool discoverable,bool canCopyPassword){
            var sb=new StringBuilder(UiLocalization.Text(action));
            sb.Append(UiLocalization.Format("\r\n현재 서버 ID: {0}",id??UiLocalization.Text("없음")));
            sb.Append(UiLocalization.Format("\r\n서버 PW: {0}",UiLocalization.Text(passwordConfigured?"설정됨 (원문 조회 불가)":"미설정")));
            sb.Append(UiLocalization.Format("\r\n유지 중인 ID ({0}/8): {1}",count,count==0?UiLocalization.Text("없음"):ids));
            sb.Append(UiLocalization.Format("\r\n직접 게시: {0}",UiLocalization.Text(selfPublish?"사용 가능":"이 서버에서 사용 불가 — 외부 게시(고급)에서 중앙 승인 코드 필요")));
            if(id!=null&&discoverable)sb.Append(UiLocalization.Format("\r\n복사: {0}",UiLocalization.Text(canCopyPassword?"서버 ID와 이번에 설정한 서버 PW":"서버 ID만 (이 창에서 PW를 새로 설정하지 않아 PW는 복사되지 않음)")));
            if(!discoverable)sb.Append(UiLocalization.Format("\r\n주의: {0}",RoomDirectoryMismatch));
            return sb.ToString();
        }
        internal static string PeerInputFailureMessage(PeerInputFailureReason reason){
            switch(reason){
                case PeerInputFailureReason.QueueAge:return UiLocalization.Text("상대 PC의 입력 전송이 지연되어 안전을 위해 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.QueueOverflow:return UiLocalization.Text("상대 PC의 입력 대기열이 가득 차 안전을 위해 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.AuthorityExpired:return UiLocalization.Text("상대 PC에서 입력 실행 권한을 제때 확인하지 못해 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.ContextMismatch:return UiLocalization.Text("상대 PC의 연결 정보가 변경되어 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.NativeFailure:return UiLocalization.Text("상대 PC에서 키보드·마우스 입력을 처리하지 못해 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.InputSafetyFailure:return UiLocalization.Text("상대 PC의 입력 안전 검사로 화면 공유가 중단되었습니다.");
                case PeerInputFailureReason.ProtocolFailure:return UiLocalization.Text("상대 PC의 입력 데이터 검증에 실패해 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.StatefulUnsupported:return UiLocalization.Text("상대 PC에서 필요한 입력 기능을 사용할 수 없어 화면 공유를 중단했습니다.");
                case PeerInputFailureReason.SendFailed:return UiLocalization.Text("상대 PC의 입력 통신에 실패해 화면 공유를 중단했습니다.");
                default:return UiLocalization.Text("상대 PC에서 입력 오류를 알려 화면 공유를 중단했습니다. 정상 종료가 아닙니다.");
            }
        }
    }
}
