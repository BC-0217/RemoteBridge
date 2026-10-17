using System;
using System.Collections.Generic;
using PeerDesk.Windows;

// Synthetic mapper checks only (no real HTTP): status/probe values are fabricated inputs.
internal static class Program
{
    private static int failures;
    private static void Check(bool ok,string name){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;}
    private static SessionItem Row(string sid,string init,string target,string status){return new SessionItem{SessionId=sid,InitiatorDeviceId=init,TargetDeviceId=target,Status=status,CreatedAt="2026-01-01T00:00:00Z"};}

    private static int Main()
    {
        Check(RequestFeedback.Request401Message(RequestRejectProbe.SelfVerified,null)==RequestFeedback.WrongTargetOrPassword,"401+self verified => wrong target/password");
        Check(RequestFeedback.Request401Message(RequestRejectProbe.SelfUnauthorized,null)==RequestFeedback.RegistrationRejected,"401+self 401 => re-register");
        Check(RequestFeedback.Request401Message(RequestRejectProbe.SelfMismatch,null)==RequestFeedback.IdentityMismatch,"401+id mismatch => not wrong password");
        var failed=RequestFeedback.Request401Message(RequestRejectProbe.ProbeFailed,"TLS failure");
        Check(failed!=RequestFeedback.WrongTargetOrPassword&&failed.Contains("TLS failure"),"401+probe failure => cannot verify, keeps cause");
        var inner=new Exception("inner");
        var ex=new SessionRequestException("m",401,inner);
        Check(ex.StatusCode==401&&ReferenceEquals(ex.InnerException,inner)&&ex is InvalidOperationException,"exception preserves status/inner/type");

        foreach(var reason in new[]{"user-stopped","session-ended","error-stopped","expired"})
            Check(new MediaHttpException(409,inner,reason).EndReason==reason,"typed409 exact "+reason);
        Check(new MediaHttpException(401,inner,"user-stopped").EndReason==null,"401 cannot claim normal end");
        Check(new MediaHttpException(409,inner,"USER-STOPPED").EndReason==null,"unknown/case-changed end reason ignored");
        Check(new MediaHttpException(409,inner).EndReason==null,"old server untyped409 remains error");
        const string sid="0123456789abcdef0123456789abcdef";
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"PEER","ME","Revoked")},sid,"ME","PEER")==SessionFeedback.PeerEnded,"Revoked matching => peer ended");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"PEER","ME","Rejected")},sid,"ME","PEER")==SessionFeedback.PeerRejected,"Rejected matching => rejected");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"PEER","ME","Expired")},sid,"ME","PEER")==null,"Expired => not normal");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"PEER","ME","Connected")},sid,"ME","PEER")==null,"Connected => no inference");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"OTHER","ME","Revoked")},sid,"ME","PEER")==null,"wrong peer => null");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row("ffffffffffffffffffffffffffffffff","PEER","ME","Revoked")},sid,"ME","PEER")==null,"missing sid => null");
        Check(SessionFeedback.SessionEndMessage(new List<SessionItem>{Row(sid,"PEER","ME","Revoked"),Row(sid,"PEER","ME","Revoked")},sid,"ME","PEER")==null,"duplicate sid => null");
        Check(SessionFeedback.SessionEndMessage(null,sid,"ME","PEER")==null,"null rows => null");
        Console.WriteLine(failures==0?"ALL PASS":"FAILURES: "+failures);
        return failures==0?0:1;
    }
}
