using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    internal sealed partial class MainForm
    {
        // Saved inputs are unverified drafts only; the in-memory list in storedSettings is authoritative
        // and every settings rewrite must carry it forward without merging entries back from disk.
        // Restoring never joins, looks up or calls an owner API; it only fills text boxes for the exact matching context.
        private readonly CheckBox saveInputsBox=new CheckBox { Name="SaveInputs", Text="입력 기억", Checked=true, AutoSize=true, Margin=new Padding(12,6,4,0) };
        private readonly Label savedInputsStatus=new Label { Name="SavedInputsStatus", AutoSize=false, Size=new Size(120,32), AutoEllipsis=true, ForeColor=Color.DimGray, Padding=new Padding(0,6,0,0) };
        private Button saveInputsNowButton,forgetSavedInputsButton;
        private readonly ToolTip savedInputsTip=new ToolTip();
        private bool savedInputsWriteBusy,inputSaveFailure;
        // Bumped by every Forget/off so late async results snapshotted before it can never resurrect a forgotten context.
        private long savedInputsRevision;
        private bool restoringInputs,roomPasswordDirty,guestPasswordRestored;
        private string lastHostInputKey;
        private SavedInputEntry lastHostContext;
        private bool previousHostRole;

        private List<SavedInputEntry> CloneStoredSavedInputs()
        {
            var result=new List<SavedInputEntry>();
            var source=storedSettings==null?null:storedSettings.SavedInputs;
            if(source==null)return result;
            foreach(var entry in source)if(entry!=null)result.Add(entry.Clone());
            return result;
        }

        private void AddSavedInputsControls(FlowLayoutPanel row)
        {
            saveInputsNowButton=Button("입력 저장",SaveInputsNow);saveInputsNowButton.Name="SaveInputsNow";
            forgetSavedInputsButton=Button("저장 입력 삭제",ForgetSavedInputs);forgetSavedInputsButton.Name="ForgetSavedInputs";
            saveInputsNowButton.Width=82;forgetSavedInputsButton.Width=106;
            row.Controls.Add(saveInputsBox);row.Controls.Add(saveInputsNowButton);row.Controls.Add(forgetSavedInputsButton);
            row.Controls.Add(CreateHostHelpButton("HelpSavedInputs","?",HostHelpForm.OwnerKey));
            row.Controls.Add(savedInputsStatus);
        }

        // Called once at the end of construction, after the owner-key TextChanged hook is attached.
        private void InitializeSavedInputs()
        {
            roomJoinPasswordBox.TextChanged+=(s,e)=>{if(!restoringInputs&&!ownerBusy)roomPasswordDirty=true;};
            guestPasswordBox.TextChanged+=(s,e)=>{if(!restoringInputs)guestPasswordRestored=false;};
            guestAliasBox.TextChanged+=(s,e)=>OnGuestAliasChanged();
            saveInputsBox.CheckedChanged+=(s,e)=>OnSaveInputsToggled();
            roleBox.SelectedIndexChanged+=(s,e)=>{ if(!restoringInputs&&!settingsInvalid&&!lifecycleBusy&&!ownerBusy&&!shutdownBusy){string error;if(previousHostRole)CaptureHostDraft(false,out error);else if(guestAliasBox.TextLength>0)CaptureGuestDraft(false,out error); } previousHostRole=HostRoleSelected; RestoreRememberChoice(); UpdateSavedInputsEnabled(); };
            if(settingsInvalid){SetSavedInputsStatus("설정 파일 오류로 입력을 저장/복원하지 않습니다.",true);UpdateSavedInputsEnabled();return;}
            OnHostContextChanged();
            RestoreGuestAtStartup();
            UpdateSavedInputsEnabled();
        }

        private bool HostRoleSelected{get{return roleBox.SelectedIndex==1;}}
        private void SetSavedInputsStatus(string text,bool warning){savedInputsTip.SetToolTip(savedInputsStatus,text??"");savedInputsStatus.Text=text??"";savedInputsStatus.ForeColor=warning?Color.DarkRed:Color.DimGray;}
        private void UpdateSavedInputsEnabled()
        {
            if(saveInputsNowButton==null)return;
            bool on=!settingsInvalid&&!ownerBusy&&!lifecycleBusy&&!shutdownBusy;
            saveInputsBox.Enabled=on;saveInputsNowButton.Enabled=on&&saveInputsBox.Checked;forgetSavedInputsButton.Enabled=on;
        }

        // ---- contexts ----------------------------------------------------------------------------------------------
        private SavedInputEntry HostInputContextOrNull()
        {
            try{
                var p=ReadProfile();
                var e=new SavedInputEntry { Role="host",ServerUrl=p.RendezvousUrl,RelayUrl=p.RelayUrl,CertificateSha256=p.CertificateSha256 };
                SettingsStore.ValidateSavedInput(e);
                return e;
            }catch{return null;}
        }
        private SavedInputEntry GuestInputContextOrNull(string alias)
        {
            if(directoryBootstrap==null||!DirectoryClient.IsValidAlias(alias))return null;
            try{
                var e=new SavedInputEntry { Role="guest",RegistryUrl=directoryBootstrap.RegistryUrl,RegistryCertificateSha256=directoryBootstrap.CertificateSha256,
                    RegistryIdentity=directoryBootstrap.RegistryIdentity,ServerId=alias };
                SettingsStore.ValidateSavedInput(e);
                return e;
            }catch{return null;}
        }
        private static string KeyOrNull(SavedInputEntry e){if(e==null)return null;try{return SettingsStore.ContextKey(e);}catch{return null;}}
        private static bool SameTarget(SavedInputEntry e,ServerProfile p)
        {
            return e!=null&&p!=null&&String.Equals(e.ServerUrl,p.RendezvousUrl,StringComparison.Ordinal)&&String.Equals(e.RelayUrl,p.RelayUrl,StringComparison.Ordinal)
                &&String.Equals(e.CertificateSha256??"",p.CertificateSha256??"",StringComparison.OrdinalIgnoreCase);
        }

        // ---- list mutation / persistence ---------------------------------------------------------------------------
        // Returns a new authoritative list with one context updated, or null (tombstone honoured, revision changed, invalid, full).
        private List<SavedInputEntry> WithSavedInput(SavedInputEntry context,Action<SavedInputEntry> mutate,long? revision,bool overrideTombstone,out string error)
        {
            error=null;
            if(settingsInvalid){error="설정 파일 오류로 저장하지 않았습니다.";return null;}
            if(context==null)return null;
            if(revision.HasValue&&revision.Value!=savedInputsRevision)return null;
            string key=KeyOrNull(context);if(key==null)return null;
            var list=CloneStoredSavedInputs();
            int index=list.FindIndex(x=>KeyOrNull(x)==key);
            var existing=index<0?null:list[index];
            if(existing!=null&&!existing.Remember&&!overrideTombstone)return null;
            var entry=existing==null?context.Clone():existing.Clone();
            if(!entry.Remember){entry.Remember=true;}
            mutate(entry);
            try{entry=SettingsStore.ValidateSavedInput(entry);}
            catch(Exception ex){error="입력 형식이 올바르지 않아 저장하지 않았습니다(기존 저장값 유지): "+ex.Message;return null;}
            if(index<0&&list.Count>=SettingsStore.MaximumSavedInputs){error="저장 입력이 16개로 가득 찼습니다. 필요 없는 항목을 [저장 입력 삭제]하세요.";return null;}
            if(index>=0)list.RemoveAt(index);
            list.Add(entry); // most recently saved last; deterministic startup choice
            return list;
        }
        private bool CommitSavedInputs(List<SavedInputEntry> list)
        {
            if(list==null||settingsInvalid||savedInputsWriteBusy)return false;
            var s=storedSettings;
            var next=new SettingsState { ProfileJson=!s.IsRegistered&&IsProfileValid(profileBox.Text)?profileBox.Text:(s.ProfileJson??""),IsRegistered=s.IsRegistered,DeviceId=s.DeviceId,BearerToken=s.BearerToken,
                TemporaryPassword=s.TemporaryPassword,PasswordExpiresAt=s.PasswordExpiresAt,PasswordAutoAccess=s.PasswordAutoAccess,BoundServerUrl=s.BoundServerUrl,BoundCertificateSha256=s.BoundCertificateSha256,SavedInputs=list };
            try{savedInputsWriteBusy=true;settingsStore.Save(next);}
            catch(Exception ex){inputSaveFailure=true;SetSavedInputsStatus("입력 저장 실패(기존 파일 보존): "+ex.Message,true);return false;}
            finally{savedInputsWriteBusy=false;}
            storedSettings=next;
            return true;
        }
        private bool UpdateSavedInput(SavedInputEntry context,Action<SavedInputEntry> mutate,long? revision,bool overrideTombstone)
        {
            string error;
            var list=WithSavedInput(context,mutate,revision,overrideTombstone,out error);
            if(error!=null){SetSavedInputsStatus(error,true);return false;}
            if(list==null)return false;
            if(!CommitSavedInputs(list))return false;
            SetSavedInputsStatus("입력을 이 Windows 사용자 계정에 암호화해 저장했습니다(미확인 초안).",false);
            return true;
        }

        // ---- draft capture ------------------------------------------------------------------------------------------
        // Empty boxes keep the stored value (boxes are cleared after submissions); explicit removal is [저장 입력 삭제].
        private bool CaptureHostDraft(bool overrideTombstone,out string error)
        {
            error=null;
            var context=HostInputContextOrNull();
            if(context==null){error="연결 설정(주소·지문)이 올바르지 않아 저장하지 않았습니다.";return false;}
            var existing=SettingsStore.FindSavedInput(storedSettings.SavedInputs,context);
            if(!overrideTombstone&&existing!=null&&!existing.Remember)return false;
            string id=OwnerRoomInfo.Normalize(roomServerIdBox.Text);
            if(id.Length>0&&!OwnerRoomInfo.IsValidServerId(id)){error=OwnerRoomInfo.ServerIdRuleMessage;return false;}
            string owner=ownerSecretBox.Text;
            if(owner.Length>0&&(owner.Length<32||owner.Length>1024||!OwnerKeyBoundToCurrent())){error="관리키가 32~1024자가 아니거나 현재 연결 설정에 묶여 있지 않아 저장하지 않았습니다.";return false;}
            string pw=hostJoinPasswordBox.TextLength>0?hostJoinPasswordBox.Text:(roomPasswordDirty?roomJoinPasswordBox.Text:"");
            if(pw.Length>0&&(pw.Length<8||pw.Length>128)){error="서버 PW는 8~128자여야 합니다(기존 저장값 유지).";return false;}
            var list=WithSavedInput(context,e=>{if(id.Length>0)e.ServerId=id;if(owner.Length>0)e.OwnerKey=owner;if(pw.Length>0)e.PasswordDraft=pw;},null,overrideTombstone,out error);
            return error==null&&list!=null&&CommitSavedInputs(list);
        }
        private bool CaptureGuestDraft(bool overrideTombstone,out string error)
        {
            error=null;
            string alias=OwnerRoomInfo.Normalize(guestAliasBox.Text);
            var context=GuestInputContextOrNull(alias);
            if(context==null){error="서버 ID가 올바르지 않거나 공통 조회 설정이 없어 저장하지 않았습니다.";return false;}
            var existing=SettingsStore.FindSavedInput(storedSettings.SavedInputs,context);
            if(!overrideTombstone&&existing!=null&&!existing.Remember)return false;
            string pw=guestPasswordBox.Text;
            if(pw.Length>0&&(pw.Length<8||pw.Length>128)){error="서버 PW는 8~128자여야 합니다(기존 저장값 유지).";return false;}
            var list=WithSavedInput(context,e=>{if(pw.Length>0)e.PasswordDraft=pw;},null,overrideTombstone,out error);
            return error==null&&list!=null&&CommitSavedInputs(list);
        }

        // ---- UI actions (synchronous; disabled during lifecycle operations) ---------------------------------------
        private void SaveInputsNow()
        {
            if(settingsInvalid||ownerBusy||lifecycleBusy||shutdownBusy)return;
            if(!saveInputsBox.Checked){SetSavedInputsStatus("[입력 기억]을 켜야 저장할 수 있습니다.",true);return;}
            string error;
            bool ok=HostRoleSelected?CaptureHostDraft(true,out error):CaptureGuestDraft(true,out error);
            if(ok){SetSavedInputsStatus("현재 역할의 입력을 암호화해 저장했습니다(미확인 초안, 인증 증명 아님).",false);}
            else if(error!=null)SetSavedInputsStatus(error,true);
        }
        private void ForgetSavedInputs()
        {
            if(settingsInvalid||ownerBusy||lifecycleBusy||shutdownBusy)return;
            ForgetCurrentRole("현재 역할의 저장 입력을 삭제했습니다. 장치 등록·연결 기록·다른 역할은 유지됩니다.");
        }
        private void OnSaveInputsToggled()
        {
            if(restoringInputs)return;
            UpdateSavedInputsEnabled();
            if(saveInputsBox.Checked){savedInputsRevision++;SetSavedInputsStatus("입력 기억 켜짐 — 삭제했던 항목은 [입력 저장]을 눌러야 다시 저장됩니다.",false);return;}
            if(settingsInvalid||ownerBusy||lifecycleBusy||shutdownBusy){savedInputsRevision++;SetSavedInputsStatus("작업 중이라 저장 입력 삭제는 작업 후 [저장 입력 삭제]로 하세요. 이후 결과는 저장되지 않습니다.",true);return;}
            ForgetCurrentRole("입력 기억 꺼짐 — 현재 역할의 저장 입력을 삭제했습니다.");
        }
        // Persists a tombstone (Remember=false, no secrets, no resolved target) for the current role's exact context only.
        private void ForgetCurrentRole(string done)
        {
            savedInputsRevision++;
            SavedInputEntry context=HostRoleSelected?HostInputContextOrNull():GuestInputContextOrNull(OwnerRoomInfo.Normalize(guestAliasBox.Text));
            if(HostRoleSelected){ClearOwnerKey();ClearRoom();restoringInputs=true;try{roomServerIdBox.Clear();roomJoinPasswordBox.Clear();}finally{restoringInputs=false;}roomPasswordDirty=false;}
            else{restoringInputs=true;try{guestPasswordBox.Clear();guestAliasBox.Clear();}finally{restoringInputs=false;}guestPasswordRestored=false;}
            if(context==null){SetSavedInputsStatus("화면 입력은 지웠습니다. 저장 항목을 찾을 현재 연결 설정/서버 ID가 없어 파일은 바꾸지 않았습니다.",true);return;}
            string key=KeyOrNull(context);
            var list=CloneStoredSavedInputs();
            int index=list.FindIndex(x=>KeyOrNull(x)==key);
            var tomb=context.Clone();tomb.Remember=false;tomb.OwnerKey=null;tomb.PasswordDraft=null;tomb.VerifiedJoinPassword=null;
            if(tomb.Role=="guest"){tomb.ServerUrl=null;tomb.RelayUrl=null;tomb.CertificateSha256=null;}else tomb.ServerId=null;
            if(index>=0)list[index]=tomb;
            else if(list.Count<SettingsStore.MaximumSavedInputs)list.Add(tomb);
            else{SetSavedInputsStatus("화면 입력은 지웠습니다. 저장 항목이 없어 파일은 바꾸지 않았습니다.",false);return;}
            if(CommitSavedInputs(list))SetSavedInputsStatus(done,false);
        }

        // ---- restore -------------------------------------------------------------------------------------------------
        // Hooked after ReconcileOwnerKey on every profile change: old room drafts are cleared before the new exact context restores.
        private void OnHostContextChanged()
        {
            if(settingsInvalid)return;
            var context=HostInputContextOrNull();
            string key=KeyOrNull(context);
            if(key==lastHostInputKey)return;
            if(lastHostInputKey!=null){restoringInputs=true;try{roomServerIdBox.Clear();roomJoinPasswordBox.Clear();}finally{restoringInputs=false;}roomPasswordDirty=false;}
            lastHostInputKey=key;lastHostContext=context==null?null:context.Clone();
            if(key==null)return;
            var entry=SettingsStore.FindSavedInput(storedSettings.SavedInputs,context);
            if(entry==null||!entry.Remember)return;
            restoringInputs=true;
            try{
                if(entry.OwnerKey!=null&&ownerSecretBox.TextLength==0)ownerSecretBox.Text=entry.OwnerKey; // binds via OnOwnerSecretChanged to this exact profile
                if(entry.ServerId!=null)roomServerIdBox.Text=entry.ServerId;
                if(entry.PasswordDraft!=null)roomJoinPasswordBox.Text=entry.PasswordDraft;
            }finally{restoringInputs=false;}
            roomPasswordDirty=false; // restored PW is never sent unless edited
            SetSavedInputsStatus("저장된 입력을 복원했습니다 — 미확인 초안이며 서버에서 다시 확인해야 합니다.",false);
        }
        private void RestoreGuestAtStartup()
        {
            if(directoryBootstrap==null||guestAliasBox.TextLength>0)return;
            SavedInputEntry pick=null;
            foreach(var e in storedSettings.SavedInputs)
            {
                if(e==null||e.Role!="guest"||!e.Remember)continue;
                var context=GuestInputContextOrNull(e.ServerId);
                if(context!=null&&KeyOrNull(context)==KeyOrNull(e))pick=e;
            }
            if(pick==null)return;
            restoringInputs=true;
            try{guestAliasBox.Text=pick.ServerId;}finally{restoringInputs=false;}
            RestoreGuestPassword(pick);
        }
        private void OnGuestAliasChanged()
        {
            if(restoringInputs||settingsInvalid)return;
            var context=GuestInputContextOrNull(OwnerRoomInfo.Normalize(guestAliasBox.Text));
            var entry=context==null?null:SettingsStore.FindSavedInput(storedSettings.SavedInputs,context);
            if(entry!=null&&entry.Remember&&entry.PasswordDraft!=null&&(guestPasswordBox.TextLength==0||guestPasswordRestored)){RestoreGuestPassword(entry);return;}
            if(guestPasswordRestored){restoringInputs=true;try{guestPasswordBox.Clear();}finally{restoringInputs=false;}guestPasswordRestored=false;}
        }
        private void RestoreGuestPassword(SavedInputEntry entry)
        {
            if(entry.PasswordDraft==null)return;
            restoringInputs=true;
            try{guestPasswordBox.Text=entry.PasswordDraft;}finally{restoringInputs=false;}
            guestPasswordRestored=true;
            SetSavedInputsStatus("저장된 서버 ID/PW 초안을 복원했습니다 — 자동 참가하지 않습니다.",false);
        }

        private void RestoreRememberChoice()
        {
            var probe=HostRoleSelected?HostInputContextOrNull():GuestInputContextOrNull(OwnerRoomInfo.Normalize(guestAliasBox.Text));
            var entry=probe==null?null:SettingsStore.FindSavedInput(storedSettings.SavedInputs,probe);
            restoringInputs=true;try{saveInputsBox.Checked=entry==null||entry.Remember;}finally{restoringInputs=false;}
        }
        // Preserve the old context before profile callbacks clear its masked key and draft.
        private void PreserveHostBeforeProfileChange(bool force=false)
        {
            if(restoringInputs||settingsInvalid||lastHostContext==null||ownerBusy||lifecycleBusy||shutdownBusy)return;
            var now=HostInputContextOrNull();
            if(!force&&KeyOrNull(now)==lastHostInputKey)return;
            string owner=ownerSecretBox.Text,pw=roomPasswordDirty?roomJoinPasswordBox.Text:"",id=OwnerRoomInfo.Normalize(roomServerIdBox.Text);
            string error;
            var list=WithSavedInput(lastHostContext,e=>{if(owner.Length>0)e.OwnerKey=owner;if(pw.Length>0)e.PasswordDraft=pw;if(id.Length>0)e.ServerId=id;},null,false,out error);
            if(error!=null)SetSavedInputsStatus(error,true);else if(list!=null)CommitSavedInputs(list);
            savedInputsRevision++;
        }

        // ---- async success hooks (called inside lifecycleGate, before finally clears boxes) ----------------------------
        private void RecordOwnerSuccess(string secret,string binding,long revision)
        {
            if(shutdownBusy||binding==null||binding!=CurrentBindingOrNull())return;
            UpdateSavedInput(HostInputContextOrNull(),e=>e.OwnerKey=secret,revision,false);
        }
        // Server actually accepted a join password for the current host profile: host draft + guest entries resolved to this exact target.
        private void RecordHostJoinSuccess(string serverId,string join,long revision)
        {
            if(shutdownBusy)return;
            var context=HostInputContextOrNull();if(context==null)return;
            string error;
            var list=WithSavedInput(context,e=>{if(serverId!=null)e.ServerId=serverId;if(join!=null){e.PasswordDraft=join;e.VerifiedJoinPassword=join;}},revision,false,out error);
            if(list==null)return;
            if(join!=null)
                foreach(var g in list)
                    if(g.Role=="guest"&&g.Remember&&g.ServerUrl!=null&&g.ServerUrl==context.ServerUrl&&g.RelayUrl==context.RelayUrl&&
                       String.Equals(g.CertificateSha256,context.CertificateSha256,StringComparison.OrdinalIgnoreCase)){g.PasswordDraft=join;g.VerifiedJoinPassword=join;}
            if(CommitSavedInputs(list)){roomPasswordDirty=false;SetSavedInputsStatus("서버가 확인한 서버 ID/PW 초안을 저장했습니다.",false);}
        }
        // GuestJoin: snapshot before lookup. Returns the existing resolved-target entry (or null).
        private SavedInputEntry CaptureGuestBeforeLookup(string alias,string password,out long revision)
        {
            revision=savedInputsRevision;
            var context=GuestInputContextOrNull(alias);
            var existing=context==null?null:SettingsStore.FindSavedInput(storedSettings.SavedInputs,context);
            UpdateSavedInput(context,e=>e.PasswordDraft=password,revision,false);
            revision=savedInputsRevision;
            return existing!=null&&existing.Remember&&existing.ServerUrl!=null?existing:null;
        }
        private const string GuestTargetChangedMessage="이 서버 ID가 이전에 참가했던 서버(주소·지문)와 다른 서버를 가리킵니다. 등록과 PW 전송을 하지 않았습니다. 새 서버가 맞다면 [저장 입력 삭제]로 이 서버 ID의 저장 항목을 지운 뒤 PW를 다시 입력하세요.";
        private void RequireGuestTarget(SavedInputEntry previous,ServerProfile resolved)
        {
            if(previous!=null&&!SameTarget(previous,resolved))
            {
                restoringInputs=true;try{guestPasswordBox.Clear();}finally{restoringInputs=false;}guestPasswordRestored=false;
                throw new InvalidOperationException(GuestTargetChangedMessage);
            }
        }
        // Applied to the SettingsState that is saved together with the new device credentials.
        private List<SavedInputEntry> GuestJoinSavedInputs(string alias,string password,ServerProfile resolved,long revision)
        {
            if(shutdownBusy)return CloneStoredSavedInputs();
            string error;
            var list=WithSavedInput(GuestInputContextOrNull(alias),e=>{e.PasswordDraft=password;e.VerifiedJoinPassword=password;e.ServerUrl=resolved.RendezvousUrl;e.RelayUrl=resolved.RelayUrl;e.CertificateSha256=resolved.CertificateSha256;},
                revision,false,out error);
            return list??CloneStoredSavedInputs();
        }

        // Close: persist the current role's valid draft before owner key clearing and shutdown flags. Tombstones are honoured.
        private bool PersistInputsOnClose()
        {
            if(settingsInvalid)return true;
            inputSaveFailure=false;string hostError=null,guestError=null;
            try{
                if(lastHostContext!=null)CaptureHostDraft(false,out hostError);
                if(guestAliasBox.TextLength>0)CaptureGuestDraft(false,out guestError);
            }catch(Exception ex){hostError=ex.Message;}
            if(inputSaveFailure||hostError!=null||guestError!=null){
                string message="입력을 저장하지 못해 창을 유지합니다. 입력을 수정하거나 해당 역할의 [입력 기억]을 끄세요. "+(hostError??guestError??"기존 파일은 보존되었습니다.");
                SetSavedInputsStatus(message,true);status.Text=message;return false;
            }
            savedInputsRevision++;return true;
        }
    }
}
