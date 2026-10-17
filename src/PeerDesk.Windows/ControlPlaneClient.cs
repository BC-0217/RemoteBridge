using System.Linq;
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PeerDesk.Windows
{
    internal sealed class ControlPlaneClient
    {
        private readonly ServerProfile profile;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 65536 };
        public string DeviceId { get; private set; }
        public string Token { get; private set; }
        private long credentialRevision;
        internal long CredentialRevision { get { return Interlocked.Read(ref credentialRevision); } }
        public string TemporaryPassword { get; private set; }
        public string PasswordExpiresAt { get; private set; }
        // Known only from a validated register/rotate response; restored credentials have no generation until rotated.
        public long PasswordGeneration { get; private set; }
        public bool HasPasswordGeneration { get; private set; }
        internal const string OwnerSecretRequiredMessage="상단 서버 관리키를 입력해 주세요. 참가 비밀번호와 다른 관리자 인증키입니다. 조회 후에는 다시 입력해야 합니다.";
        public string ServerUrl { get { return profile.RendezvousUrl; } }
        public string CertificateSha256 { get { return profile.CertificateSha256; } }
        public ControlPlaneClient(ServerProfile p) { profile = p; }

        internal void RestoreCredentials(string deviceId,string bearerToken,string temporaryPassword,string passwordExpiresAt)
        {
            if(String.IsNullOrWhiteSpace(deviceId)||String.IsNullOrWhiteSpace(bearerToken)||
               String.IsNullOrWhiteSpace(temporaryPassword)||String.IsNullOrWhiteSpace(passwordExpiresAt))
                throw new InvalidOperationException("저장된 등록 정보가 완전하지 않습니다.");
            Interlocked.Increment(ref credentialRevision);
            DeviceId=deviceId;Token=bearerToken;TemporaryPassword=temporaryPassword;PasswordExpiresAt=passwordExpiresAt;
            Interlocked.Increment(ref credentialRevision);
            PasswordGeneration=0;HasPasswordGeneration=false;
        }

        internal Task<DeviceIdentity> ValidateDeviceAsync() { return ValidateDeviceAsync(default(CancellationToken)); }
        internal Task<DeviceIdentity> ValidateDeviceAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                Dictionary<string,object> data;
                try { data=Deserialize<Dictionary<string,object>>(Send("GET","/api/v1/devices/me",null,true,10000,cancellationToken)); }
                catch(InvalidOperationException ex)
                {
                    if(HttpStatus(ex)==401)
                        throw new UnauthorizedAccessException("저장된 등록 인증이 거부되었습니다. 다시 등록해야 합니다.",ex);
                    throw;
                }
                if(data==null) throw new InvalidOperationException("장치 상태 응답 형식이 잘못되었습니다.");
                object required;
                if(!data.TryGetValue("rotationRequired",out required)||!(required is bool)) throw new InvalidOperationException("장치 상태 응답 필드가 잘못되었습니다: rotationRequired");
                string expires=RequireString(data,"passwordExpiresAt");
                return new DeviceIdentity {
                    DeviceId=RequireString(data,"deviceId"),PasswordExpiresAt=expires,PasswordExpiresAtUtc=RequireUtc(expires,"passwordExpiresAt"),
                    PasswordGeneration=RequireGeneration(data),RotationRequired=(bool)required,
                    ServerTimeUtc=RequireUtc(RequireString(data,"serverTimeUtc"),"serverTimeUtc") };
            });
        }
        internal static bool TryParseUtc(string value,out DateTimeOffset result)
        {
            return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out result);
        }
        private static DateTimeOffset RequireUtc(string value,string key)
        {
            DateTimeOffset parsed;
            if(!TryParseUtc(value,out parsed)) throw new InvalidOperationException("서버 응답 시간 형식이 잘못되었습니다: "+key);
            return parsed;
        }
        // JavaScriptSerializer yields int/long for integral JSON numbers; strings, decimals, doubles, bools and negatives are rejected.
        private static long RequireGeneration(Dictionary<string,object> x)
        {
            object value;long generation;
            if(!x.TryGetValue("passwordGeneration",out value)||value==null) throw new InvalidOperationException("서버 응답 필드가 잘못되었습니다: passwordGeneration");
            if(value is int) generation=(int)value;
            else if(value is long) generation=(long)value;
            else throw new InvalidOperationException("서버 응답 필드가 잘못되었습니다: passwordGeneration");
            if(generation<0) throw new InvalidOperationException("서버 응답 필드가 잘못되었습니다: passwordGeneration");
            return generation;
        }
        internal static int HttpStatus(InvalidOperationException ex)
        {
            var web=ex.InnerException as WebException;
            var response=web==null?null:web.Response as HttpWebResponse;
            return response==null?0:(int)response.StatusCode;
        }

        internal Task CheckHealthAsync()
        {
            return Task.Run(() =>
            {
                var data=Deserialize<Dictionary<string,object>>(Send("GET","/healthz",null,false));
                object value;
                if(data==null||!data.TryGetValue("status",out value)||!(value is string)||!String.Equals((string)value,"ok",StringComparison.Ordinal))
                    throw new InvalidOperationException("서버 상태 응답이 올바르지 않습니다.");
            });
        }

        public void Register(string invitation)
        {
            var response = Send("POST", "/api/v1/register", new { invitationCode = invitation }, false);
            var d = Deserialize<Dictionary<string, object>>(response);
            if(d==null) throw new InvalidOperationException("등록 응답 형식이 잘못되었습니다.");
            string id=RequireString(d,"deviceId"), token=RequireString(d,"bearerToken");
            string password=RequireString(d,"temporaryPassword"), expires=RequireString(d,"passwordExpiresAt");
            RequireUtc(expires,"passwordExpiresAt");
            long generation=RequireGeneration(d);
            if(!CredentialFormat.IsNewDeviceId(id)) throw new InvalidOperationException("서버가 반환한 장치 ID 형식이 잘못되었습니다.");
            if(!CredentialFormat.IsNewPassword(password)) throw new InvalidOperationException("서버가 반환한 임시 비밀번호 형식이 잘못되었습니다.");
            Interlocked.Increment(ref credentialRevision);
            DeviceId=id; Token=token; TemporaryPassword=password; PasswordExpiresAt=expires; PasswordGeneration=generation; HasPasswordGeneration=true;
            Interlocked.Increment(ref credentialRevision);
        }

        // Manual/startup force rotation: no expectedGeneration, server always rotates.
        public void RotatePassword() { RotateCore(new { },null,default(CancellationToken)); }
        // Conditional rotation: server answers 409 without mutation when its generation differs from expectedGeneration.
        internal void RotatePassword(long expectedGeneration,CancellationToken cancellationToken)
        {
            if(expectedGeneration<0) throw new ArgumentOutOfRangeException("expectedGeneration");
            RotateCore(new Dictionary<string,object> { {"expectedGeneration",expectedGeneration} },expectedGeneration,cancellationToken);
        }
        private void RotateCore(object body,long? expectedGeneration,CancellationToken cancellationToken)
        {
            if(String.IsNullOrWhiteSpace(Token)) throw new InvalidOperationException("먼저 장치를 등록하십시오.");
            string raw;
            try { raw=Send("POST","/api/v1/devices/password/rotate",body,true,10000,cancellationToken); }
            catch(InvalidOperationException ex)
            {
                int code=HttpStatus(ex);
                if(code==401) throw new UnauthorizedAccessException("등록 인증이 거부되었습니다. 다시 등록해야 합니다.",ex);
                if(code==409&&expectedGeneration.HasValue) throw new PasswordGenerationConflictException(ex);
                throw;
            }
            var d=Deserialize<Dictionary<string,object>>(raw);
            if(d==null) throw new InvalidOperationException("비밀번호 갱신 응답 형식이 잘못되었습니다.");
            string password=RequireString(d,"temporaryPassword");
            string expires=RequireString(d,"passwordExpiresAt");
            RequireUtc(expires,"passwordExpiresAt");
            long generation=RequireGeneration(d);
            if(!CredentialFormat.IsNewPassword(password)) throw new InvalidOperationException("서버가 반환한 임시 비밀번호 형식이 잘못되었습니다.");
            if((expectedGeneration.HasValue&&generation<=expectedGeneration.Value)||(HasPasswordGeneration&&generation<=PasswordGeneration))
                throw new InvalidOperationException("서버가 반환한 비밀번호 세대가 올바르지 않습니다.");
            // Commit only after the complete response validated.
            TemporaryPassword=password;
            PasswordExpiresAt=expires;
            PasswordGeneration=generation;
            HasPasswordGeneration=true;
        }
        public List<SessionItem> Pending()
        {
            return ParseSessionList(Send("GET", "/api/v1/sessions/pending", null, true));
        }
        // Authenticated snapshot of every session this device participates in (all states). Missing rows are not terminal.
        internal async Task<List<SessionItem>> SnapshotAsync(CancellationToken ct)
        {
            return ParseSessionList(await Task.Run(()=>Send("GET","/api/v1/sessions/snapshot",null,true,10000,ct,null,262144),ct).ConfigureAwait(false),true);
        }
        internal async Task<SessionItem> DecideAsync(string id,bool approve,CancellationToken ct) { return ParseSession(await SendJsonAsync("POST","/api/v1/sessions/decision",new { sessionId=id, approve=approve },ct).ConfigureAwait(false)); }
        internal async Task<SessionItem> ConnectAsync(string id,CancellationToken ct) { return ParseSession(await SendJsonAsync("POST","/api/v1/sessions/connect",new { sessionId=id },ct).ConfigureAwait(false)); }
        private List<SessionItem> ParseSessionList(string body, bool snapshot=false)
        {
            var d = snapshot ? new JavaScriptSerializer { MaxJsonLength=262144 }.Deserialize<Dictionary<string,object>>(body) : Deserialize<Dictionary<string, object>>(body);
            object raw;
            if (d == null || !d.TryGetValue("sessions", out raw) || (raw is string) || (raw is IDictionary) || (!(raw is ArrayList) && !(raw is object[])))
                throw new InvalidOperationException("대기 세션 응답 형식이 잘못되었습니다.");
            var rows = raw as IEnumerable;
            var list = new List<SessionItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var x = row as Dictionary<string, object>;
                if (x == null || (x.Count != 5 && x.Count != 9) || list.Count >= 500) throw new InvalidOperationException("세션 목록의 항목 또는 개수 제한이 잘못되었습니다.");
                var item=ParseSessionObject(x); Guid sid; DateTimeOffset created;
                if(item.SessionId.Length!=32||!Guid.TryParseExact(item.SessionId,"N",out sid)||!seen.Add(item.SessionId)||item.InitiatorDeviceId.Length>128||item.TargetDeviceId.Length>128||!TryParseUtc(item.CreatedAt,out created)||!(new[]{"Pending","Approved","Connected","Rejected","Revoked","Expired"}).Contains(item.Status))throw new InvalidOperationException("세션 목록 필드가 잘못되었습니다.");
                list.Add(item);
            }
            return list;
        }
        public SessionItem Request(string target, string temporaryPassword)
        {
            if(String.IsNullOrWhiteSpace(target)) throw new ArgumentException("상대 장치 ID를 입력하십시오.","target");
            // null/empty only => manual approval request; whitespace is a supplied password and is never trimmed.
            if(String.IsNullOrEmpty(temporaryPassword)) temporaryPassword=null;
            else if(temporaryPassword.Length<8||temporaryPassword.Length>128) throw new ArgumentException("비밀번호는 비워 두거나 8~128자로 입력하십시오.","temporaryPassword");
            string raw;
            try { raw=Send("POST", "/api/v1/sessions/request", new { targetDeviceId=target, password=temporaryPassword }, true); }
            catch(InvalidOperationException ex)
            {
                int code=HttpStatus(ex);
                if(code==401) { string probeError; var probe=ProbeSelfAfterRequest401(out probeError); throw new SessionRequestException(RequestFeedback.Request401Message(probe,probeError),code,ex.InnerException); }
                if(code==429) throw new SessionRequestException(RequestFeedback.TooManyRequests,code,ex.InnerException);
                throw;
            }
            return ParseSession(raw);
        }
        // Server 401 on /sessions/request covers both bearer failure and unknown target / wrong / expired password.
        // One bounded authenticated GET /devices/me (no rotate, no re-register) separates them. Synchronous like Request.
        private RequestRejectProbe ProbeSelfAfterRequest401(out string probeError)
        {
            probeError=null;
            try
            {
                var data=Deserialize<Dictionary<string,object>>(Send("GET","/api/v1/devices/me",null,true,5000));
                object id;
                if(data==null||!data.TryGetValue("deviceId",out id)||!(id is string)) { probeError="장치 상태 응답 형식이 잘못되었습니다."; return RequestRejectProbe.ProbeFailed; }
                return !String.IsNullOrEmpty(DeviceId)&&String.Equals((string)id,DeviceId,StringComparison.Ordinal)?RequestRejectProbe.SelfVerified:RequestRejectProbe.SelfMismatch;
            }
            catch(InvalidOperationException ex)
            {
                if(HttpStatus(ex)==401) return RequestRejectProbe.SelfUnauthorized;
                probeError=ex.Message; return RequestRejectProbe.ProbeFailed;
            }
            catch(Exception ex) { probeError=ex.Message; return RequestRejectProbe.ProbeFailed; }
        }
        public SessionItem Decide(string id, bool approve)
        {
            return ParseSession(Send("POST", "/api/v1/sessions/decision", new { sessionId=id, approve=approve }, true));
        }
        public SessionItem Connect(string id)
        {
            return ParseSession(Send("POST", "/api/v1/sessions/connect", new { sessionId=id }, true));
        }
        public SessionItem Revoke(string id, int timeoutMs = 10000)
        {
            return ParseSession(Send("POST", "/api/v1/sessions/revoke", new { sessionId=id }, true, timeoutMs));
        }
        internal async Task RevokeAsync(string id,CancellationToken ct) { ParseSession(await SendJsonAsync("POST","/api/v1/sessions/revoke",new { sessionId=id },ct).ConfigureAwait(false)); }
        private SessionItem ParseSession(string raw)
        {
            var x=Deserialize<Dictionary<string,object>>(raw);
            if (x == null) throw new InvalidOperationException("세션 응답 형식이 잘못되었습니다.");
            return ParseSessionObject(x);
        }
        private static SessionItem ParseSessionObject(Dictionary<string,object> x)
        {
            var item=new SessionItem {
                SessionId=RequireString(x,"sessionId"), InitiatorDeviceId=RequireString(x,"initiatorDeviceId"),
                TargetDeviceId=RequireString(x,"targetDeviceId"), Status=RequireString(x,"status"),
                CreatedAt=RequireString(x,"createdAt")
            };
            ParseAuthorization(x,item);
            return item;
        }
        // Authorization provenance is server-generated. Absent on old servers => manual / no auto (fail closed).
        // If any field is present, all four must be present and well-formed.
        private static void ParseAuthorization(Dictionary<string,object> x,SessionItem item)
        {
            bool hasKind=x.ContainsKey("authorizationKind"), hasRev=x.ContainsKey("authorizationRevision"), hasEpoch=x.ContainsKey("runtimeEpoch"), hasEligible=x.ContainsKey("autoApprovalEligible");
            if(!hasKind&&!hasRev&&!hasEpoch&&!hasEligible) return;
            if(!(hasKind&&hasRev&&hasEpoch&&hasEligible)) throw new InvalidOperationException("세션 승인 정보 필드가 불완전합니다.");
            object kind=x["authorizationKind"], rev=x["authorizationRevision"], epoch=x["runtimeEpoch"], eligible=x["autoApprovalEligible"];
            string k=kind as string;
            if(k!="manual"&&k!="temporary-password"&&k!="fixed-password") throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: authorizationKind");
            long revision;
            if(rev is int) revision=(int)rev; else if(rev is long) revision=(long)rev; else throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: authorizationRevision");
            if(revision<0) throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: authorizationRevision");
            if(epoch!=null&&!IsEpoch(epoch as string)) throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: runtimeEpoch");
            if(!(eligible is bool)) throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: autoApprovalEligible");
            bool auto=(bool)eligible;
            if(auto&&(k=="manual"||epoch==null)) throw new InvalidOperationException("세션 자동 승인 정보가 일관되지 않습니다.");
            item.AuthorizationKind=k; item.AuthorizationRevision=revision; item.RuntimeEpoch=(string)epoch; item.AutoApprovalEligible=auto;
        }
        internal static bool IsEpoch(string value)
        {
            if(value==null||value.Length!=32) return false;
            foreach(char c in value) if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'))) return false;
            return true;
        }
        // Target-own password-mode decision; server re-verifies Pending/eligible/epoch/revision under its state lock.
        internal async Task<SessionItem> DecidePasswordAsync(string id,string runtimeEpoch,CancellationToken ct)
        {
            if(!IsEpoch(runtimeEpoch)) throw new ArgumentException("runtimeEpoch");
            return ParseSession(await SendJsonAsync("POST","/api/v1/sessions/decision",new { sessionId=id, approve=true, mode="password", runtimeEpoch=runtimeEpoch },ct).ConfigureAwait(false));
        }
        internal async Task<FixedPasswordState> GetFixedPasswordStateAsync(CancellationToken ct)
        {
            return ParseFixedPasswordState(await SendJsonAsync("GET","/api/v1/devices/fixed-password",null,ct).ConfigureAwait(false));
        }
        // Plaintext is sent once over TLS and never stored or returned.
        internal async Task<FixedPasswordState> SetFixedPasswordAsync(string password,CancellationToken ct)
        {
            if(password==null||password.Length<8||password.Length>128) throw new ArgumentException("고정 비밀번호는 8~128자여야 합니다.","password");
            return ParseFixedPasswordState(await SendJsonAsync("PUT","/api/v1/devices/fixed-password",new { password=password },ct).ConfigureAwait(false));
        }
        internal async Task<FixedPasswordState> ClearFixedPasswordAsync(CancellationToken ct)
        {
            return ParseFixedPasswordState(await SendJsonAsync("DELETE","/api/v1/devices/fixed-password",null,ct).ConfigureAwait(false));
        }
        private FixedPasswordState ParseFixedPasswordState(string raw)
        {
            var d=Deserialize<Dictionary<string,object>>(raw); object configured, rev; long revision;
            if(d==null||d.Count!=2||!d.TryGetValue("configured",out configured)||!(configured is bool)||!d.TryGetValue("revision",out rev)) throw new InvalidOperationException("고정 비밀번호 응답 형식이 잘못되었습니다.");
            if(rev is int) revision=(int)rev; else if(rev is long) revision=(long)rev; else throw new InvalidOperationException("고정 비밀번호 응답 형식이 잘못되었습니다.");
            if(revision<0) throw new InvalidOperationException("고정 비밀번호 응답 형식이 잘못되었습니다.");
            return new FixedPasswordState { Configured=(bool)configured, Revision=revision };
        }
        // In-memory server runtime registration (60s TTL). enabled=false returns runtimeEpoch=null.
        internal async Task<AccessRuntimeState> UpdateAccessRuntimeAsync(bool enabled,string instanceNonce,CancellationToken ct)
        {
            if(!IsEpoch(instanceNonce)) throw new ArgumentException("instanceNonce");
            var d=Deserialize<Dictionary<string,object>>(await SendJsonAsync("POST","/api/v1/devices/access-runtime",new { version=1, enabled=enabled, instanceNonce=instanceNonce },ct).ConfigureAwait(false));
            object version, en, epoch, expires;
            if(d==null||d.Count!=4||!d.TryGetValue("version",out version)||!(version is int)||(int)version!=1||!d.TryGetValue("enabled",out en)||!(en is bool)||!d.TryGetValue("runtimeEpoch",out epoch)||!d.TryGetValue("expiresAt",out expires))
                throw new InvalidOperationException("접근 런타임 응답 형식이 잘못되었습니다.");
            bool on=(bool)en; DateTimeOffset exp=default(DateTimeOffset);
            if(on!=enabled) throw new InvalidOperationException("접근 런타임 응답 상태가 요청과 다릅니다.");
            if(on) { if(!IsEpoch(epoch as string)||!(expires is string)||!TryParseUtc((string)expires,out exp)) throw new InvalidOperationException("접근 런타임 응답 형식이 잘못되었습니다."); }
            else if(epoch!=null||expires!=null) throw new InvalidOperationException("접근 런타임 응답 형식이 잘못되었습니다.");
            return new AccessRuntimeState { Enabled=on, RuntimeEpoch=on?(string)epoch:null, ExpiresAt=exp };
        }
        private static string RequireString(Dictionary<string,object> x, string key)
        {
            object value;
            if (!x.TryGetValue(key,out value) || !(value is string) || String.IsNullOrWhiteSpace((string)value))
                throw new InvalidOperationException("세션 응답 필드가 잘못되었습니다: "+key);
            return (string)value;
        }
        internal Task<string> SendJsonAsync(string method, string path, object body, CancellationToken cancellationToken)
        {
            return Task.Run(() => Send(method, path, body, true, 10000, cancellationToken), cancellationToken);
        }
        internal Task<MediaHttpResponse> SendMediaAsync(string method, string path, byte[] body, IDictionary<string,string> headers, CancellationToken cancellationToken)
        {
            return Task.Run(() => SendMedia(method,path,body,headers,cancellationToken), cancellationToken);
        }
        private MediaHttpResponse SendMedia(string method,string path,byte[] body,IDictionary<string,string> headers,CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
            var req=(HttpWebRequest)WebRequest.Create(new Uri(profile.RendezvousUrl.TrimEnd('/')+path));
            req.Method=method; req.AllowAutoRedirect=false; req.Timeout=10000; req.ReadWriteTimeout=10000; req.Proxy=null;
            // Bound per-origin concurrency for frame/input/viewer traffic, without changing global ServicePoint defaults.
            req.ServicePoint.ConnectionLimit=4; req.ServicePoint.Expect100Continue=false; req.ServicePoint.UseNagleAlgorithm=false;
            req.ServerCertificateValidationCallback=ValidateCertificate;
            if(String.IsNullOrEmpty(Token)) throw new InvalidOperationException("먼저 장치를 등록하십시오.");
            req.Headers[HttpRequestHeader.Authorization]="Bearer "+Token;
            if(headers!=null) foreach(var h in headers) req.Headers[h.Key]=h.Value;
            using(cancellationToken.Register(()=>{try{req.Abort();}catch{}}))
            using(new System.Threading.Timer(delegate{try{req.Abort();}catch{}},null,10000,System.Threading.Timeout.Infinite))
            {
                try {
                    if(body!=null) { req.ContentType="image/jpeg"; req.ContentLength=body.Length; using(var s=req.GetRequestStream()) s.Write(body,0,body.Length); }
                    using(var response=(HttpWebResponse)req.GetResponse())
                    {
                        var result=new MediaHttpResponse { StatusCode=(int)response.StatusCode, ContentType=response.ContentType, Headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) };
                        foreach(string key in response.Headers.AllKeys) result.Headers[key]=response.Headers[key];
                        if(response.StatusCode==HttpStatusCode.NoContent) return result;
                        using(var stream=response.GetResponseStream())
                        using(var ms=new MemoryStream()) {
                            byte[] buffer=new byte[4096]; int n;
                            while((n=stream.Read(buffer,0,buffer.Length))>0) { if(ms.Length+n>524288) throw new InvalidOperationException("미디어 응답이 512KB를 초과했습니다."); ms.Write(buffer,0,n); }
                            result.Body=ms.ToArray();
                        }
                        return result;
                    }
                } catch(WebException ex) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response=ex.Response as HttpWebResponse;
                    if(response!=null) { int code=(int)response.StatusCode; string endReason=response.Headers["X-PeerDesk-Media-End"]; response.Close(); throw new MediaHttpException(code,ex,endReason); }
                    throw new InvalidOperationException("미디어 서버 연결 실패: "+ex.Message,ex);
                }
            }
        }
        internal async Task<Dictionary<string,object>> OwnerGetSettingsAsync(string secret)
        {
            return ParseOwnerSettings(await SendOwnerAsync("GET","/api/v1/owner/settings",null,secret).ConfigureAwait(false));
        }
        // Empty joinPassword is sent as null: the server keeps the existing verifier (name-only update). Nonempty values are never trimmed.
        internal async Task<Dictionary<string,object>> OwnerSettingsAsync(string secret,string name,string joinPassword)
        {
            if(String.IsNullOrWhiteSpace(name)||name.Trim().Length>64) throw new ArgumentException("표시 이름은 1~64자여야 합니다.","name");
            if(joinPassword!=null&&joinPassword.Length==0) joinPassword=null;
            if(joinPassword!=null&&(joinPassword.Length<8||joinPassword.Length>128)) throw new ArgumentException("새 참가 비밀번호는 8~128자여야 합니다. 기존 비밀번호를 유지하려면 비워 두십시오.","joinPassword");
            return ParseOwnerSettings(await SendOwnerAsync("POST","/api/v1/owner/settings",new Dictionary<string,object> { {"displayName",name.Trim()},{"joinPassword",joinPassword} },secret).ConfigureAwait(false));
        }
        private static Dictionary<string,object> ParseOwnerSettings(Dictionary<string,object> d)
        {
            object name,configured;
            if(d==null||d.Count!=2||!d.TryGetValue("displayName",out name)||!(name is string)||((string)name).Length<1||((string)name).Length>64||String.IsNullOrWhiteSpace((string)name))
                throw new InvalidOperationException("소유자 설정 응답의 표시 이름이 올바르지 않습니다.");
            if(!d.TryGetValue("joinPasswordConfigured",out configured)||!(configured is bool))
                throw new InvalidOperationException("소유자 설정 응답의 참가 비밀번호 상태가 올바르지 않습니다.");
            return d;
        }
        internal async Task<OwnerRoomInfo> OwnerGetRoomAsync(string secret)
        {
            return ParseOwnerRoom(await SendOwnerAsync("GET","/api/v1/owner/room",null,secret).ConfigureAwait(false));
        }
        // serverId must already be normalized (ASCII lowercase) by the caller. Empty joinPassword is sent as null (keep existing); nonempty values are never trimmed.
        internal async Task<OwnerRoomInfo> OwnerSaveRoomAsync(string secret,string serverId,string joinPassword)
        {
            if(!OwnerRoomInfo.IsValidServerId(serverId)) throw new ArgumentException(OwnerRoomInfo.ServerIdRuleMessage,"serverId");
            if(joinPassword!=null&&joinPassword.Length==0) joinPassword=null;
            if(joinPassword!=null&&(joinPassword.Length<8||joinPassword.Length>128)) throw new ArgumentException("서버 PW는 8~128자여야 합니다. 기존 PW를 유지하려면 비워 두십시오.","joinPassword");
            return ParseOwnerRoom(await SendOwnerAsync("POST","/api/v1/owner/room",new Dictionary<string,object> { {"serverId",serverId},{"joinPassword",joinPassword} },secret).ConfigureAwait(false));
        }
        private static OwnerRoomInfo ParseOwnerRoom(Dictionary<string,object> d)
        {
            object id,ids,configured,self,profileJson;
            if(d==null||d.Count!=5||!d.TryGetValue("serverId",out id)||!d.TryGetValue("serverIds",out ids)||!d.TryGetValue("joinPasswordConfigured",out configured)||!d.TryGetValue("selfPublishEnabled",out self)||!d.TryGetValue("profileJson",out profileJson))
                throw new InvalidOperationException("서버 ID 응답 형식이 올바르지 않습니다.");
            if((id!=null&&!(id is string))||!(configured is bool)||!(self is bool)||!(profileJson is string)||!(ids is ArrayList||ids is object[]))
                throw new InvalidOperationException("서버 ID 응답 필드 형식이 올바르지 않습니다.");
            var list=new List<string>();
            foreach(var x in (IEnumerable)ids)
            {
                var s=x as string;
                if(s==null||!OwnerRoomInfo.IsValidServerId(s)||list.Count>=8||list.Contains(s)) throw new InvalidOperationException("서버 ID 목록 형식이 올바르지 않습니다.");
                list.Add(s);
            }
            var current=(string)id;
            if((current==null&&list.Count!=0)||(current!=null&&!(bool)configured))throw new InvalidOperationException("서버 ID와 비밀번호 상태가 일치하지 않습니다.");
            if(current!=null&&(!OwnerRoomInfo.IsValidServerId(current)||!list.Contains(current))) throw new InvalidOperationException("현재 서버 ID가 목록과 일치하지 않습니다.");
            return new OwnerRoomInfo { ServerId=current,ServerIds=list.AsReadOnly(),JoinPasswordConfigured=(bool)configured,SelfPublishEnabled=(bool)self,ProfileJson=(string)profileJson };
        }
        internal async Task<List<OwnerDevice>> OwnerListDevicesAsync(string secret)
        {
            var d=await SendOwnerAsync("GET","/api/v1/owner/devices",null,secret).ConfigureAwait(false);
            object raw;
            if(!d.TryGetValue("devices",out raw)||!(raw is ArrayList||raw is object[])) throw new InvalidOperationException("장치 목록 응답 형식이 잘못되었습니다.");
            var list=new List<OwnerDevice>();
            foreach(var row in (IEnumerable)raw)
            {
                var x=row as Dictionary<string,object>;
                if(x==null||list.Count>=1000) throw new InvalidOperationException("장치 목록 항목 형식이 잘못되었습니다.");
                list.Add(new OwnerDevice { DeviceId=RequireString(x,"deviceId"),RegisteredAt=RequireString(x,"registeredAt") });
            }
            return list;
        }
        internal async Task OwnerRevokeDeviceAsync(string secret,string deviceId)
        {
            if(String.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("장치를 선택하십시오.","deviceId");
            var d=await SendOwnerAsync("POST","/api/v1/owner/devices/revoke",new Dictionary<string,object> { {"deviceId",deviceId} },secret).ConfigureAwait(false);
            object v;
            if(!d.TryGetValue("revoked",out v)||!(v is bool)||!(bool)v) throw new InvalidOperationException("장치 해제 응답이 올바르지 않습니다.");
        }
        internal Task<Dictionary<string,object>> OwnerProofAsync(string secret,Dictionary<string,object> challenge)
        {
            if(challenge==null) throw new ArgumentNullException("challenge");
            return SendOwnerAsync("POST","/api/v1/owner/proof",challenge,secret);
        }
        private Task<Dictionary<string,object>> SendOwnerAsync(string method,string path,object body,string secret)
        {
            if(String.IsNullOrEmpty(secret)||secret.Length<32||secret.Length>1024) throw new ArgumentException(OwnerSecretRequiredMessage,"secret");
            var headers=new Dictionary<string,string> { {"X-PeerDesk-Owner",secret} };
            return Task.Run(() =>
            {
                var d=Deserialize<Dictionary<string,object>>(Send(method,path,body,false,10000,default(CancellationToken),headers));
                if(d==null) throw new InvalidOperationException("소유자 API 응답 형식이 잘못되었습니다.");
                return d;
            });
        }
        // Unauthenticated JSON call over the same pinned, redirect-free transport (used by DirectoryClient).
        internal Task<Dictionary<string,object>> SendPublicJsonAsync(string method,string path,object body)
        {
            return Task.Run(() =>
            {
                var d=Deserialize<Dictionary<string,object>>(Send(method,path,body,false));
                if(d==null) throw new InvalidOperationException("서버 응답 형식이 잘못되었습니다.");
                return d;
            });
        }
        private T Deserialize<T>(string text) { lock(json) return json.Deserialize<T>(text); }
        private byte[] Serialize(object body) { lock(json) return Encoding.UTF8.GetBytes(json.Serialize(body)); }
        private string Send(string method, string path, object body, bool auth, int timeoutMs = 10000, CancellationToken cancellationToken = default(CancellationToken), IDictionary<string,string> extraHeaders = null, int maxResponseBytes = 65536)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var uri = new Uri(profile.RendezvousUrl.TrimEnd('/') + path);
            var req=(HttpWebRequest)WebRequest.Create(uri);
            int boundedTimeout = Math.Max(1, Math.Min(10000, timeoutMs));
            req.Method=method; req.AllowAutoRedirect=false; req.Timeout=boundedTimeout; req.ReadWriteTimeout=boundedTimeout;
            req.Proxy=null;
            // Bound per-origin concurrency for frame/input/viewer traffic, without changing global ServicePoint defaults.
            req.ServicePoint.ConnectionLimit=4; req.ServicePoint.Expect100Continue=false; req.ServicePoint.UseNagleAlgorithm=false;
            req.ServerCertificateValidationCallback = ValidateCertificate;
            if (auth) {
                if (String.IsNullOrEmpty(Token)) throw new InvalidOperationException("먼저 장치를 등록하십시오.");
                req.Headers[HttpRequestHeader.Authorization]="Bearer "+Token;
            }
            if (extraHeaders != null) foreach (var h in extraHeaders) req.Headers[h.Key]=h.Value;
            using (cancellationToken.Register(() => { try { req.Abort(); } catch { } }))
            using (new System.Threading.Timer(delegate { try { req.Abort(); } catch { } }, null, boundedTimeout, System.Threading.Timeout.Infinite))
            {
                try {
                    if (body != null) {
                        byte[] bytes=Serialize(body);
                        req.ContentType="application/json; charset=utf-8"; req.ContentLength=bytes.Length;
                        using(var s=req.GetRequestStream()) s.Write(bytes,0,bytes.Length);
                    }
                    using(var resp=(HttpWebResponse)req.GetResponse())
                    using(var stream=resp.GetResponseStream())
                    using(var ms=new MemoryStream()) {
                        byte[] buf=new byte[4096]; int n; int total=0;
                        while((n=stream.Read(buf,0,buf.Length))>0) { total+=n; if(total>maxResponseBytes) throw new InvalidOperationException("서버 응답이 허용 크기를 초과했습니다."); ms.Write(buf,0,n); }
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                } catch(WebException ex) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var r=ex.Response as HttpWebResponse;
                    string detail="";
                    if(r!=null) {
                        var typed=new MediaHttpException((int)r.StatusCode,ex,r.Headers["X-PeerDesk-Media-End"]);
                        if(auth&&typed.EndReason!=null&&IsMediaJsonPath(path)){r.Close();throw typed;}
                        using(var responseStream=r.GetResponseStream())
                        using(var sr=new StreamReader(responseStream)) {
                            char[] b=new char[2048]; int n=sr.Read(b,0,b.Length); if(n>0) detail=new string(b,0,n);
                        }
                        throw new InvalidOperationException("서버 응답 "+(int)r.StatusCode+" "+detail,ex);
                    }
                    throw new InvalidOperationException("서버 연결 실패: "+ex.Message,ex);
                }
            }
        }
        private static bool IsMediaJsonPath(string path)
        {
            const string prefix="/api/v1/sessions/";
            if(path==null||!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            int slash=path.IndexOf('/',prefix.Length);
            if(slash<=prefix.Length)return false;
            string suffix=path.Substring(slash);
            return suffix=="/lease"||suffix=="/input-lease"||suffix=="/input"||suffix=="/sharing";
        }
        private bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0 ||
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
            using(var cert=new X509Certificate2(certificate)) {
                DateTime now=DateTime.UtcNow;
                if(now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
                using(var sha=SHA256.Create()) {
                    string hash=BitConverter.ToString(sha.ComputeHash(cert.RawData)).Replace("-","");
                    return String.Equals(hash,profile.CertificateSha256,StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
    internal sealed class MediaHttpResponse { public int StatusCode; public string ContentType; public byte[] Body; public Dictionary<string,string> Headers; }
    internal sealed class OwnerSettings
    {
        public string DisplayName { get; set; }
        public bool JoinPasswordConfigured { get; set; }
    }
    internal sealed class OwnerDevice
    {
        public string DeviceId { get; set; }
        public string RegisteredAt { get; set; }
        public override string ToString() { return DeviceId+"   ·   "+RegisteredAt; }
    }
    internal sealed class DeviceIdentity
    {
        public string DeviceId { get; set; }
        public string PasswordExpiresAt { get; set; }
        public DateTimeOffset PasswordExpiresAtUtc { get; set; }
        public long PasswordGeneration { get; set; }
        public bool RotationRequired { get; set; }
        public DateTimeOffset ServerTimeUtc { get; set; }
    }
    internal sealed class PasswordGenerationConflictException : InvalidOperationException
    {
        public PasswordGenerationConflictException(Exception inner) : base("다른 창이나 장치에서 비밀번호가 먼저 갱신되었습니다.",inner) { }
    }
    // Outcome of the single GET /api/v1/devices/me probe after a session request returned 401.
    internal enum RequestRejectProbe { SelfVerified, SelfUnauthorized, SelfMismatch, ProbeFailed }
    // Pure request-failure message mapping (no I/O, never includes passwords).
    internal static class RequestFeedback
    {
        internal const string WrongTargetOrPassword="상대 PC ID 또는 비밀번호가 맞지 않습니다. 최신 임시 PW 또는 설정한 고정 PW를 확인해 주세요.";
        internal const string RegistrationRejected="이 PC의 등록 인증이 거부되었습니다. 다시 등록해야 합니다.";
        internal const string IdentityMismatch="연결 요청이 거부되었고(401) 이 PC의 등록 ID가 서버 확인 결과와 일치하지 않습니다. 다시 등록이 필요할 수 있습니다.";
        internal const string TooManyRequests="연결 요청이 너무 자주 반복되었습니다. 잠시 기다린 후 다시 시도해 주세요.";
        internal static string Request401Message(RequestRejectProbe probe,string probeError)
        {
            switch(probe)
            {
                case RequestRejectProbe.SelfVerified: return WrongTargetOrPassword;
                case RequestRejectProbe.SelfUnauthorized: return RegistrationRejected;
                case RequestRejectProbe.SelfMismatch: return IdentityMismatch;
                default: return "연결 요청이 거부되었지만(401) 원인을 확인하지 못했습니다(비밀번호 오류로 단정할 수 없음): "+(String.IsNullOrEmpty(probeError)?"확인 요청 실패":probeError);
            }
        }
    }
    // User-facing request failure. InnerException is the original WebException so HttpStatus-style inspection still
    // yields the HTTP code; StatusCode mirrors it.
    internal sealed class SessionRequestException : InvalidOperationException
    {
        internal int StatusCode { get; private set; }
        public SessionRequestException(string message,int code,Exception inner) : base(message,inner) { StatusCode=code; }
    }
    internal sealed class MediaHttpException : InvalidOperationException
    {
        internal int StatusCode { get; private set; }
        internal string EndReason { get; private set; }
        public MediaHttpException(int code,Exception inner,string endReason=null) : base("미디어 서버 응답 "+code,inner) { StatusCode=code; EndReason=code==409&&(endReason=="user-stopped"||endReason=="session-ended"||endReason=="error-stopped"||endReason=="expired")?endReason:null; }
    }
    internal sealed class FixedPasswordState { public bool Configured; public long Revision; }
    internal sealed class AccessRuntimeState { public bool Enabled; public string RuntimeEpoch; public DateTimeOffset ExpiresAt; }
    internal sealed class SessionItem
    {
        public string SessionId, InitiatorDeviceId, TargetDeviceId, Status, CreatedAt;
        public string AuthorizationKind="manual", RuntimeEpoch;
        public long AuthorizationRevision;
        public bool AutoApprovalEligible;
        public override string ToString() { return "요청 "+InitiatorDeviceId+" → 대상 "+TargetDeviceId+"   ·   "+Status; }
    }
}
