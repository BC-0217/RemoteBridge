using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    internal sealed class ServerProfile
    {
        public int Version { get; private set; }
        public string Name { get; private set; }
        public string RendezvousUrl { get; private set; }
        public string RelayUrl { get; private set; }
        public string CertificateSha256 { get; private set; }

        public static ServerProfile Parse(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > 16384)
                throw new InvalidOperationException("프로필은 16KB 이하여야 합니다.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16384 };
            ValidateFlatObjectKeys(json, serializer);
            var d = serializer.DeserializeObject(json) as Dictionary<string, object>;
            var allowed = new HashSet<string>(new[] { "Version", "Name", "RendezvousUrl", "RelayUrl", "CertificateSha256" }, StringComparer.Ordinal);
            if (d == null || d.Keys.Any(k => !allowed.Contains(k)) || d.Count != 5)
                throw new InvalidOperationException("프로필 필드는 Version, Name, RendezvousUrl, RelayUrl, CertificateSha256만 허용됩니다.");
            object version;
            if (!d.TryGetValue("Version", out version) || !(version is int) || (int)version != 1)
                throw new InvalidOperationException("Version은 숫자 1이어야 합니다.");
            var p = new ServerProfile {
                Version = 1, Name = GetString(d, "Name"), RendezvousUrl = GetString(d, "RendezvousUrl"),
                RelayUrl = GetString(d, "RelayUrl"), CertificateSha256 = GetString(d, "CertificateSha256")
            };
            if (String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64) throw new InvalidOperationException("Name은 1~64자여야 합니다.");
            ValidateUrl(p.RendezvousUrl); ValidateUrl(p.RelayUrl);
            if (p.CertificateSha256.Length != 64 || !Regex.IsMatch(p.CertificateSha256, "^[0-9a-fA-F]{64}$")) throw new InvalidOperationException("인증서 SHA-256은 64자리 16진수여야 합니다.");
            return p;
        }

        internal static void ValidateFlatObjectKeys(string json, JavaScriptSerializer serializer)
        {
            int i = 0;
            SkipWhite(json, ref i);
            if (i >= json.Length || json[i++] != '{') throw new InvalidOperationException("프로필은 JSON 객체여야 합니다.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            SkipWhite(json, ref i);
            if (i < json.Length && json[i] == '}') throw new InvalidOperationException("프로필 필드가 없습니다.");
            while (i < json.Length)
            {
                SkipWhite(json, ref i);
                string rawKey = ReadQuoted(json, ref i);
                string key = serializer.Deserialize<string>(rawKey);
                if (!names.Add(key)) throw new InvalidOperationException("JSON 키 중복(escape 표기 포함)이 있습니다.");
                SkipWhite(json, ref i);
                if (i >= json.Length || json[i++] != ':') throw new InvalidOperationException("JSON 필드 구문이 잘못되었습니다.");
                SkipWhite(json, ref i);
                if (i >= json.Length) throw new InvalidOperationException("JSON 필드 값이 없습니다.");
                if (json[i] == '"') ReadQuoted(json, ref i);
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}' && !Char.IsWhiteSpace(json[i])) i++;
                    if (start == i) throw new InvalidOperationException("JSON 필드 값이 잘못되었습니다.");
                    string atom = json.Substring(start, i - start);
                    if (atom != "1" && key != "Version") { /* typed validation below rejects non-string values */ }
                }
                SkipWhite(json, ref i);
                if (i >= json.Length) throw new InvalidOperationException("JSON 객체가 닫히지 않았습니다.");
                if (json[i] == '}') { i++; break; }
                if (json[i++] != ',') throw new InvalidOperationException("JSON 필드 구분자가 잘못되었습니다.");
            }
            SkipWhite(json, ref i);
            if (i != json.Length) throw new InvalidOperationException("JSON 끝에 불필요한 데이터가 있습니다.");
        }
        private static string ReadQuoted(string json, ref int i)
        {
            int start = i;
            if (i >= json.Length || json[i++] != '"') throw new InvalidOperationException("JSON 문자열이 필요합니다.");
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') return json.Substring(start, i - start);
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    char escape = json[i++];
                    if (escape == 'u')
                    {
                        if (i + 4 > json.Length) break;
                        for (int n = 0; n < 4; n++) if (!Uri.IsHexDigit(json[i + n])) throw new InvalidOperationException("JSON escape가 잘못되었습니다.");
                        i += 4;
                    }
                    else if ("\\\"/bfnrt".IndexOf(escape) < 0) throw new InvalidOperationException("JSON escape가 잘못되었습니다.");
                }
                else if (c < 0x20) throw new InvalidOperationException("JSON 제어 문자는 문자열에 허용되지 않습니다.");
            }
            throw new InvalidOperationException("JSON 문자열이 닫히지 않았습니다.");
        }
        private static void SkipWhite(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\r' || s[i] == '\n' || s[i] == '\t')) i++; }
        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is string)) throw new InvalidOperationException(key + " 필드가 문자열이어야 합니다.");
            return (string)v;
        }
        private static void ValidateUrl(string value)
        {
            Uri u;
            if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out u) ||
                u.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(u.UserInfo) ||
                !String.IsNullOrEmpty(u.Query) || !String.IsNullOrEmpty(u.Fragment) || u.AbsolutePath != "/")
                throw new InvalidOperationException("서버 URL은 경로 없는 HTTPS root URL이어야 합니다.");
        }
    }
}
