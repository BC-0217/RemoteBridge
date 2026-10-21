using System;
using System.Collections.Generic;

namespace PeerDesk.Windows
{
    // Pure session-end message mapping: no I/O. Request-failure messages live in ControlPlaneClient.cs (RequestFeedback)
    // because test hosts link ControlPlaneClient.cs alone.
    internal static class SessionFeedback
    {
        internal const string PeerEnded="연결이 종료되었습니다.";
        internal const string PeerRejected="상대방이 연결 요청을 거절했습니다.";

        // Returns a normal end message only for an authoritative snapshot row that matches sid, own id and expected peer
        // and is terminal Revoked/Rejected. Expired, live states, missing, duplicate or mismatching rows return null so the
        // caller keeps its original diagnostic.
        internal static string SessionEndMessage(IEnumerable<SessionItem> rows,string sessionId,string ownId,string peerId)
        {
            if(rows==null||String.IsNullOrEmpty(sessionId)||String.IsNullOrEmpty(ownId)||String.IsNullOrEmpty(peerId)||String.Equals(ownId,peerId,StringComparison.Ordinal))return null;
            SessionItem match=null;
            foreach(var x in rows)
            {
                if(x==null||!String.Equals(x.SessionId,sessionId,StringComparison.Ordinal))continue;
                if(match!=null)return null;
                match=x;
            }
            if(match==null)return null;
            bool pair=(String.Equals(match.TargetDeviceId,ownId,StringComparison.Ordinal)&&String.Equals(match.InitiatorDeviceId,peerId,StringComparison.Ordinal))
                ||(String.Equals(match.InitiatorDeviceId,ownId,StringComparison.Ordinal)&&String.Equals(match.TargetDeviceId,peerId,StringComparison.Ordinal));
            if(!pair)return null;
            if(String.Equals(match.Status,"Revoked",StringComparison.Ordinal))return PeerEnded;
            if(String.Equals(match.Status,"Rejected",StringComparison.Ordinal))return PeerRejected;
            return null;
        }
    }
}
