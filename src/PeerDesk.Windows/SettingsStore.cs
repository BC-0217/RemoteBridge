using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    internal sealed class SettingsState
    {
        public string ProfileJson { get; set; }
        public bool IsRegistered { get; set; }
        public bool PasswordAutoAccess { get; set; } = true;
        public string DeviceId { get; set; }
        public string BearerToken { get; set; }
        public string TemporaryPassword { get; set; }
        public string PasswordExpiresAt { get; set; }
        public string BoundServerUrl { get; set; }
        public string BoundCertificateSha256 { get; set; }
        // Remembered input drafts. Restored values are always UNVERIFIED and are never authority proof.
        public List<SavedInputEntry> SavedInputs { get; set; } = new List<SavedInputEntry>();
    }

    internal sealed class SavedInputEntry
    {
        public string Role { get; set; }
        public string ServerUrl { get; set; }
        public string RelayUrl { get; set; }
        public string CertificateSha256 { get; set; }
        public string RegistryUrl { get; set; }
        public string RegistryCertificateSha256 { get; set; }
        public string RegistryIdentity { get; set; }
        public string ServerId { get; set; }
        public string OwnerKey { get; set; }
        public string PasswordDraft { get; set; }
        public string VerifiedJoinPassword { get; set; }
        public bool Remember { get; set; } = true;
        public SavedInputEntry Clone() { return (SavedInputEntry)MemberwiseClone(); }
    }

    internal sealed class SettingsStore
    {
        private const int MaximumBytes=65536;
        internal const int MaximumSavedInputs=16;
        private static readonly byte[] SavedInputsEntropy=Encoding.UTF8.GetBytes("RemoteBridge.SavedInputs.v1");
        private static readonly string[] EntryStringFields={
            "Role","ServerUrl","RelayUrl","CertificateSha256","RegistryUrl","RegistryCertificateSha256",
            "RegistryIdentity","ServerId","OwnerKey","PasswordDraft","VerifiedJoinPassword"
        };
        private readonly string path;
        private readonly JavaScriptSerializer json=new JavaScriptSerializer { MaxJsonLength=MaximumBytes*8 };
        private bool loadFailed;
        public string Warning { get; private set; }

        public SettingsStore(string filePath) { path=filePath; }

        public SettingsState Load()
        {
            try { return LoadInternal(); }
            catch { loadFailed=true; throw; }
        }

        private SettingsState LoadInternal()
        {
            Warning=null;
            byte[] bytes;
            try { bytes=ReadBounded(); }
            catch(FileNotFoundException) { return new SettingsState { ProfileJson="" }; }
            catch(DirectoryNotFoundException) { return new SettingsState { ProfileJson="" }; }
            string text=new UTF8Encoding(false,true).GetString(bytes);
            ServerProfile.ValidateFlatObjectKeys(text,json);
            var outer=json.Deserialize<Dictionary<string,object>>(text);
            object versionValue;
            if(outer==null||!outer.TryGetValue("Version",out versionValue)||!(versionValue is int))
                throw new InvalidDataException("설정 파일 형식이 잘못되었습니다.");
            int version=(int)versionValue;
            if(version==1)
            {
                if(outer.Count!=3||!outer.ContainsKey("ProfileJson")||!outer.ContainsKey("ProtectedCredentials"))
                    throw new InvalidDataException("설정 파일의 필드 구성이 잘못되었습니다.");
            }
            else if(version==2)
            {
                if(outer.Count!=4||!outer.ContainsKey("ProfileJson")||!outer.ContainsKey("ProtectedCredentials")||!outer.ContainsKey("ProtectedInputs"))
                    throw new InvalidDataException("설정 파일의 필드 구성이 잘못되었습니다.");
            }
            else throw new InvalidDataException("설정 파일 형식이 잘못되었습니다.");
            object profileValue,protectedValue;
            if(!outer.TryGetValue("ProfileJson",out profileValue)||!(profileValue is string) ||
               !outer.TryGetValue("ProtectedCredentials",out protectedValue))
                throw new InvalidDataException("설정 파일 형식이 잘못되었습니다.");
            var state=new SettingsState { ProfileJson=(string)profileValue };
            if(state.ProfileJson.Length>16384) throw new InvalidDataException("공개 서버 프로필이 16KiB를 초과합니다.");
            ServerProfile profile=String.IsNullOrEmpty(state.ProfileJson)?null:ServerProfile.Parse(state.ProfileJson);
            // Saved inputs are decrypted independently of the active profile; each entry carries and validates its own context.
            if(version==2) state.SavedInputs=LoadSavedInputs(outer["ProtectedInputs"]);
            if(protectedValue==null) return state;
            if(profile==null || !(protectedValue is string)) throw new InvalidDataException("암호화된 등록 정보에 프로필이 없습니다.");
            byte[] protectedBytes;
            try { protectedBytes=Convert.FromBase64String((string)protectedValue); }
            catch(FormatException ex) { throw new InvalidDataException("암호화된 등록 정보가 잘못되었습니다.",ex); }
            byte[] clear=ProtectedData.Unprotect(protectedBytes,Entropy(profile),DataProtectionScope.CurrentUser);
            string privateJson;
            try { privateJson=new UTF8Encoding(false,true).GetString(clear); }
            finally { Array.Clear(clear,0,clear.Length); }
            ServerProfile.ValidateFlatObjectKeys(privateJson,json);
            var secrets=json.Deserialize<Dictionary<string,object>>(privateJson);
            if(secrets==null||(secrets.Count!=6&&secrets.Count!=7)) throw new InvalidDataException("보호된 등록 정보의 필드 구성이 잘못되었습니다.");
            if(secrets.Count==7) { object policy; if(!secrets.TryGetValue("PasswordAutoAccess",out policy)||!(policy is bool)) throw new InvalidDataException("접근 설정 형식이 잘못되었습니다."); state.PasswordAutoAccess=(bool)policy; }
            state.DeviceId=RequiredString(secrets,"DeviceId");
            state.BearerToken=RequiredString(secrets,"BearerToken");
            state.TemporaryPassword=RequiredString(secrets,"TemporaryPassword");
            state.PasswordExpiresAt=RequiredString(secrets,"PasswordExpiresAt");
            if(state.BearerToken.Length>256||!CredentialFormat.IsStoredDeviceId(state.DeviceId)||!CredentialFormat.IsStoredPassword(state.TemporaryPassword))
                throw new InvalidDataException("저장된 장치 ID/토큰/임시 비밀번호 형식이 잘못되었습니다.");
            DateTimeOffset expiryCheck;
            if(!DateTimeOffset.TryParse(state.PasswordExpiresAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expiryCheck))
                throw new InvalidDataException("저장된 비밀번호 만료 시각이 잘못되었습니다.");
            state.BoundServerUrl=RequiredString(secrets,"ServerUrl");
            state.BoundCertificateSha256=RequiredString(secrets,"CertificateSha256");
            if(!String.Equals(state.BoundServerUrl,profile.RendezvousUrl,StringComparison.Ordinal)||
               !String.Equals(state.BoundCertificateSha256,profile.CertificateSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("등록 정보가 현재 서버 URL 또는 인증서 지문과 일치하지 않습니다.");
            state.IsRegistered=true;
            return state;
        }

        // Writes exactly state.SavedInputs (the caller's authoritative list); never merges entries back from disk.
        public void Save(SettingsState state)
        {
            if(loadFailed) throw new InvalidDataException("설정 파일을 읽는 중 오류가 있어 원본을 보존합니다.");
            if(state==null) throw new ArgumentNullException("state");
            string profileJson=state.ProfileJson??"";
            ServerProfile profile=String.IsNullOrEmpty(profileJson)?null:ServerProfile.Parse(profileJson);
            if(Encoding.UTF8.GetByteCount(profileJson)>16384) throw new InvalidDataException("공개 서버 프로필이 16KiB를 초과합니다.");
            string protectedCredentials=null;
            if(state.IsRegistered)
            {
                if(profile==null||String.IsNullOrWhiteSpace(state.DeviceId)||String.IsNullOrWhiteSpace(state.BearerToken)||
                   String.IsNullOrWhiteSpace(state.TemporaryPassword)||String.IsNullOrWhiteSpace(state.PasswordExpiresAt))
                    throw new InvalidDataException("저장할 등록 정보가 완전하지 않습니다.");
                if(state.BearerToken.Length>256||!CredentialFormat.IsStoredDeviceId(state.DeviceId)||!CredentialFormat.IsStoredPassword(state.TemporaryPassword))
                    throw new InvalidDataException("저장할 장치 ID/토큰/임시 비밀번호 길이 또는 형식이 잘못되었습니다.");
                DateTimeOffset expiryCheck;
                if(!DateTimeOffset.TryParse(state.PasswordExpiresAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expiryCheck))
                    throw new InvalidDataException("저장할 임시 비밀번호 만료 시각이 잘못되었습니다.");
                if(!String.Equals(profile.RendezvousUrl,state.BoundServerUrl,StringComparison.Ordinal)||
                   !String.Equals(profile.CertificateSha256,state.BoundCertificateSha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("등록 정보의 서버 바인딩이 프로필과 다릅니다.");
                var secrets=new Dictionary<string,object> {
                    { "DeviceId",state.DeviceId },{ "BearerToken",state.BearerToken },
                    { "TemporaryPassword",state.TemporaryPassword },{ "PasswordExpiresAt",state.PasswordExpiresAt },
                    { "ServerUrl",state.BoundServerUrl },{ "CertificateSha256",state.BoundCertificateSha256 },{ "PasswordAutoAccess",state.PasswordAutoAccess }
                };
                byte[] clear=Encoding.UTF8.GetBytes(json.Serialize(secrets));
                try { protectedCredentials=Convert.ToBase64String(ProtectedData.Protect(clear,Entropy(profile),DataProtectionScope.CurrentUser)); }
                finally { Array.Clear(clear,0,clear.Length); }
            }
            string protectedInputs=ProtectSavedInputs(state.SavedInputs);
            var envelope=new Dictionary<string,object> {
                { "Version",2 },{ "ProfileJson",profileJson },{ "ProtectedCredentials",protectedCredentials },{ "ProtectedInputs",protectedInputs }
            };
            byte[] output=new UTF8Encoding(false).GetBytes(json.Serialize(envelope));
            if(output.Length>MaximumBytes) throw new InvalidDataException("설정 파일이 64KiB를 초과합니다.");
            WriteAtomic(output);
        }

        // Clears only the device registration; remembered inputs are preserved.
        public void ClearCredentials()
        {
            SettingsState state=Load();
            state.IsRegistered=false;state.DeviceId=null;state.BearerToken=null;state.TemporaryPassword=null;
            state.PasswordExpiresAt=null;state.BoundServerUrl=null;state.BoundCertificateSha256=null;
            Save(state);
        }

        // Context key: host = role+URL+relay+pin; guest = role+registryURL+pin+identity+alias. Resolved guest target is not part of the key.
        internal static string ContextKey(SavedInputEntry entry)
        {
            if(entry==null) throw new ArgumentNullException("entry");
            if(entry.Role=="host")
                return "host\n"+entry.ServerUrl+"\n"+entry.RelayUrl+"\n"+(entry.CertificateSha256??"").ToUpperInvariant();
            if(entry.Role=="guest")
                return "guest\n"+entry.RegistryUrl+"\n"+(entry.RegistryCertificateSha256??"").ToUpperInvariant()+"\n"+entry.RegistryIdentity+"\n"+entry.ServerId;
            throw new InvalidDataException("저장 입력의 역할이 잘못되었습니다.");
        }

        // Exact-context lookup (same URL with a different pin never matches). Returns a clone or null.
        internal static SavedInputEntry FindSavedInput(IEnumerable<SavedInputEntry> entries,SavedInputEntry probe)
        {
            if(entries==null||probe==null) return null;
            string key;
            try { key=ContextKey(probe); } catch(InvalidDataException) { return null; }
            foreach(var entry in entries)
            {
                if(entry==null) continue;
                string candidate;
                try { candidate=ContextKey(entry); } catch(InvalidDataException) { continue; }
                if(String.Equals(candidate,key,StringComparison.Ordinal)) return entry.Clone();
            }
            return null;
        }

        // Returns a validated clone with empty secrets/optional fields normalized to null. Secrets are never trimmed or case-folded.
        internal static SavedInputEntry ValidateSavedInput(SavedInputEntry source)
        {
            if(source==null) throw new InvalidDataException("저장 입력 항목이 없습니다.");
            var e=source.Clone();
            e.ServerUrl=EmptyToNull(e.ServerUrl);e.RelayUrl=EmptyToNull(e.RelayUrl);e.CertificateSha256=EmptyToNull(e.CertificateSha256);
            e.RegistryUrl=EmptyToNull(e.RegistryUrl);e.RegistryCertificateSha256=EmptyToNull(e.RegistryCertificateSha256);
            e.RegistryIdentity=EmptyToNull(e.RegistryIdentity);e.ServerId=EmptyToNull(e.ServerId);
            e.OwnerKey=EmptyToNull(e.OwnerKey);e.PasswordDraft=EmptyToNull(e.PasswordDraft);e.VerifiedJoinPassword=EmptyToNull(e.VerifiedJoinPassword);
            bool hasTarget=e.ServerUrl!=null||e.RelayUrl!=null||e.CertificateSha256!=null;
            bool hasRegistry=e.RegistryUrl!=null||e.RegistryCertificateSha256!=null||e.RegistryIdentity!=null;
            if(e.Role=="host")
            {
                ValidateTarget(e);
                if(hasRegistry) ValidateRegistry(e);
                if(e.ServerId!=null&&!IsCanonicalId(e.ServerId)) throw new InvalidDataException("저장된 서버 ID 형식이 잘못되었습니다.");
                if(e.OwnerKey!=null&&(e.OwnerKey.Length<32||e.OwnerKey.Length>1024)) throw new InvalidDataException("저장된 소유자 키 길이가 잘못되었습니다.");
            }
            else if(e.Role=="guest")
            {
                ValidateRegistry(e);
                if(!IsCanonicalId(e.ServerId)) throw new InvalidDataException("저장된 서버 ID 형식이 잘못되었습니다.");
                if(hasTarget) ValidateTarget(e);
                if(e.OwnerKey!=null) throw new InvalidDataException("게스트 입력에는 소유자 키를 저장할 수 없습니다.");
            }
            else throw new InvalidDataException("저장 입력의 역할이 잘못되었습니다.");
            if(!IsValidPasswordOrNull(e.PasswordDraft)||!IsValidPasswordOrNull(e.VerifiedJoinPassword))
                throw new InvalidDataException("저장된 비밀번호 길이가 잘못되었습니다.");
            if(!e.Remember&&(e.OwnerKey!=null||e.PasswordDraft!=null||e.VerifiedJoinPassword!=null))
                throw new InvalidDataException("기억 안 함 항목에는 비밀 값을 저장할 수 없습니다.");
            return e;
        }

        private string ProtectSavedInputs(List<SavedInputEntry> entries)
        {
            if(entries==null) throw new InvalidDataException("저장 입력 목록이 없습니다.");
            if(entries.Count>MaximumSavedInputs) throw new InvalidDataException("저장 입력은 최대 16개입니다.");
            if(entries.Count==0) return null;
            var keys=new HashSet<string>(StringComparer.Ordinal);
            var payload=new Dictionary<string,string>();
            for(int i=0;i<entries.Count;i++)
            {
                var e=ValidateSavedInput(entries[i]);
                if(!keys.Add(ContextKey(e))) throw new InvalidDataException("저장 입력의 서버 문맥이 중복되었습니다.");
                var fields=new Dictionary<string,object>();
                fields["Role"]=e.Role;fields["ServerUrl"]=e.ServerUrl;fields["RelayUrl"]=e.RelayUrl;fields["CertificateSha256"]=e.CertificateSha256;
                fields["RegistryUrl"]=e.RegistryUrl;fields["RegistryCertificateSha256"]=e.RegistryCertificateSha256;
                fields["RegistryIdentity"]=e.RegistryIdentity;fields["ServerId"]=e.ServerId;fields["OwnerKey"]=e.OwnerKey;
                fields["PasswordDraft"]=e.PasswordDraft;fields["VerifiedJoinPassword"]=e.VerifiedJoinPassword;fields["Remember"]=e.Remember;
                payload["Context"+i.ToString(CultureInfo.InvariantCulture)]=json.Serialize(fields);
            }
            byte[] clear=Encoding.UTF8.GetBytes(json.Serialize(payload));
            try
            {
                if(clear.Length>MaximumBytes) throw new InvalidDataException("저장 입력이 64KiB를 초과합니다.");
                return Convert.ToBase64String(ProtectedData.Protect(clear,SavedInputsEntropy,DataProtectionScope.CurrentUser));
            }
            finally { Array.Clear(clear,0,clear.Length); }
        }

        private List<SavedInputEntry> LoadSavedInputs(object protectedValue)
        {
            var result=new List<SavedInputEntry>();
            if(protectedValue==null) return result;
            if(!(protectedValue is string)) throw new InvalidDataException("암호화된 저장 입력이 잘못되었습니다.");
            byte[] protectedBytes;
            try { protectedBytes=Convert.FromBase64String((string)protectedValue); }
            catch(FormatException ex) { throw new InvalidDataException("암호화된 저장 입력이 잘못되었습니다.",ex); }
            byte[] clear=ProtectedData.Unprotect(protectedBytes,SavedInputsEntropy,DataProtectionScope.CurrentUser);
            string payloadJson;
            try
            {
                if(clear.Length>MaximumBytes) throw new InvalidDataException("저장 입력이 64KiB를 초과합니다.");
                payloadJson=new UTF8Encoding(false,true).GetString(clear);
            }
            finally { Array.Clear(clear,0,clear.Length); }
            ServerProfile.ValidateFlatObjectKeys(payloadJson,json);
            var payload=json.Deserialize<Dictionary<string,object>>(payloadJson);
            if(payload==null||payload.Count<1||payload.Count>MaximumSavedInputs) throw new InvalidDataException("저장 입력 개수가 잘못되었습니다.");
            var keys=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<payload.Count;i++)
            {
                object raw;
                if(!payload.TryGetValue("Context"+i.ToString(CultureInfo.InvariantCulture),out raw)||!(raw is string))
                    throw new InvalidDataException("저장 입력 문맥 필드가 잘못되었습니다.");
                var e=ValidateSavedInput(ParseEntry((string)raw));
                if(!keys.Add(ContextKey(e))) throw new InvalidDataException("저장 입력의 서버 문맥이 중복되었습니다.");
                result.Add(e);
            }
            return result;
        }

        private SavedInputEntry ParseEntry(string entryJson)
        {
            ServerProfile.ValidateFlatObjectKeys(entryJson,json);
            var d=json.Deserialize<Dictionary<string,object>>(entryJson);
            if(d==null||d.Count!=EntryStringFields.Length+1) throw new InvalidDataException("저장 입력 필드 구성이 잘못되었습니다.");
            object remember;
            if(!d.TryGetValue("Remember",out remember)||!(remember is bool)) throw new InvalidDataException("저장 입력 Remember 필드가 잘못되었습니다.");
            var values=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var name in EntryStringFields)
            {
                object v;
                if(!d.TryGetValue(name,out v)||(v!=null&&!(v is string))) throw new InvalidDataException("저장 입력 필드가 잘못되었습니다: "+name);
                values[name]=(string)v;
            }
            return new SavedInputEntry {
                Role=values["Role"],ServerUrl=values["ServerUrl"],RelayUrl=values["RelayUrl"],CertificateSha256=values["CertificateSha256"],
                RegistryUrl=values["RegistryUrl"],RegistryCertificateSha256=values["RegistryCertificateSha256"],
                RegistryIdentity=values["RegistryIdentity"],ServerId=values["ServerId"],OwnerKey=values["OwnerKey"],
                PasswordDraft=values["PasswordDraft"],VerifiedJoinPassword=values["VerifiedJoinPassword"],Remember=(bool)remember
            };
        }

        private static void ValidateTarget(SavedInputEntry e)
        {
            if(!IsRootHttpsUrl(e.ServerUrl)||!IsRootHttpsUrl(e.RelayUrl)||!IsPin(e.CertificateSha256))
                throw new InvalidDataException("저장된 서버 URL/릴레이/인증서 지문이 잘못되었습니다.");
        }
        private static void ValidateRegistry(SavedInputEntry e)
        {
            if(!IsPin(e.RegistryCertificateSha256)) throw new InvalidDataException("저장된 레지스트리 인증서 지문이 잘못되었습니다.");
            new DirectoryBootstrap(e.RegistryUrl,e.RegistryCertificateSha256,e.RegistryIdentity).Validate();
        }
        private static bool IsRootHttpsUrl(string value)
        {
            Uri u;
            return value!=null&&value.Length<=2048&&Uri.TryCreate(value,UriKind.Absolute,out u)&&u.Scheme==Uri.UriSchemeHttps&&
                String.IsNullOrEmpty(u.UserInfo)&&String.IsNullOrEmpty(u.Query)&&String.IsNullOrEmpty(u.Fragment)&&u.AbsolutePath=="/";
        }
        private static bool IsPin(string value) { return value!=null&&value.Length==64&&Regex.IsMatch(value,"\\A[0-9A-Fa-f]{64}\\z",RegexOptions.CultureInvariant); }
        private static bool IsCanonicalId(string value) { return value!=null&&Regex.IsMatch(value,"\\A[a-z0-9][a-z0-9-]{2,31}\\z",RegexOptions.CultureInvariant); }
        private static bool IsValidPasswordOrNull(string value) { return value==null||(value.Length>=8&&value.Length<=128); }
        private static string EmptyToNull(string value) { return String.IsNullOrEmpty(value)?null:value; }

        private byte[] ReadBounded()
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var memory=new MemoryStream())
            {
                byte[] chunk=new byte[4096];int n;
                while((n=stream.Read(chunk,0,Math.Min(chunk.Length,MaximumBytes+1-(int)memory.Length)))>0)
                {
                    if(memory.Length+n>MaximumBytes) throw new InvalidDataException("설정 파일이 64KiB를 초과합니다.");
                    memory.Write(chunk,0,n);
                }
                return memory.ToArray();
            }
        }

        private void WriteAtomic(byte[] bytes)
        {
            string directory=Path.GetDirectoryName(path);
            if(String.IsNullOrEmpty(directory)) throw new InvalidOperationException("설정 경로가 유효하지 않습니다.");
            Directory.CreateDirectory(directory);
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { stream.Write(bytes,0,bytes.Length);stream.Flush(true); }
                if(File.Exists(path)) File.Replace(temp,path,null);
                else File.Move(temp,path);
            }
            finally { try { if(File.Exists(temp))File.Delete(temp); } catch { } }
        }

        private static byte[] Entropy(ServerProfile profile)
        {
            return Encoding.UTF8.GetBytes(profile.RendezvousUrl+"\n"+profile.CertificateSha256.ToUpperInvariant());
        }
        private static string RequiredString(Dictionary<string,object> values,string key)
        {
            object value;
            if(!values.TryGetValue(key,out value)||!(value is string)||String.IsNullOrWhiteSpace((string)value))
                throw new InvalidDataException("보호된 설정 필드가 없습니다: "+key);
            return (string)value;
        }
    }
}
