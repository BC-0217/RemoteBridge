using System;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        private SignedUpdateCoordinator signedUpdates;
        private void AddUpdateOptions(FlowLayoutPanel flow) {
            signedUpdates=new SignedUpdateCoordinator();
            flow.Controls.Add(new Label{Text="프로그램 업데이트",AutoSize=true,Font=new System.Drawing.Font(Font,System.Drawing.FontStyle.Bold)});
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new System.Drawing.Size(620,0),Text="이 시험판에 내장된 제작자 확인 키로 서명을 확인한 업데이트 폴더만 사용할 수 있습니다. 서버 관리키·서버 비밀번호와는 다릅니다. 기존 공개 배포본을 자동으로 믿지 않으며, 업데이트 자동 다운로드·프로그램 강제 종료·관리자 권한 요청은 하지 않습니다. 되돌리기도 확인된 서명 버전만 가능합니다."});
            var initialize=new Button{Name="InitializeUpdateTrust",Text="업데이트 처음 설정하기",AutoSize=true};
            var stage=new Button{Name="VerifySignedUpdate",Text="받은 업데이트 폴더 확인",AutoSize=true};
            var apply=new Button{Name="ApplySignedUpdate",Text="확인한 업데이트 적용",AutoSize=true};
            var rollback=new Button{Name="RollbackSignedUpdate",Text="확인된 이전 버전으로 되돌리기",AutoSize=true};
            initialize.Click+=(s,e)=>UpdateAction(()=>{
                if(MessageBox.Show(this,"이 PC의 현재 설치 폴더에 업데이트 보호 기록을 처음 만들까요? 기존·손상된 기록은 덮어쓰지 않습니다.","업데이트 보호",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                signedUpdates.InitializeTrustFloorExplicit();experienceWarning.Text="업데이트 보호 기록을 만들었습니다.";
            });
            stage.Click+=(s,e)=>UpdateAction(()=>{using(var pick=new FolderBrowserDialog{Description="제작자에게 받은 서명된 업데이트 폴더를 선택하세요."})if(pick.ShowDialog(this)==DialogResult.OK){
                signedUpdates.StageBundle(pick.SelectedPath);experienceWarning.Text="서명·파일 검증 완료. 적용을 누르면 프로그램 정상 종료 후 교체합니다.";
            }});
            apply.Click+=(s,e)=>UpdateAction(()=>LaunchUpdate(false));
            rollback.Click+=(s,e)=>UpdateAction(()=>LaunchUpdate(true));
            flow.Controls.Add(initialize);flow.Controls.Add(stage);flow.Controls.Add(apply);flow.Controls.Add(rollback);
        }
        private void UpdateAction(Action action){try{if(shutdownBusy||lifecycleBusy||sharing!=null||viewer!=null||known.Exists(x=>IsLive(x.Status)))throw new InvalidOperationException("연결·승인 요청을 모두 종료한 다음 업데이트하세요.");action();}catch(Exception ex){experienceWarning.Text="업데이트 요청 실패: "+ex.Message;}}
        private void LaunchUpdate(bool rollback){
            if(MessageBox.Show(this,rollback?"검증된 이전 서명 버전으로 복구할까요? 보호 기록은 낮아지지 않습니다.":"검증한 업데이트를 적용할까요? 이 프로그램이 정상 종료된 뒤 파일을 교체합니다.","RemoteBridge 업데이트",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            if(rollback)signedUpdates.StartRollbackHelper();else signedUpdates.StartApplyHelper();
            MessageBox.Show(this,"적용 도우미가 준비되었습니다. 프로그램이 정상 종료되면 교체합니다. 이후 RemoteBridge.exe를 다시 실행하세요.","업데이트",MessageBoxButtons.OK,MessageBoxIcon.Information);Close();
        }
    }
}
