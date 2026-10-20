using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    internal sealed class ViewerForm : Form
    {
        private readonly ControlPlaneClient client;
        private readonly string sessionId;
        private readonly MediaWire wire;
        private readonly PictureBox picture=new PictureBox();
        private readonly Label state=new Label();
        private readonly Panel imageViewport=new Panel { Dock=DockStyle.Fill };
        private readonly ComboBox zoomChoice=new ComboBox{Name="ViewerZoom",DropDownStyle=ComboBoxStyle.DropDownList,Width=105};
        private readonly Label diagnostics=new Label{Name="ViewerDiagnostics",AutoSize=true,Padding=new Padding(6,8,0,0)};
        private bool fullScreen;private FormBorderStyle previousBorder;private FormWindowState previousWindowState;
        private readonly System.Windows.Forms.Timer pollTimer=new System.Windows.Forms.Timer();
        private CancellationTokenSource stop=new CancellationTokenSource();
        private long generation,sequence,lastSequence;
        private bool frameValid,polling,closingFinished,closingCloseQueued;
        private Task closeTask;
        private long lastMoveTick=-1000; // monotonic per-viewer time; Environment.TickCount becomes negative after ~24.9 days
        // One bounded ordered queue drained by a single pump: wire order == sequence order. Only a move directly behind
        // another pending move is coalesced (never across an edge). Overflow or an edge older than 750ms fails closed.
        private const int MaxPendingSends=32, MaxQueueAgeMs=750, StatefulMoveThrottleMs=25, LegacyMoveThrottleMs=100, HeartbeatMs=200;
        private sealed class PendingInput { public InputCommand Command; public long EnqueuedMs; }
        private readonly System.Collections.Generic.List<PendingInput> pending=new System.Collections.Generic.List<PendingInput>();
        private bool pumping, sending, inputFailed;
        private readonly System.Collections.Generic.Queue<long> sentInputTicks=new System.Collections.Generic.Queue<long>();
        private readonly System.Diagnostics.Stopwatch queueClock=System.Diagnostics.Stopwatch.StartNew();
        // Stateful mode only when the latest frame carried X-Stateful-Input-Version: 1.
        private int statefulVersion;
        private readonly System.Collections.Generic.HashSet<int> heldKeys=new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.HashSet<int> heldButtons=new System.Collections.Generic.HashSet<int>();
        private readonly System.Windows.Forms.Timer heartbeatTimer=new System.Windows.Forms.Timer();
        private bool Stateful { get { return statefulVersion==1; } }
        // Rolling FPS over unique (generation,sequence) frames actually rendered, not GET count.
        private const int FpsWindowMs=2000;
        private readonly System.Collections.Generic.Queue<long> uniqueFrameTicks=new System.Collections.Generic.Queue<long>();
        private readonly System.Diagnostics.Stopwatch fpsClock=System.Diagnostics.Stopwatch.StartNew();
        // The target may still be preparing its share right after Connected: 409 is tolerated only before the first valid
        // frame and only for a bounded grace; any later 409 (or other error) stays a real authorization failure.
        private const int PreparingGraceMs=10000;
        private readonly System.Diagnostics.Stopwatch preparingWatch=new System.Diagnostics.Stopwatch();
        private bool firstFrameReceived,remoteEnded;
        internal Func<Task> SendClipboardText,SendSelectedFile;
        internal Action NetworkInterrupted;private bool networkReported;
        internal bool UserClosed {get;private set;}private bool reconnectClosing;
        internal readonly CancellationTokenSource UserIntentCancellation=new CancellationTokenSource();
        internal void CloseForReconnect(){reconnectClosing=true;Close();}
        private bool InputAllowed { get { return wire.Secure==null||(wire.Secure.Descriptor.PermissionBits&2)!=0; } }
        internal string EndReason { get; private set; }
        internal string SessionId { get { return sessionId; } }
        internal long LastFrameSequence { get { return lastSequence; } }
        internal int FrameCount { get; private set; }
        internal PictureBox Display { get { return picture; } }
        // Diagnostics: correlation shared with the secure adapter; poll failures are rate-limited (first, then one per 5s).
        private readonly long diagCorrelation;private long diagPollFailures,diagLastPollLogMs=-1;private bool diagCloseLogged;
        internal long DiagCorrelation { get { return diagCorrelation; } }
        private void DiagPollFailure(Exception ex,bool preparing)
        {
            diagPollFailures++;long now=fpsClock.ElapsedMilliseconds;
            if(diagLastPollLogMs>=0&&now-diagLastPollLogMs<5000)return;
            diagLastPollLogMs=now;
            DiagnosticLog.Failure(DiagEvent.ViewerPollFailure,diagCorrelation,ex,DiagValue.Of(DiagKey.Failures,diagPollFailures),DiagValue.Of(DiagKey.Count,(long)FrameCount),
                DiagValue.Of(DiagKey.Phase,preparing),DiagValue.Of(DiagKey.ElapsedMs,preparingWatch.ElapsedMilliseconds),DiagValue.Of(DiagKey.Direct,wire.Secure!=null&&wire.Secure.HasDirectScreen));
            diagPollFailures=0;
        }

        internal ViewerForm(ControlPlaneClient control,string id,SecureSessionAdapter secure=null)
        {
            client=control;sessionId=id;wire=new MediaWire(client){Secure=secure};
            diagCorrelation=secure!=null?secure.DiagCorrelation:DiagnosticLog.NewCorrelation();
            Text="RemoteBridge — 원격 화면 보기 — 입력은 유효 프레임 수신 후 활성";
            Width=900;Height=650;KeyPreview=true;
            picture.Dock=DockStyle.Fill;picture.SizeMode=PictureBoxSizeMode.Zoom;picture.BackColor=Color.Black;picture.TabStop=true;
            state.Dock=DockStyle.Bottom;state.Height=28;state.Text="프레임 대기 중";
            imageViewport.Controls.Add(picture);Controls.Add(imageViewport);Controls.Add(state);
            var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,WrapContents=false,Padding=new Padding(4)};
            toolbar.Controls.Add(new Label{Text="보기 배율",AutoSize=true,Padding=new Padding(0,8,2,0)});
            zoomChoice.Items.AddRange(new object[]{"창에 맞춤","100%","125%","150%","200%"});zoomChoice.SelectedIndex=0;toolbar.Controls.Add(zoomChoice);
            zoomChoice.SelectedIndexChanged+=(s,e)=>{ReleaseAllLocal();ApplyViewerZoom();};
            var fullscreenButton=new Button{Name="ViewerFullScreen",Text="전체화면",AutoSize=true};toolbar.Controls.Add(fullscreenButton);
            fullscreenButton.Click+=(s,e)=>{ReleaseAllLocal();if(!fullScreen){previousBorder=FormBorderStyle;previousWindowState=WindowState;WindowState=FormWindowState.Normal;FormBorderStyle=FormBorderStyle.None;WindowState=FormWindowState.Maximized;fullScreen=true;fullscreenButton.Text="돌아오기";}else{WindowState=FormWindowState.Normal;FormBorderStyle=previousBorder;WindowState=previousWindowState;fullScreen=false;fullscreenButton.Text="전체화면";}};
            var finish=new Button{Name="ViewerStop",Text="연결 종료",AutoSize=true,ForeColor=Color.DarkRed};toolbar.Controls.Add(finish);finish.Click+=(s,e)=>Close();
            var clipboardButton=new Button{Name="ViewerSendText",Text="텍스트 보내기",AutoSize=true,Enabled=secure!=null&&(secure.Descriptor.PermissionBits&4)!=0};
            var fileButton=new Button{Name="ViewerSendFile",Text="파일 보내기",AutoSize=true,Enabled=secure!=null&&(secure.Descriptor.PermissionBits&8)!=0};
            clipboardButton.Click+=async (s,e)=>await RunFeatureAction(clipboardButton,()=>SendClipboardText);
            fileButton.Click+=async (s,e)=>await RunFeatureAction(fileButton,()=>SendSelectedFile);
            toolbar.Controls.Add(clipboardButton);toolbar.Controls.Add(fileButton);
            toolbar.Controls.Add(diagnostics);Controls.Add(toolbar);
            pollTimer.Interval=100;pollTimer.Tick+=async (s,e)=>await PollOnce();
            Shown+=(s,e)=>{preparingWatch.Start();pollTimer.Start();DiagnosticLog.Event(DiagEvent.ViewerStart,diagCorrelation,DiagValue.Of(DiagKey.Secure,wire.Secure!=null),DiagValue.Of(DiagKey.BindInputs,InputAllowed));};
            FormClosing+=OnClosing;
            FormClosed+=(s,e)=>{pollTimer.Stop();heartbeatTimer.Stop();heartbeatTimer.Dispose();stop.Cancel();if(picture.Image!=null){picture.Image.Dispose();picture.Image=null;}stop.Dispose();};
            heartbeatTimer.Interval=HeartbeatMs;heartbeatTimer.Tick+=(s,e)=>{
                // Only while this controller is active and something is locally held; never restores a released hold.
                if(Stateful&&frameValid&&!inputFailed&&ContainsFocus&&(heldKeys.Count+heldButtons.Count)>0)Enqueue(new InputCommand{Kind="holdHeartbeat"});
            };
            Shown+=(s,e)=>heartbeatTimer.Start();
            picture.MouseMove+=(s,e)=>{if(frameValid) SendPointer("move",e.Location,0,heldButtons.Count>0);};
            picture.MouseClick+=(s,e)=>{if(frameValid&&!Stateful) SendPointer("click",e.Location,ButtonIndex(e.Button),false);};
            picture.MouseDown+=(s,e)=>{
                if(!frameValid||!Stateful)return;
                int b=ButtonIndex(e.Button); if(b<0||heldButtons.Contains(b))return;
                picture.Focus(); picture.Capture=true;
                if(SendPointer("mouseDown",e.Location,b,false))heldButtons.Add(b);
            };
            picture.MouseUp+=(s,e)=>{
                int b=ButtonIndex(e.Button); if(b<0||!heldButtons.Remove(b))return;
                SendPointer("mouseUp",e.Location,b,true); // clamped onto the image when released outside
                if(heldButtons.Count==0)picture.Capture=false;
            };
            picture.MouseWheel+=(s,e)=>{
                if(!frameValid||!Stateful||e.Delta==0)return;
                int d=Math.Max(-1200,Math.Min(1200,e.Delta));
                Point p=e.Location; Rectangle r=ImageRect(); if(!r.Contains(p))return;
                Enqueue(new InputCommand{Kind="wheel",X=ScaleX(p,r),Y=ScaleY(p,r),WheelDelta=d});
            };
            picture.MouseCaptureChanged+=(s,e)=>{if(!picture.Capture&&heldButtons.Count>0)ReleaseAllLocal();};
            Deactivate+=(s,e)=>ReleaseAllLocal();
            KeyUp+=(s,e)=>{
                if(!Stateful)return;
                int vk=(int)e.KeyCode; e.Handled=true;
                if(heldKeys.Remove(vk))Enqueue(new InputCommand{Kind="keyUp",KeyCode=vk});
            };
            KeyDown+=async (s,e)=>{
                if(!frameValid||Stateful)return;
                if(e.KeyCode==Keys.ControlKey||e.KeyCode==Keys.ShiftKey||e.KeyCode==Keys.Menu)return;
                if(e.KeyCode==Keys.Enter||e.KeyCode==Keys.Back||e.KeyCode==Keys.Tab||e.KeyCode==Keys.Escape||e.KeyCode==Keys.Delete)
                    await SendKey((int)e.KeyCode);
            };
            KeyPress+=async (s,e)=>{if(frameValid&&!Stateful&&!Char.IsControl(e.KeyChar)&&!Char.IsSurrogate(e.KeyChar))await SendText(e.KeyChar);};
        }
        private async Task RunFeatureAction(Button button,Func<Func<Task>> action)
        {
            if(closingFinished||stop.IsCancellationRequested)return;
            button.Enabled=false;
            try {var callback=action();if(callback==null)throw new InvalidOperationException("이 연결에서는 사용할 수 없습니다.");await callback();}
            catch(OperationCanceledException){state.Text="작업을 취소했습니다.";}
            catch(Exception ex){if(!IsDisposed)MessageBox.Show(this,ex.Message,"요청 실패",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally {if(!button.IsDisposed&&!stop.IsCancellationRequested)button.Enabled=true;}
        }
        // Stateful: every VK down (Ctrl/Shift/Alt, Tab, arrows, ...) is consumed here so dialog navigation and KeyPress (WM_CHAR)
        // never duplicate it. OS-reserved sequences (Ctrl+Alt+Del, Alt+Tab, Win+L, UAC secure desktop) are not hooked.
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            const int WM_KEYDOWN=0x0100,WM_SYSKEYDOWN=0x0104;
            if(Stateful&&frameValid&&!inputFailed&&(msg.Msg==WM_KEYDOWN||msg.Msg==WM_SYSKEYDOWN)) {
                int vk=(int)(keyData&Keys.KeyCode);
                if(vk>=1&&vk<=254&&!InputSafety.IsMouseVk(vk)) {
                    // Auto-repeat re-sends keyDown (target keeps a single held entry).
                    if(Enqueue(new InputCommand{Kind="keyDown",KeyCode=vk}))heldKeys.Add(vk);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg,keyData);
        }
        private static int ButtonIndex(MouseButtons b) { return b==MouseButtons.Left?0:b==MouseButtons.Right?1:b==MouseButtons.Middle?2:-1; }
        // Clears local held state and asks the target to release (no automatic re-press later).
        private void ReleaseAllLocal()
        {
            bool had=heldKeys.Count+heldButtons.Count>0;
            heldKeys.Clear(); heldButtons.Clear();
            if(picture.Capture)picture.Capture=false;
            if(had&&Stateful&&frameValid&&!inputFailed) {
                // Preserve only the actual HTTP request in flight. Unsent downs must not run after focus loss.
                int keep=sending&&pending.Count>0?1:0;
                if(pending.Count>keep)pending.RemoveRange(keep,pending.Count-keep);
                Enqueue(new InputCommand{Kind="releaseAll"});
            }
        }
        internal Task CloseSessionAsync()
        {
            if(closeTask==null)closeTask=CloseSessionCore();
            return closeTask;
        }
        private async Task CloseSessionCore()
        {
            pollTimer.Stop(); stop.Cancel();
            try { await client.RevokeAsync(sessionId,CancellationToken.None); } catch { }
        }
        private async void OnClosing(object sender,FormClosingEventArgs e)
        {
            if(!reconnectClosing){UserClosed=true;UserIntentCancellation.Cancel();}
            if(closingFinished)return;
            if(!diagCloseLogged){diagCloseLogged=true;DiagnosticLog.Event(DiagEvent.ViewerClose,diagCorrelation,DiagValue.Of(DiagKey.Close,reconnectClosing?DiagViewerClose.Reconnect:DiagViewerClose.User),
                DiagValue.Of(DiagKey.HadError,inputFailed),DiagValue.Of(DiagKey.Value,remoteEnded),DiagValue.Of(DiagKey.Count,(long)FrameCount));}
            e.Cancel=true;
            ReleaseAllLocal();
            await CloseSessionAsync();
            closingFinished=true;
            if(!closingCloseQueued&&!IsDisposed&&IsHandleCreated){closingCloseQueued=true;BeginInvoke(new Action(Close));}
        }
        private async Task PollOnce()
        {
            if(polling||remoteEnded||stop.IsCancellationRequested)return;polling=true;
            try {
                var networkWatch=System.Diagnostics.Stopwatch.StartNew();
                FramePacket f=await wire.GetFrameForDisplayAsync(sessionId,stop.Token,firstFrameReceived?generation:0,firstFrameReceived?lastSequence:0);
                double frameRtt=networkWatch.Elapsed.TotalMilliseconds;
                if(stop.IsCancellationRequested||IsDisposed)return;
                if(f==null){ReleaseAllLocal();frameValid=false;state.Text="화면 공유가 중지되었습니다.";return;}
                if(f.IsDuplicate&&firstFrameReceived&&f.Generation==generation&&f.Sequence==lastSequence)return; // already shown; structure validated
                if(generation!=0&&generation!=f.Generation) {
                    frameValid=false; pending.Clear(); heldKeys.Clear(); heldButtons.Clear();
                    if(picture.Capture)picture.Capture=false;
                }
                generation=f.Generation;
                if(f.StatefulInputVersion!=statefulVersion) {
                    // Version change mid-stream: drop local holds (target guard releases on its own TTL/lease) and never
                    // send stateful kinds to a target that no longer advertises them.
                    heldKeys.Clear(); heldButtons.Clear(); if(picture.Capture)picture.Capture=false;
                    statefulVersion=f.StatefulInputVersion;
                }
                pollTimer.Interval=f.MediaOptimizationVersion==1?MediaWire.OptimizedFrameIntervalMs:100;
                var decodeWatch=System.Diagnostics.Stopwatch.StartNew();
                using(var ms=new System.IO.MemoryStream(f.Jpeg,false))
                using(var src=Image.FromStream(ms,false,true))
                {
                    if(src.Width!=f.Width||src.Height!=f.Height)throw new InvalidOperationException("decoded JPEG size mismatch");
                    var next=new Bitmap(src);
                    var old=picture.Image;picture.Image=next;if(old!=null)old.Dispose();
                }
                double decodeMs=decodeWatch.Elapsed.TotalMilliseconds;ApplyViewerZoom();
                diagnostics.Text=(wire.Secure!=null&&wire.Secure.HasDirectScreen?"화면 직결·입력 중계":"중계")+" · 프레임 요청 "+frameRtt.ToString("0")+"ms · 표시 처리 "+decodeMs.ToString("0")+"ms · "+(f.Jpeg.Length/1024.0).ToString("0.0")+"KiB";
                frameValid=!inputFailed&&InputAllowed;firstFrameReceived=true;lastSequence=f.Sequence;FrameCount++;
                if(FrameCount==1)DiagnosticLog.Event(DiagEvent.ViewerFirstFrame,diagCorrelation,DiagValue.Of(DiagKey.ElapsedMs,preparingWatch.ElapsedMilliseconds),DiagValue.Of(DiagKey.ResponseMs,(long)frameRtt),
                    DiagValue.Of(DiagKey.Stateful,f.StatefulInputVersion==1),DiagValue.Of(DiagKey.Optimized,f.MediaOptimizationVersion==1),DiagValue.Of(DiagKey.Direct,wire.Secure!=null&&wire.Secure.HasDirectScreen));
                long now=fpsClock.ElapsedMilliseconds; uniqueFrameTicks.Enqueue(now);
                while(uniqueFrameTicks.Count>0&&now-uniqueFrameTicks.Peek()>FpsWindowMs)uniqueFrameTicks.Dequeue();
                double fps=uniqueFrameTicks.Count*1000.0/FpsWindowMs;
                if(!inputFailed)state.Text="공유 중 — generation "+generation+" / frame "+lastSequence+" · "+fps.ToString("0.0")+" fps · "+(!InputAllowed?"화면만 보기":Stateful?"미디어 입력(누름/뗌 상태 전송)":"미디어 입력(개별 키/문자 전송)");
            } catch(OperationCanceledException) { }
              catch(MediaHttpException ex) when(ex.StatusCode==409&&ex.EndReason!=null) { FinishRemoteEnd(ex.EndReason); }
              catch(MediaHttpException ex) when(ex.StatusCode==409&&!firstFrameReceived)
              {
                  frameValid=false;
                  if(preparingWatch.ElapsedMilliseconds<PreparingGraceMs){state.Text="상대가 화면 공유를 준비하는 중입니다…";}
                  else{DiagPollFailure(ex,true);pollTimer.Stop();state.Text="화면 공유가 시작되지 않았습니다 (승인/공유 필요): "+ex.Message;}
              }
              catch(Exception ex){DiagPollFailure(ex,false);ReleaseAllLocal();frameValid=false;state.Text="프레임 오류: "+ex.Message;
                  if(!networkReported&&!stop.IsCancellationRequested&&wire.Secure!=null&&MainForm.IsRecoverableNetworkFailure(wire.Secure.Failure)){networkReported=true;pollTimer.Stop();var notify=NetworkInterrupted;if(notify!=null)notify();}
              }
            finally { polling=false; }
        }
        // clamp=true (stateful release/drag while captured): coordinates outside the image are clamped onto its edge.
        private bool SendPointer(string kind,Point p,int button,bool clamp)
        {
            Rectangle r=ImageRect(); if(r.Width<=0||r.Height<=0)return false;
            if(kind=="move") {
                long now=queueClock.ElapsedMilliseconds; if(now-lastMoveTick<(Stateful?StatefulMoveThrottleMs:LegacyMoveThrottleMs))return false;
                if(!clamp&&!r.Contains(p))return false; lastMoveTick=now;
            } else if(!clamp&&!r.Contains(p))return false;
            if(clamp)p=new Point(Math.Max(r.Left,Math.Min(r.Right-1,p.X)),Math.Max(r.Top,Math.Min(r.Bottom-1,p.Y)));
            return Enqueue(new InputCommand{Kind=kind,X=ScaleX(p,r),Y=ScaleY(p,r),Button=button});
        }
        private static int ScaleX(Point p,Rectangle r) { return (int)((long)(p.X-r.Left)*65535/Math.Max(1,r.Width-1)); }
        private static int ScaleY(Point p,Rectangle r) { return (int)((long)(p.Y-r.Top)*65535/Math.Max(1,r.Height-1)); }
        private Task SendKey(int key) { Enqueue(new InputCommand{Kind="key",KeyCode=key}); return Task.CompletedTask; }
        private Task SendText(char ch) { Enqueue(new InputCommand{Kind="text",Text=ch.ToString()}); return Task.CompletedTask; }
        // UI thread only. Sequence is assigned at enqueue. Returns false when nothing was queued.
        private bool Enqueue(InputCommand c)
        {
            if(!frameValid||inputFailed||stop.IsCancellationRequested||IsDisposed)return false;
            if(MediaWire.IsStatefulKind(c.Kind)&&!Stateful)return false; // old target: never an accidental down
            // The head is in flight while pumping, so it is never mutated.
            if(c.Kind=="move"&&pending.Count>(pumping?1:0)&&pending[pending.Count-1].Command.Kind=="move") {
                var last=pending[pending.Count-1].Command; last.X=c.X; last.Y=c.Y; pending[pending.Count-1].EnqueuedMs=queueClock.ElapsedMilliseconds; return true; // consecutive moves only
            }
            if(pending.Count>=MaxPendingSends) { FailInput("입력 대기열 초과("+MaxPendingSends+")",DiagViewerInputFailure.QueueOverflow,null); return false; }
            c.Generation=generation; c.Sequence=++sequence;
            pending.Add(new PendingInput{Command=c,EnqueuedMs=queueClock.ElapsedMilliseconds});
            if(!pumping)Pump();
            return true;
        }
        private async void Pump()
        {
            pumping=true;
            try {
                while(pending.Count>0&&!inputFailed) {
                    var next=pending[0];
                    long now=queueClock.ElapsedMilliseconds;
                    while(sentInputTicks.Count>0&&now-sentInputTicks.Peek()>=1000)sentInputTicks.Dequeue();
                    if(next.Command.Kind=="move"&&sentInputTicks.Count>=25) {
                        // A queued edge carries its own coordinates; never hold that edge behind budgeted motion.
                        if(pending.Count>1) { pending.RemoveAt(0); continue; }
                        await Task.Delay(25);
                        continue;
                    }
                    if(queueClock.ElapsedMilliseconds-next.EnqueuedMs>MaxQueueAgeMs) { FailInput("입력 지연 750ms 초과",DiagViewerInputFailure.QueueAge,null); return; }
                    if(!frameValid||stop.IsCancellationRequested||IsDisposed||next.Command.Generation!=generation) {
                        // Not silently dropped: stateful holds are cleared locally; the target releases on its own TTL.
                        pending.Clear(); heldKeys.Clear(); heldButtons.Clear(); return;
                    }
                    if(MediaWire.IsStatefulKind(next.Command.Kind)&&!Stateful) { FailInput("상대가 상태 입력을 더 이상 지원하지 않습니다",DiagViewerInputFailure.StatefulUnsupported,null); return; }
                    try { sending=true; sentInputTicks.Enqueue(queueClock.ElapsedMilliseconds); await wire.SendInputAsync(sessionId,next.Command,stop.Token); }
                    catch(OperationCanceledException) { pending.Clear(); return; }
                    catch(ObjectDisposedException) { pending.Clear(); return; }
                    catch(Exception ex) { if(remoteEnded||await TryObserveRemoteEnd(ex))return; FailInput("입력 전송 실패: "+ex.Message,DiagViewerInputFailure.SendFailed,ex); return; }
                    finally { sending=false; }
                    if(pending.Count>0&&pending[0]==next)pending.RemoveAt(0);
                }
            } finally { pumping=false; }
        }
        private async Task<bool> TryObserveRemoteEnd(Exception failure)
        {
            var invalid=failure as InvalidOperationException;
            if(invalid==null||ControlPlaneClient.HttpStatus(invalid)!=409)return false;
            // An input POST 409 alone may be a stale generation, not normal termination.
            // Confirm against a fresh authenticated frame response; never soften TLS/authorization errors.
            try { await wire.GetFrameAsync(sessionId,stop.Token); }
            catch(MediaHttpException end) when(end.StatusCode==409&&end.EndReason!=null) { FinishRemoteEnd(end.EndReason); return true; }
            catch { }
            return remoteEnded;
        }
        private void FinishRemoteEnd(string reason)
        {
            if(remoteEnded)return;
            bool priorInputFailure=inputFailed;
            remoteEnded=true; EndReason=priorInputFailure?"error-stopped":reason; frameValid=false;
            DiagnosticLog.Event(DiagEvent.ViewerClose,diagCorrelation,DiagValue.Of(DiagKey.Close,DiagViewerClose.RemoteEnd),DiagValue.Of(DiagKey.EndReason,DiagnosticLog.NormalizeEndReason(reason)),
                DiagValue.Of(DiagKey.HadError,priorInputFailure),DiagValue.Of(DiagKey.Count,(long)FrameCount));
            pending.Clear(); heldKeys.Clear(); heldButtons.Clear();
            if(picture.Capture)picture.Capture=false;
            pollTimer.Stop(); heartbeatTimer.Stop(); stop.Cancel();
            if(priorInputFailure)return; // a later revoke acknowledgment must not hide our earlier real input failure
            state.Text=reason=="user-stopped"?"상대 PC에서 화면 공유를 정상 종료했습니다.":
                reason=="session-ended"?"원격 연결이 종료되었습니다.":
                reason=="expired"?"원격 연결이 만료되어 종료되었습니다.":
                "상대 PC의 화면 공유가 오류로 중단되었습니다.";
        }
        // Fail closed: clear queue and local holds, stop input, and revoke the session (target releases on lease loss).
        private void FailInput(string reason,DiagViewerInputFailure kind,Exception error)
        {
            if(inputFailed)return; inputFailed=true; frameValid=false;
            if(error==null)DiagnosticLog.Event(DiagEvent.ViewerInputFailure,diagCorrelation,DiagValue.Of(DiagKey.Reason,kind),DiagValue.Of(DiagKey.Count,(long)pending.Count));
            else DiagnosticLog.Failure(DiagEvent.ViewerInputFailure,diagCorrelation,error,DiagValue.Of(DiagKey.Reason,kind),DiagValue.Of(DiagKey.Count,(long)pending.Count));
            pending.Clear(); heldKeys.Clear(); heldButtons.Clear();
            try { if(picture.Capture)picture.Capture=false; } catch { }
            if(!IsDisposed)state.Text=reason+" — 입력을 중지하고 세션을 종료합니다.";
            var ignored=CloseSessionAsync();
        }
        private void ApplyViewerZoom()
        {
            if(zoomChoice.SelectedIndex<=0){imageViewport.AutoScroll=false;imageViewport.AutoScrollMinSize=Size.Empty;picture.Dock=DockStyle.Fill;picture.SizeMode=PictureBoxSizeMode.Zoom;return;}
            picture.Dock=DockStyle.None;picture.SizeMode=PictureBoxSizeMode.Zoom;imageViewport.AutoScroll=true;
            if(picture.Image==null)return;
            double scale=zoomChoice.SelectedIndex==1?1:zoomChoice.SelectedIndex==2?1.25:zoomChoice.SelectedIndex==3?1.5:2;
            var size=new Size((int)(picture.Image.Width*scale),(int)(picture.Image.Height*scale));
            if(picture.Size!=size){picture.Size=size;imageViewport.AutoScrollMinSize=size;}
        }
        private Rectangle ImageRect()
        {
            if(picture.Image==null)return Rectangle.Empty;
            float ratio=Math.Min((float)picture.ClientSize.Width/picture.Image.Width,(float)picture.ClientSize.Height/picture.Image.Height);
            int w=(int)(picture.Image.Width*ratio),h=(int)(picture.Image.Height*ratio);
            return new Rectangle((picture.ClientSize.Width-w)/2,(picture.ClientSize.Height-h)/2,w,h);
        }
    }
}
