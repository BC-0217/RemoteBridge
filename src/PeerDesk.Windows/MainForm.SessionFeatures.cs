using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed partial class MainForm {
        sealed class IncomingFile {internal FileTransferCoordinator Receiver;internal TransferIdentity Identity;internal string Destination,DisplayName,Sha256;}
        sealed class FeatureState {
            internal SecureSessionAdapter Adapter;internal ControlPlaneClient Client;internal string Sid,Peer,ServerBinding;internal long Epoch;internal long CredentialRevision;
            internal int Pending,Outgoing;internal bool Closed;internal readonly Dictionary<string,IncomingFile> Incoming=new Dictionary<string,IncomingFile>();
            internal readonly HashSet<string> Cancelled=new HashSet<string>();
            internal readonly HashSet<string> Offers=new HashSet<string>();
            internal readonly Dictionary<string,long> Completed=new Dictionary<string,long>();
            internal readonly ConcurrentDictionary<string,TaskCompletionSource<long>> Acks=new ConcurrentDictionary<string,TaskCompletionSource<long>>();
        }
        readonly Dictionary<string,FeatureState> featureStates=new Dictionary<string,FeatureState>();
        private bool FeatureLive(FeatureState f,int bit){return !shutdownBusy&&!f.Closed&&f.Adapter.Ready&&(f.Adapter.Descriptor.PermissionBits&bit)!=0&&f.Client.CredentialRevision==f.CredentialRevision;}
        private void InitializeFeatureChannel(SecureSessionAdapter adapter,ControlPlaneClient c,string sid){
            string peer=adapter.IsInitiator?adapter.Descriptor.TargetDeviceId:adapter.Descriptor.InitiatorDeviceId;
            var binding=SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(Encoding.UTF8.GetBytes(c.ServerUrl+"|"+c.CertificateSha256+"|"+peer)));
            var f=new FeatureState{Adapter=adapter,Client=c,Sid=sid,Peer=peer,ServerBinding=binding,Epoch=BitConverter.ToInt64(HexBytes(adapter.Descriptor.TransportEpoch),0)&Int64.MaxValue,CredentialRevision=c.CredentialRevision};
            featureStates[sid]=f;
            adapter.FeaturePayload+=(channel,payload)=>{
                if(Interlocked.Increment(ref f.Pending)>8){Interlocked.Decrement(ref f.Pending);throw new InvalidDataException("Feature queue bound exceeded.");}
                if(IsDisposed||!IsHandleCreated){Interlocked.Decrement(ref f.Pending);return;}
                BeginInvoke(new Action(async ()=>{
                    try{if(channel==3)ReceiveExplicitText(f,payload);else if(channel==4)await ReceiveFileMessage(f,payload);else if(channel==0)ReceiveFileAck(f,payload);}
                    catch(Exception ex){if(!shutdownBusy)status.Text=UiLocalization.Text("편의 기능 요청을 처리하지 못했습니다: ")+ex.Message;}
                    finally{Interlocked.Decrement(ref f.Pending);}
                }));
            };
        }
        private void CloseFeatureChannel(string sid) {
            FeatureState f;if(!featureStates.TryGetValue(sid,out f))return;featureStates.Remove(sid);f.Closed=true;
            foreach(var ack in f.Acks.Values)ack.TrySetCanceled();
            foreach(var incoming in f.Incoming.Values)try{incoming.Receiver.Cancel(incoming.Identity);}catch{}
            f.Incoming.Clear();
        }
        private void InitializeViewerFeatureActions(ViewerForm view,SecureSessionAdapter adapter,ControlPlaneClient c,string sid){
            FeatureState f=featureStates[sid];
            view.SendClipboardText=()=>SendExplicitText(f,view);
            view.SendSelectedFile=()=>SendPickedFile(f,view);
        }
        private async Task SendExplicitText(FeatureState f,Form owner){
                if(!FeatureLive(f,4))throw new InvalidOperationException(UiLocalization.Text("상대가 텍스트 받기를 허용하지 않았습니다."));
                if(!Clipboard.ContainsText()){status.Text=UiLocalization.Text("클립보드에 텍스트가 없습니다.");return;}
                string value=Clipboard.GetText();
                if(MessageBox.Show(owner,UiLocalization.Text("현재 클립보드 텍스트를 상대 PC에 보낼까요? 비밀번호·개인정보가 포함되지 않았는지 확인해 주세요."),UiLocalization.Text("텍스트 보내기"),MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                if(!FeatureLive(f,4))return;
                var endpoint=new TextClipboardCoordinator(x=>FeatureLive(f,4),(x,t)=>{});
                await f.Adapter.SendAsync(3,endpoint.SendText(FeatureIdentity(f,"clipboard"),value),CancellationToken.None);
        }
        private async Task SendPickedFile(FeatureState f,Form owner){
                if(!FeatureLive(f,8))throw new InvalidOperationException(UiLocalization.Text("상대가 파일 받기를 허용하지 않았습니다."));
                using(var pick=new OpenFileDialog{Title=UiLocalization.Text("상대 PC에 보낼 파일 선택"),CheckFileExists=true,Multiselect=false})
                    if(pick.ShowDialog(owner)==DialogResult.OK){
                        if(f.Outgoing>=2)throw new InvalidOperationException(UiLocalization.Text("파일은 동시에 2개까지만 보낼 수 있습니다."));
                        f.Outgoing++;try{await SendFile(f,pick.FileName,owner);}finally{f.Outgoing--;}
                    }
        }
        private async Task ShareFeatureAction(bool file){
            FeatureState f;if(sharing==null||!featureStates.TryGetValue(sharing.SessionId,out f)){status.Text=UiLocalization.Text("텍스트·파일 전송은 종단 간 암호화 연결에서만 사용할 수 있습니다.");return;}
            try{if(file)await SendPickedFile(f,this);else await SendExplicitText(f,this);}
            catch(OperationCanceledException){status.Text=UiLocalization.Text("작업을 취소했습니다.");}
            catch(Exception ex){if(!shutdownBusy)MessageBox.Show(this,ex.Message,UiLocalization.Text("요청 실패"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        private void ReceiveExplicitText(FeatureState f,byte[] payload){
            if(!FeatureLive(f,4)||!textPermissionChoice.Checked)throw new UnauthorizedAccessException(UiLocalization.Text("텍스트 받기가 허용되지 않았습니다."));
            var endpoint=new TextClipboardCoordinator(x=>FeatureLive(f,4)&&textPermissionChoice.Checked,(x,text)=>{
                if(MessageBox.Show(this,UiLocalization.Text("상대 PC ")+f.Peer+UiLocalization.Text("에서 보낸 텍스트를 내 클립보드에 넣을까요? 자동으로 붙여넣거나 실행하지 않습니다."),UiLocalization.Text("텍스트 받기"),MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                if(!FeatureLive(f,4)||!textPermissionChoice.Checked)return;if(text.Length==0)Clipboard.Clear();else Clipboard.SetText(text);status.Text=UiLocalization.Text("받은 텍스트를 클립보드에 넣었습니다.");
            });
            endpoint.AcceptText(FeatureIdentity(f,"clipboard"),payload);
        }
        private TransferIdentity FeatureIdentity(FeatureState f,string id){return new TransferIdentity(f.Peer,f.ServerBinding,f.Sid+"|"+f.Adapter.Descriptor.TransportEpoch,id,f.Adapter.Descriptor.PermissionRevision,f.Epoch);}
        static byte[] HexBytes(string hex){var b=new byte[hex.Length/2];for(int i=0;i<b.Length;i++)b[i]=Convert.ToByte(hex.Substring(i*2,2),16);return b;}
        static string FileHash(string file,long? prefix=null){using(var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))using(var h=SHA256.Create()){
            long left=prefix??s.Length;var b=new byte[65536];while(left>0){int n=s.Read(b,0,(int)Math.Min(left,b.Length));if(n==0)throw new EndOfStreamException();h.TransformBlock(b,0,n,b,0);left-=n;}h.TransformFinalBlock(new byte[0],0,0);return SecurePeerIdentity.Hex(h.Hash);}}
        static string ChunkHash(byte[] bytes){return SecurePeerIdentity.Hex(SecurePeerIdentity.Hash(bytes));}
        static byte[] FileMessage(byte kind,string id,Action<BinaryWriter> extra=null){using(var s=new MemoryStream())using(var w=new BinaryWriter(s,new UTF8Encoding(false,true))){w.Write(kind);w.Write(id);if(extra!=null)extra(w);return s.ToArray();}}
        static string ReadBounded(BinaryReader r,int max){string value=r.ReadString();if(value.Length>max)throw new InvalidDataException("Feature field bound.");return value;}
        private async Task<long> FileRequest(FeatureState f,string id,byte[] packet,CancellationToken ct){
            var ack=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!f.Acks.TryAdd(id,ack))throw new InvalidOperationException("A transfer request is already pending.");
            try{if(!FeatureLive(f,8))throw new UnauthorizedAccessException();await f.Adapter.SendAsync(4,packet,ct);
                var timeout=Task.Delay(120000,ct);if(await Task.WhenAny(ack.Task,timeout)!=ack.Task){ct.ThrowIfCancellationRequested();throw new TimeoutException(UiLocalization.Text("상대 PC가 파일 요청에 응답하지 않았습니다."));}long result=await ack.Task;if(!FeatureLive(f,8))throw new OperationCanceledException(UiLocalization.Text("연결이 종료되었습니다."));return result;
            }finally{TaskCompletionSource<long> removed;f.Acks.TryRemove(id,out removed);}
        }
        private void ReceiveFileAck(FeatureState f,byte[] data){
            if(!FeatureLive(f,8)||data.Length<1||data[0]!=70)return; // reserved encrypted control messages are handled by their own feature
            using(var s=new MemoryStream(data,false))using(var r=new BinaryReader(s,new UTF8Encoding(false,true))){
                r.ReadByte();string id=ReadBounded(r,32);bool accepted=r.ReadBoolean();long offset=r.ReadInt64();if(s.Position!=s.Length||offset<0||offset>FileTransferCoordinator.MaxFileBytes)throw new InvalidDataException("Invalid file ack.");
                TaskCompletionSource<long> ack;if(f.Acks.TryGetValue(id,out ack)){if(accepted)ack.TrySetResult(offset);else ack.TrySetException(new IOException(UiLocalization.Text("상대가 파일 수신을 취소했거나 수신에 실패했습니다.")));}
            }
        }
        private Task SendFileAck(FeatureState f,string id,bool accepted,long offset){return f.Adapter.SendAsync(0,FileMessage(70,id,w=>{w.Write(accepted);w.Write(offset);}),CancellationToken.None);}
        private async Task ReceiveFileMessage(FeatureState f,byte[] data){
            if(!FeatureLive(f,8)||data.Length>262144)throw new UnauthorizedAccessException(UiLocalization.Text("파일 권한이 없습니다."));
            string id=null;
            try{
                using(var s=new MemoryStream(data,false))using(var r=new BinaryReader(s,new UTF8Encoding(false,true))){
                    byte kind=r.ReadByte();id=ReadBounded(r,32);Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new InvalidDataException("Invalid transfer ID.");
                    IncomingFile incoming;
                    long completed;if(f.Completed.TryGetValue(id,out completed)){if(kind==3&&s.Position==s.Length){await SendFileAck(f,id,true,completed);return;}throw new InvalidDataException("Completed transfer ID.");}
                    if(kind==1){
                        if(f.Incoming.ContainsKey(id)||f.Offers.Contains(id)||f.Cancelled.Contains(id)||f.Completed.Count>=64)throw new InvalidDataException("Duplicate or exhausted transfer ID.");
                        string name=ReadBounded(r,255);long length=r.ReadInt64();string hash=ReadBounded(r,64);if(s.Position!=s.Length||length<0||length>FileTransferCoordinator.MaxFileBytes||f.Incoming.Count+f.Offers.Count>=2||!filePermissionChoice.Checked)throw new InvalidDataException("File offer denied.");
                        if(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidDataException("Unsafe offer filename.");
                        f.Offers.Add(id);try{
                        using(var save=new SaveFileDialog{Title=f.Peer+UiLocalization.Text(" 님이 보낸 파일 — 저장 위치를 선택하면 수신을 허용합니다"),FileName=name,OverwritePrompt=true}){
                            if(save.ShowDialog(this)!=DialogResult.OK||!FeatureLive(f,8)||f.Cancelled.Contains(id)){await SendFileAck(f,id,false,0);return;}
                            string destination=Path.GetFullPath(save.FileName);if(File.Exists(destination))throw new IOException(UiLocalization.Text("기존 파일은 덮어쓰지 않습니다. 다른 이름을 선택해 주세요."));
                            bool consent=true;var identity=FeatureIdentity(f,id);
                            var coordinator=new FileTransferCoordinator(Path.GetDirectoryName(destination),x=>x.Equals(identity)&&FeatureLive(f,8),x=>{bool allow=consent;consent=false;return allow;});
                            var progress=coordinator.AcceptIncoming(identity,name,length,hash);
                            f.Incoming.Add(id,new IncomingFile{Receiver=coordinator,Identity=identity,Destination=destination,DisplayName=name,Sha256=hash});
                            await SendFileAck(f,id,true,progress.Offset);status.Text=UiLocalization.Text("파일 받기 시작: ")+name;
                        }}finally{f.Offers.Remove(id);}
                    }else if(kind==4){
                        if(s.Position!=s.Length)throw new InvalidDataException("Trailing cancel.");
                        if(f.Cancelled.Count>=64)throw new InvalidDataException("Cancelled transfer bound.");f.Cancelled.Add(id);
                        if(f.Incoming.TryGetValue(id,out incoming)){incoming.Receiver.Cancel(incoming.Identity);status.Text=UiLocalization.Text("파일 수신 일시 중지 — 미완료 파일 보관 위치: ")+incoming.Receiver.GetProgress(incoming.Identity).PartialPath;}
                    }else{
                        if(!f.Incoming.TryGetValue(id,out incoming))throw new InvalidDataException("Unknown incoming transfer.");
                        if(kind==2){long offset=r.ReadInt64();string hash=ReadBounded(r,64);int n=r.ReadInt32();if(n<1||n>65536||s.Length-s.Position!=n)throw new InvalidDataException("File chunk bound.");
                            byte[] chunk=r.ReadBytes(n);var progress=await Task.Run(()=>incoming.Receiver.AppendChunk(incoming.Identity,offset,chunk,hash));
                            await SendFileAck(f,id,true,progress.Offset);
                        }else if(kind==3){if(s.Position!=s.Length)throw new InvalidDataException("Trailing complete.");string saved=await Task.Run(()=>incoming.Receiver.Complete(incoming.Identity,incoming.Destination));
                            long completedSize=new FileInfo(saved).Length;f.Completed.Add(id,completedSize);f.Incoming.Remove(id);await SendFileAck(f,id,true,completedSize);status.Text=UiLocalization.Text("파일 수신 완료: ")+Path.GetFileName(saved);
                        }else if(kind==5){long offset=r.ReadInt64();string prefix=ReadBounded(r,64);if(s.Position!=s.Length)throw new InvalidDataException("Trailing resume.");
                            // Explicit resume is a new local consent. Recreate only the nonblocking one-use approval closure.
                            if(MessageBox.Show(this,UiLocalization.Text("일시 중지한 파일 수신을 계속할까요?"),UiLocalization.Text("파일 이어받기"),MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)throw new UnauthorizedAccessException();
                            var previous=incoming.Receiver.GetProgress(incoming.Identity);bool consent=true;
                            var restored=new FileTransferCoordinator(Path.GetDirectoryName(incoming.Destination),x=>x.Equals(incoming.Identity)&&FeatureLive(f,8),x=>{bool ok=consent;consent=false;return ok;});
                            var p=await Task.Run(()=>restored.ResumeFresh(incoming.Identity,incoming.DisplayName,previous.Size,incoming.Sha256,previous.PartialPath,offset,prefix));
                            incoming.Receiver=restored;f.Cancelled.Remove(id);await SendFileAck(f,id,true,p.Offset);
                        }else throw new InvalidDataException("Unknown file command.");
                    }
                }
            }catch{if(id!=null&&FeatureLive(f,8))try{await SendFileAck(f,id,false,0);}catch{}throw;}
        }
        private async Task SendFile(FeatureState f,string path,Form owner){
            var info=new FileInfo(path);if(info.Length>FileTransferCoordinator.MaxFileBytes)throw new InvalidOperationException(UiLocalization.Text("파일은 256MiB 이하만 보낼 수 있습니다."));
            string id=Guid.NewGuid().ToString("N"),hash=await Task.Run(()=>FileHash(path));bool paused=false;long offset=0;
            using(var cancel=new CancellationTokenSource())
            using(var dialog=new Form{Text=UiLocalization.Text("파일 보내기"),ClientSize=new Size(420,130),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedToolWindow}){
                var label=new Label{Left=12,Top=12,Width=395,Text=info.Name};var progress=new ProgressBar{Left=12,Top=42,Width=395};
                var pause=new Button{Text=UiLocalization.Text("일시 중지"),Left=200,Top=82,Width=100};var stop=new Button{Text=UiLocalization.Text("취소"),Left=305,Top=82,Width=100};
                pause.Click+=(s,e)=>{paused=!paused;pause.Text=paused?UiLocalization.Text("계속 보내기"):UiLocalization.Text("일시 중지");};stop.Click+=(s,e)=>cancel.Cancel();dialog.FormClosing+=(s,e)=>cancel.Cancel();
                dialog.Controls.AddRange(new Control[]{label,progress,pause,stop});dialog.Show(owner);
                try{
                    await FileRequest(f,id,FileMessage(1,id,w=>{w.Write(info.Name);w.Write(info.Length);w.Write(hash);}),cancel.Token);
                    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                        while(offset<info.Length){
                            if(paused){
                                await f.Adapter.SendAsync(4,FileMessage(4,id),CancellationToken.None);
                                while(paused){cancel.Token.ThrowIfCancellationRequested();if(!FeatureLive(f,8))throw new UnauthorizedAccessException();await Task.Delay(100,cancel.Token);}
                                string prefix=await Task.Run(()=>FileHash(path,offset));long resumed=await FileRequest(f,id,FileMessage(5,id,w=>{w.Write(offset);w.Write(prefix);}),cancel.Token);if(resumed!=offset)throw new InvalidDataException("Resume offset mismatch.");
                            }
                            cancel.Token.ThrowIfCancellationRequested();int size=(int)Math.Min(65536,info.Length-offset);var bytes=new byte[size];int read=stream.Read(bytes,0,size);if(read!=size)throw new EndOfStreamException();
                            long sentOffset=offset;long ack=await FileRequest(f,id,FileMessage(2,id,w=>{w.Write(sentOffset);w.Write(ChunkHash(bytes));w.Write(bytes.Length);w.Write(bytes);}),cancel.Token);
                            if(ack!=offset+bytes.Length)throw new InvalidDataException("File ACK offset mismatch.");offset=ack;progress.Value=info.Length==0?100:(int)(offset*100/info.Length);label.Text=info.Name+" · "+offset+" / "+info.Length+" bytes";
                        }
                    }
                    long finalOffset=await FileRequest(f,id,FileMessage(3,id),cancel.Token);if(finalOffset!=info.Length)throw new InvalidDataException("File completion offset mismatch.");status.Text=UiLocalization.Text("파일을 보냈습니다: ")+info.Name;
                }catch{if(FeatureLive(f,8))try{await f.Adapter.SendAsync(4,FileMessage(4,id),CancellationToken.None);}catch{}throw;}
                finally{dialog.Close();}
            }
        }
    }
}
