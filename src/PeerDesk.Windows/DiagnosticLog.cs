using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PeerDesk.Windows
{
    // Local, privacy-safe diagnostic log. Only typed enum keys with numeric or enum-name values are ever written:
    // no message/ToString/stack text, no addresses, credentials, session/device identifiers, file names, clipboard,
    // text, input contents or coordinates. Callers never block: entries go to a bounded queue drained by a single
    // background writer into three ring files (max 1 MiB each, shared by all instances and Windows sessions of the same
    // user via an exclusive zero-byte lock file in the log directory).
    internal enum DiagEvent
    {
        ProcessStart, ProcessShutdown, LogDrops,
        SharingStart, SharingStartFailure, SharingWorkerFirstError, SharingWorkerClassFirst, SharingWorkerEnd, SharingFatal, SharingNormalEnd,
        SharingCancel, SharingStop, SharingCleanup, SharingLegacyLoopEnd,
        CaptureMetrics, CaptureFailure, CaptureProofExpired, InputBudgetTimeout,
        AdapterFail, DirectNegotiation, DirectEstablished, DirectFallback, DirectTerminal,
        ViewerStart, ViewerFirstFrame, ViewerPollFailure, ViewerInputFailure, ViewerClose,
        MainCleanup, MainRefinement, SecurePreparationFailure,
        InputWorkerSite, InputGuardFailure, InputNativeFailure,
        ControlRequestFailure,
        InputCapability, InputBatchSample, InputFailureNotice
    }

    internal enum DiagKey
    {
        Generation, Worker, Outcome, Stage, Reason, EndReason, Source, Origin, Phase, Count, Attempt,
        ElapsedMs, BudgetMs, RequestBudgetMs, ProofRemainingMs, PendingBudgetMs, ProofAgeMs,
        CaptureMs, EncodeMs, ResponseMs, FrameValid, FrameReset, Bytes, Width, Height, Quality, IntervalMs,
        ExType, ExHResult, ExHttpStatus, ExWebStatus, ExSocketError, ExInner, ExEndReason,
        Direct, Relay, Close, Refinement, Dropped, MetricDrops, PriorityDrops, ContentionDrops, IoFailures, Value,
        UploadMs, AlreadyCanceled, Stateful, Optimized, CaptureLeased, Secure, Negotiated, BindInputs, Expiries,
        ExplicitUserStop, HadError, Site, Fallback, Read, Write, LastResponseMs, StartProofAgeMs, EndProofAgeMs,
        Initiator, Detail, Ready, Visible, Polls, Failures, Confirmed, Running,
        Cause, PollAgeMs, LeaseMs, Context, ScopeCurrent, GenerationMatch, Index, HeldCount, HeldKeys, HeldButtons,
        HeartbeatAgeMs, ServerLeaseAgeMs, ScopeResult, FirstFailure, Disposed, BodyCause, NativeFailure,
        Operation, Requested, Sent, Win32Error, ErrorCaptured, ErrorMayBeStale, AuthoritativeCause,
        CleanupAttempted, CleanupRequested, CleanupSent, CleanupWin32Error,
        BatchCount, GateWaitMs, RttMs, AgeMs, Acked, Capabilities, Delivered
    }

    internal enum DiagControlStage { Connecting, SendingBody, WaitingResponse, ReadingResponse }
    internal enum DiagControlOperation { Other, Health, Directory, Owner, Registration, Device, Session }
    internal enum DiagConnectionCause { Deadline, CallerCanceled, Transport }

    internal enum DiagWorker { Unknown, Frame, Input, LegacyLoop }
    internal enum DiagWorkerOutcome { Completed, Canceled, DisposedAfterLocalCancel, Candidate, TypedNormal, Fatal, Error }
    internal enum DiagSettle { IndependentFatal, CandidateProbeNormal, CandidateProbeUnresolved, CandidateNoProbe, TypedNormal, Untyped }
    internal enum DiagStartStage { Capabilities, Validate, FrozenProbe, Guard, Runner }
    internal enum DiagCleanupStage { WorkersDone, StopSetSharing }
    internal enum DiagFramePhase { None, LeaseRequest, ProofBeforeCapture, Capture, ProofAfterCapture, Encode, ProofAfterEncode, Upload, Done }
    // Distinct pure-expiry sites in the capture-v1 frame worker.
    internal enum DiagExpiryStage { AfterLease, AfterCapture, AfterEncode, PreSealSend }
    internal enum DiagInputTimeoutSite { BudgetExhausted, PollCanceled }
    // Fixed input-worker failure sites (secure and legacy stateful workers); one value per throw/fatal site.
    internal enum DiagInputCause { AdapterUnavailable, GuardPreFailed, ContextBeforePoll, GenerationAfterPoll, GuardFailedAfterPoll, ContextAfterPoll, ScopeAfterPoll,
        ReplayBatch, ScopeBeforeApply, StaleBatch, EventGeneration, EventScope, LeaseMidBatch, ApplyFailed, BindFailed }
    // Which InputAuthorityCurrent clause failed, in its original short-circuit order.
    internal enum DiagInputContext { NotEvaluated, Current, Canceled, NotRunning, NotLeased, Generation, CredentialRevision, CredentialOdd, Adapter, Scope, Binding, Epoch, BindingThrew }
    internal enum DiagTri { NotEvaluated, False, True, Threw }
    internal enum DiagGuardCause { RunActionFailure, RejectedPreFailed, RejectedDisposed, QueuedPreFailed, NativeFailure, BodyFailure, QueueClosed,
        WatchdogHeartbeat, WatchdogServerLease, WatchdogScopeFalse, WatchdogScopeThrew, FailAsync, FailAsyncQueueClosed, Dispose }
    internal enum DiagBodyCause { None, CanceledBeforeNative, ContextBeforeNative, Other }
    internal enum DiagNativeOp { KeyDown, KeyUp, ButtonDown, ButtonUp, ButtonUpInPlace, Wheel, Move, Click, KeyTap, TextTap }
    internal enum DiagAdapterOrigin { Unknown, Receiver, ReceiverEnded, Handshake, Packet, Send, SendFrame, BindInputs, PendingInputs, DirectNegotiate, DirectRead }
    internal enum DiagDirectOutcome { Started, LocalDisabled, ListenerUnavailable, PeerDisabled, AcceptTimeout, ConnectFailed, Established, ReadInvalidData, ReadTransport, ReadOther, WriteTransport, WriteTerminal, NegotiationFailed }
    internal enum DiagViewerClose { User, RemoteEnd, Reconnect, Error, Disposed }
    internal enum DiagViewerInputFailure { QueueOverflow, QueueAge, StatefulUnsupported, SendFailed }
    internal enum DiagInputNotice { Sent, Received, AckReceived, NotNegotiated, GateBusy, SendFailed, SentAckUnavailable }
    internal enum DiagCleanupReason { Unknown, UserStop, RemoteEnd, Error, Expired, Reconnect, Shutdown, Superseded }
    internal enum DiagCleanupSite { StoppedMonitor, TerminalDrop, StopShare, StartFailure, LateStart }
    internal enum DiagRefinement { NotAuthoritative, StaleContext, Superseded, Replaced }
    internal enum DiagPreparation { Transport, Authenticate, Prepare, Attach, Direct, Start, Unknown }

    internal enum DiagExceptionKind
    {
        None, OperationCanceled, Timeout, CaptureProofExpired, MediaHttp, OpaquePacketHttp, SessionRequest,
        PasswordConflict, Web, HttpRequest, Socket, EndOfStream, IO, InvalidData, Cryptographic, Authentication,
        ObjectDisposed, InvalidOperation, Argument, UnauthorizedAccess, Format, Win32, NotSupported, OutOfMemory,
        Aggregate, Other
    }

    internal enum DiagEndReason { None, UserStopped, SessionEnded, ErrorStopped, Expired, Unknown }

    internal struct DiagValue
    {
        internal readonly DiagKey Key; internal readonly long Number; internal readonly Enum Code;
        private DiagValue(DiagKey key,long number,Enum code) { Key=key; Number=number; Code=code; }
        internal static DiagValue Of(DiagKey key,long value) { return new DiagValue(key,value,null); }
        internal static DiagValue Of(DiagKey key,bool value) { return new DiagValue(key,value?1:0,null); }
        internal static DiagValue Of(DiagKey key,Enum value) { return new DiagValue(key,0,value); }
    }

    internal static class DiagnosticLog
    {
        internal const long MaxFileBytes=1024*1024; internal const int FileCount=3, MaxFields=32;
        internal const string LockFileName="remotebridge-diag.lock";
        private const int PriorityCap=1024, MetricCap=256, MaxBatchEntries=256, MaxCarry=64, MaxCarryAttempts=3, MaxIoFailures=8;
        private const string FilePrefix="remotebridge-diag-";
        // Static state holds no OS resources: nothing here can fail type initialization and disable every hook.
        // The signal and writer thread are created inside Initialize's try; any failure leaves logging a disabled no-op.
        private static readonly ConcurrentQueue<Entry> queue=new ConcurrentQueue<Entry>();
        private static AutoResetEvent signal;
        private static string pid="0", mvid="00000000-0000-0000-0000-000000000000";
        private static int claimed, enabled, queued, stopping, ioFailures;
        private static long metricDrops, priorityDrops, contentionDrops, sequence, fallbackIds;
        private static string directory, runId; private static long maxFileBytes=MaxFileBytes;
        private static Thread writer;

        private sealed class Entry { internal DiagEvent Event; internal long Correlation; internal DateTime Utc; internal DiagValue[] Fields; internal bool Priority; internal long Sequence; }

        internal static bool Enabled { get { return Volatile.Read(ref enabled)==1&&Volatile.Read(ref stopping)==0; } }
        internal static long MetricDrops { get { return Interlocked.Read(ref metricDrops); } }
        internal static long PriorityDrops { get { return Interlocked.Read(ref priorityDrops); } }
        internal static long ContentionDrops { get { return Interlocked.Read(ref contentionDrops); } }
        internal static long DroppedCount { get { return MetricDrops+PriorityDrops+ContentionDrops; } }
        internal static int IoFailures { get { return Volatile.Read(ref ioFailures); } }
        internal static string Directory { get { return directory??DefaultDirectory(); } }

        internal static string DefaultDirectory()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PeerDesk","logs"); } catch { return null; }
        }

        internal static void Initialize() { Initialize(DefaultDirectory(),MaxFileBytes); }

        // Test entry point: isolated directory and smaller ring size. Production uses Initialize(). Once per process;
        // all setup completes before logging is published as enabled, and any failure leaves it a disabled no-op.
        internal static void Initialize(string logDirectory,long fileBytes)
        {
            try
            {
                if(String.IsNullOrEmpty(logDirectory)||Interlocked.CompareExchange(ref claimed,1,0)!=0)return;
                pid=SafePid(); mvid=SafeMvid();
                directory=logDirectory; maxFileBytes=Math.Max(4096,Math.Min(MaxFileBytes,fileBytes));
                runId=NewId().ToString("x16",CultureInfo.InvariantCulture);
                signal=new AutoResetEvent(false);
                var t=new Thread(WriterLoop){IsBackground=true,Name="RemoteBridge diagnostic log",Priority=ThreadPriority.BelowNormal};
                t.Start(); writer=t;
                Volatile.Write(ref enabled,1);
            }
            catch { Interlocked.Exchange(ref stopping,1); return; }
            Event(DiagEvent.ProcessStart,0);
        }

        // Bounded flush for ordinary exit only; never waits longer than timeoutMs and never throws.
        internal static void Shutdown(int timeoutMs)
        {
            try
            {
                if(Volatile.Read(ref enabled)!=1)return;
                Event(DiagEvent.ProcessShutdown,0);
                if(Interlocked.Exchange(ref stopping,1)!=0)return;
                signal.Set(); Thread t=writer; if(t!=null)t.Join(Math.Max(0,timeoutMs));
            }
            catch { }
        }

        // Random local correlation id shared between an adapter and its sharing/viewer; never derived from session/device ids.
        // Works without Initialize and without any OS resource.
        internal static long NewCorrelation() { long v=NewId(); return v==0?1:v; }

        internal static void Event(DiagEvent ev,long correlation,params DiagValue[] fields) { Enqueue(ev,correlation,fields,null,true); }
        internal static void Metric(DiagEvent ev,long correlation,params DiagValue[] fields) { Enqueue(ev,correlation,fields,null,false); }
        // Site fields come first; exception facts follow and never replace a site value with the same key (first wins).
        internal static void Failure(DiagEvent ev,long correlation,Exception ex,params DiagValue[] fields)
        {
            try { if(Enabled)Enqueue(ev,correlation,fields,ExceptionFields(ex),true); } catch { }
        }

        private static void Enqueue(DiagEvent ev,long correlation,DiagValue[] fields,DiagValue[] extra,bool priority)
        {
            try
            {
                if(!Enabled)return;
                int n=Interlocked.Increment(ref queued);
                if(n>(priority?PriorityCap:MetricCap)) { Interlocked.Decrement(ref queued); if(priority)Interlocked.Increment(ref priorityDrops); else Interlocked.Increment(ref metricDrops); return; }
                DiagValue[] copy;
                try { copy=BoundedCopy(fields,extra); }
                catch { Interlocked.Decrement(ref queued); throw; }
                queue.Enqueue(new Entry{Event=ev,Correlation=correlation,Utc=DateTime.UtcNow,Fields=copy,Priority=priority,Sequence=Interlocked.Increment(ref sequence)});
                signal.Set();
            }
            catch { }
        }

        // Private clone capped at MaxFields (cap applied before allocation) so callers cannot grow or mutate retained fields.
        private static DiagValue[] BoundedCopy(DiagValue[] fields,DiagValue[] extra)
        {
            int a=fields==null?0:Math.Min(MaxFields,fields.Length);
            int b=extra==null?0:Math.Min(MaxFields-a,extra.Length);
            if(a+b==0)return null;
            var copy=new DiagValue[a+b];
            if(a>0)Array.Copy(fields,0,copy,0,a);
            if(b>0)Array.Copy(extra,0,copy,a,b);
            return copy;
        }

        internal static DiagEndReason NormalizeEndReason(string reason)
        {
            if(reason==null)return DiagEndReason.None;
            switch(reason)
            {
                case "user-stopped": return DiagEndReason.UserStopped;
                case "session-ended": return DiagEndReason.SessionEnded;
                case "error-stopped": return DiagEndReason.ErrorStopped;
                case "expired": return DiagEndReason.Expired;
                default: return DiagEndReason.Unknown;
            }
        }

        internal static DiagExceptionKind Classify(Exception ex)
        {
            if(ex==null)return DiagExceptionKind.None;
            if(ex is CaptureProofExpiredException)return DiagExceptionKind.CaptureProofExpired;
            if(ex is MediaHttpException)return DiagExceptionKind.MediaHttp;
            if(ex is OpaquePacketHttpException)return DiagExceptionKind.OpaquePacketHttp;
            if(ex is SessionRequestException)return DiagExceptionKind.SessionRequest;
            if(ex is PasswordGenerationConflictException)return DiagExceptionKind.PasswordConflict;
            if(ex is OperationCanceledException)return DiagExceptionKind.OperationCanceled;
            if(ex is TimeoutException)return DiagExceptionKind.Timeout;
            if(ex is WebException)return DiagExceptionKind.Web;
            if(ex is SocketException)return DiagExceptionKind.Socket;
            if(ex is System.ComponentModel.Win32Exception)return DiagExceptionKind.Win32;
            if(ex is EndOfStreamException)return DiagExceptionKind.EndOfStream;
            if(ex is InvalidDataException)return DiagExceptionKind.InvalidData;
            if(ex is IOException)return DiagExceptionKind.IO;
            if(ex is CryptographicException)return DiagExceptionKind.Cryptographic;
            if(ex is System.Security.Authentication.AuthenticationException)return DiagExceptionKind.Authentication;
            if(ex is ObjectDisposedException)return DiagExceptionKind.ObjectDisposed;
            if(ex is UnauthorizedAccessException)return DiagExceptionKind.UnauthorizedAccess;
            if(ex is NotSupportedException)return DiagExceptionKind.NotSupported;
            if(ex is OutOfMemoryException)return DiagExceptionKind.OutOfMemory;
            if(ex is AggregateException)return DiagExceptionKind.Aggregate;
            if(ex is FormatException)return DiagExceptionKind.Format;
            if(ex is ArgumentException)return DiagExceptionKind.Argument;
            if(ex is InvalidOperationException)return ex.GetType().FullName=="System.Net.Http.HttpRequestException"?DiagExceptionKind.HttpRequest:DiagExceptionKind.InvalidOperation;
            return ex.GetType().FullName=="System.Net.Http.HttpRequestException"?DiagExceptionKind.HttpRequest:DiagExceptionKind.Other;
        }

        // Numeric/enum facts only; Message, ToString and stack traces are never read.
        internal static DiagValue[] ExceptionFields(Exception ex)
        {
            List<DiagValue> list=null;
            try
            {
                list=new List<DiagValue>(7);
                var agg=ex as AggregateException; if(agg!=null&&agg.InnerExceptions.Count==1)ex=agg.InnerExceptions[0];
                DiagExceptionKind kind=Classify(ex); list.Add(DiagValue.Of(DiagKey.ExType,kind));
                if(ex==null)return list.ToArray();
                list.Add(DiagValue.Of(DiagKey.ExHResult,(long)ex.HResult));
                int status=HttpStatus(ex); if(status!=0)list.Add(DiagValue.Of(DiagKey.ExHttpStatus,(long)status));
                WebException web=FindWeb(ex); if(web!=null)list.Add(DiagValue.Of(DiagKey.ExWebStatus,web.Status));
                var socket=ex as SocketException??ex.InnerException as SocketException; if(socket!=null)list.Add(DiagValue.Of(DiagKey.ExSocketError,socket.SocketErrorCode));
                var media=ex as MediaHttpException; if(media!=null)list.Add(DiagValue.Of(DiagKey.ExEndReason,NormalizeEndReason(media.EndReason)));
                if(ex.InnerException!=null)list.Add(DiagValue.Of(DiagKey.ExInner,Classify(ex.InnerException)));
            }
            catch { }
            try { return list==null?new DiagValue[0]:list.ToArray(); } catch { return null; }
        }

        private static WebException FindWeb(Exception ex)
        {
            for(int i=0;ex!=null&&i<4;i++,ex=ex.InnerException) { var w=ex as WebException; if(w!=null)return w; }
            return null;
        }

        private static int HttpStatus(Exception ex)
        {
            var media=ex as MediaHttpException; if(media!=null)return media.StatusCode;
            var opaque=ex as OpaquePacketHttpException; if(opaque!=null)return opaque.StatusCode;
            var request=ex as SessionRequestException; if(request!=null)return request.StatusCode;
            WebException web=FindWeb(ex);
            if(web!=null)try { var r=web.Response as HttpWebResponse; if(r!=null)return (int)r.StatusCode; } catch { }
            return 0;
        }

        // Guid v4 randomness; the fallback is a plain process-local counter (never an identifier or address).
        private static long NewId()
        {
            try { return BitConverter.ToInt64(Guid.NewGuid().ToByteArray(),0); }
            catch { return Interlocked.Increment(ref fallbackIds)|unchecked((long)0x4000000000000000L); }
        }
        private static string SafePid() { try { return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); } catch { return "0"; } }
        private static string SafeMvid() { try { return typeof(DiagnosticLog).Module.ModuleVersionId.ToString("D"); } catch { return "00000000-0000-0000-0000-000000000000"; } }

        private static void WriterLoop()
        {
            var carry=new List<string>(); int carryAttempts=0; long reportedDrops=0;
            try { if(directory!=null)System.IO.Directory.CreateDirectory(directory); } catch { Interlocked.Increment(ref ioFailures); }
            while(true)
            {
                bool exiting=Volatile.Read(ref stopping)==1;
                if(!exiting)try { signal.WaitOne(1000); } catch { }
                exiting=Volatile.Read(ref stopping)==1;
                try
                {
                    var lines=new List<string>(); var priority=new List<bool>(); Entry e; int taken=0;
                    while(taken<MaxBatchEntries&&queue.TryDequeue(out e)) { Interlocked.Decrement(ref queued); taken++; lines.Add(Format(e)); priority.Add(e.Priority); }
                    long drops=DroppedCount;
                    if(drops!=reportedDrops) { reportedDrops=drops; lines.Add(Format(DropsEntry())); priority.Add(true); }
                    if(lines.Count>0||carry.Count>0)WriteBatch(lines,priority,carry,ref carryAttempts);
                    if(exiting&&queue.IsEmpty)break;
                }
                catch { Interlocked.Increment(ref ioFailures); if(exiting)break; }
            }
            // Lines still carried at exit (lock held elsewhere) are dropped, never waited for.
            Interlocked.Add(ref contentionDrops,carry.Count);
        }

        private static Entry DropsEntry()
        {
            return new Entry{Event=DiagEvent.LogDrops,Utc=DateTime.UtcNow,Priority=true,Sequence=Interlocked.Increment(ref sequence),Fields=new[]{
                DiagValue.Of(DiagKey.MetricDrops,MetricDrops),DiagValue.Of(DiagKey.PriorityDrops,PriorityDrops),
                DiagValue.Of(DiagKey.ContentionDrops,ContentionDrops),DiagValue.Of(DiagKey.IoFailures,(long)IoFailures)}};
        }

        // Cross-session/cross-process authority: a fixed zero-byte lock file in the log directory, opened exclusively by
        // the writer thread only. Contention is immediate (no wait); the lock file is never written to.
        private static FileStream TryLock(out bool contended)
        {
            contended=false;
            try { return new FileStream(Path.Combine(directory,LockFileName),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.None); }
            catch(IOException ex) { int code=ex.HResult&0xFFFF; contended=code==32||code==33; if(!contended)Interlocked.Increment(ref ioFailures); return null; }
            catch { Interlocked.Increment(ref ioFailures); return null; }
        }

        private static void WriteBatch(List<string> lines,List<bool> priority,List<string> carry,ref int carryAttempts)
        {
            if(Volatile.Read(ref ioFailures)>=MaxIoFailures||directory==null) { Interlocked.Add(ref contentionDrops,lines.Count+carry.Count); carry.Clear(); return; }
            bool contended; FileStream held=TryLock(out contended);
            if(held==null)
            {
                // Contention: keep a bounded number of priority lines (first failures) for a few attempts, drop metrics.
                for(int i=0;i<lines.Count;i++) { if(priority[i]&&carry.Count<MaxCarry)carry.Add(lines[i]); else Interlocked.Increment(ref contentionDrops); }
                if(++carryAttempts>MaxCarryAttempts) { Interlocked.Add(ref contentionDrops,carry.Count); carry.Clear(); carryAttempts=0; }
                return;
            }
            try
            {
                var all=new List<string>(carry.Count+lines.Count); all.AddRange(carry); all.AddRange(lines); carry.Clear(); carryAttempts=0;
                AppendLines(all);
                Interlocked.Exchange(ref ioFailures,0);
            }
            catch { Interlocked.Increment(ref ioFailures); Interlocked.Add(ref contentionDrops,lines.Count); }
            finally { try { held.Dispose(); } catch { } }
        }

        private static void AppendLines(List<string> lines)
        {
            var encoding=new UTF8Encoding(false); int index=CurrentIndex();
            string path=FilePath(index); long length=0; try { var fi=new FileInfo(path); if(fi.Exists)length=fi.Length; } catch { }
            var buffer=new MemoryStream();
            foreach(string line in lines)
            {
                byte[] bytes=encoding.GetBytes(line+"\n"); if(bytes.Length>maxFileBytes)continue;
                if(length+buffer.Length+bytes.Length>maxFileBytes)
                {
                    Flush(path,buffer,false); length=length+buffer.Length; buffer.SetLength(0);
                    index=(index+1)%FileCount; path=FilePath(index); length=0; Flush(path,buffer,true);
                }
                buffer.Write(bytes,0,bytes.Length);
            }
            Flush(path,buffer,false);
        }

        private static void Flush(string path,MemoryStream buffer,bool truncate)
        {
            if(buffer.Length==0&&!truncate)return;
            using(var fs=new FileStream(path,truncate?FileMode.Create:FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))
                buffer.WriteTo(fs);
        }

        private static string FilePath(int index) { return Path.Combine(directory,FilePrefix+index.ToString(CultureInfo.InvariantCulture)+".jsonl"); }

        private static int CurrentIndex()
        {
            int best=0; DateTime newest=DateTime.MinValue;
            for(int i=0;i<FileCount;i++) { try { var fi=new FileInfo(FilePath(i)); if(fi.Exists&&fi.LastWriteTimeUtc>newest) { newest=fi.LastWriteTimeUtc; best=i; } } catch { } }
            return best;
        }

        private static string Format(Entry e)
        {
            var sb=new StringBuilder(256);
            sb.Append("{\"v\":1,\"t\":\"").Append(e.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture));
            sb.Append("\",\"run\":\"").Append(runId).Append("\",\"pid\":").Append(pid).Append(",\"mvid\":\"").Append(mvid);
            sb.Append("\",\"seq\":").Append(e.Sequence.ToString(CultureInfo.InvariantCulture)).Append(",\"ev\":\"").Append(Identifier(e.Event.ToString())).Append('"');
            if(e.Correlation!=0)sb.Append(",\"cor\":\"").Append(e.Correlation.ToString("x16",CultureInfo.InvariantCulture)).Append('"');
            // Each key at most once, first occurrence wins (site values before exception facts); undefined keys are skipped.
            var seen=new HashSet<DiagKey>();
            if(e.Fields!=null)foreach(DiagValue f in e.Fields)
            {
                if(!Enum.IsDefined(typeof(DiagKey),f.Key)||!seen.Add(f.Key))continue;
                sb.Append(",\"").Append(Identifier(f.Key.ToString())).Append("\":");
                if(f.Code==null)sb.Append(f.Number.ToString(CultureInfo.InvariantCulture));
                else sb.Append('"').Append(Identifier(f.Code.ToString())).Append('"');
            }
            return sb.Append('}').ToString();
        }

        // Enum names are code constants; still restrict to identifier characters so nothing else can reach the file.
        private static string Identifier(string name)
        {
            if(String.IsNullOrEmpty(name))return "_";
            var sb=new StringBuilder(Math.Min(name.Length,64));
            foreach(char c in name) { if(sb.Length>=64)break; sb.Append((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-'?c:'_'); }
            return sb.ToString();
        }
    }
}
