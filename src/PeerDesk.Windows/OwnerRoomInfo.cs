using System;
using System.Collections.Generic;

namespace PeerDesk.Windows
{
    // Owner room state exactly as returned by GET/POST /api/v1/owner/room. The join password is never returned.
    internal sealed class OwnerRoomInfo
    {
        internal const string ServerIdRuleMessage="서버 ID는 영문 소문자/숫자/하이픈 3~32자이며, 처음과 끝은 영문 또는 숫자여야 합니다.";
        public string ServerId { get; set; }
        public IList<string> ServerIds { get; set; }
        public bool JoinPasswordConfigured { get; set; }
        public bool SelfPublishEnabled { get; set; }
        public string ProfileJson { get; set; }

        // ASCII-only lowercase; non-ASCII characters are left untouched so validation rejects them.
        internal static string Normalize(string value)
        {
            if(value==null) return "";
            var chars=value.Trim().ToCharArray();
            for(int i=0;i<chars.Length;i++) if(chars[i]>='A'&&chars[i]<='Z') chars[i]=(char)(chars[i]+32);
            return new string(chars);
        }
        internal static bool IsValidServerId(string id)
        {
            if(id==null||id.Length<3||id.Length>32) return false;
            for(int i=0;i<id.Length;i++)
            {
                char c=id[i];
                bool alnum=(c>='a'&&c<='z')||(c>='0'&&c<='9');
                if(alnum) continue;
                if(c=='-'&&i>0&&i<id.Length-1) continue;
                return false;
            }
            return true;
        }
    }
}
