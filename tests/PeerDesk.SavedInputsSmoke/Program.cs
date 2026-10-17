using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using PeerDesk.Windows;

// Synthetic fixtures only; no live keys/passwords are read or logged.
internal static class Program
{
    private const string PinA="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PinB="BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HostUrl="https://host.example.test/";
    private const string RelayUrl="https://relay.example.test/";
    private const string RegistryUrl="https://registry.example.test/";
    private const string OwnerKey="SYNTHETIC-OWNER-KEY-0123456789abcdefXYZ";
    private const string Draft="SyntheticDraftPw1";
    private const string Verified="SyntheticVerifiedPw2";
    private static readonly byte[] InputsEntropy=Encoding.UTF8.GetBytes("RemoteBridge.SavedInputs.v1");
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static int failures;

    private static int Main()
    {
        string dir=Path.Combine(Path.GetTempPath(),"PeerDesk.SavedInputsSmoke."+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Run("host+guest draft+resolved round trip, no plaintext",()=>RoundTrip(dir));
            Run("password auto access OFF survives DPAPI reload and input rewrite",()=>{var path=NewPath(dir);var state=Registered();state.PasswordAutoAccess=false;new SettingsStore(path).Save(state);var loaded=new SettingsStore(path).Load();Check(!loaded.PasswordAutoAccess,"OFF reload");loaded.SavedInputs.Add(Host());new SettingsStore(path).Save(loaded);Check(!new SettingsStore(path).Load().PasswordAutoAccess,"OFF input rewrite");});
            Run("v1 read backwards compatible",()=>V1Read(dir));
            Run("tamper fails and preserves original",()=>Tamper(dir));
            Run("same URL wrong pin does not match",()=>WrongPin());
            Run("16 accepted, 17 rejected and preserved",()=>MaxEntries(dir));
            Run("duplicate envelope/payload/entry keys rejected",()=>Duplicates(dir));
            Run("tombstone without secrets; ClearCredentials preserves inputs",()=>TombstoneAndClear(dir));
            Run("invalid entries rejected",()=>InvalidEntries(dir));
            Run("bounded file/read failure preserves original",()=>BoundedFailure(dir));
        }
        finally { try { Directory.Delete(dir,true); } catch { } }
        Console.WriteLine(failures==0?"SavedInputsSmoke PASS":"SavedInputsSmoke FAIL "+failures);
        return failures==0?0:1;
    }

    private static void Run(string name,Action test)
    {
        try { test();Console.WriteLine("PASS "+name); }
        catch(Exception ex) { failures++;Console.WriteLine("FAIL "+name+": "+ex.GetType().Name+" "+ex.Message); }
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Throws(Action action,string message)
    {
        try { action(); } catch(Exception) { return; }
        throw new Exception("expected failure: "+message);
    }
    private static string NewPath(string dir) { return Path.Combine(dir,Guid.NewGuid().ToString("N")+".json"); }

    private static SavedInputEntry Host(string pin=PinA)
    {
        return new SavedInputEntry { Role="host",ServerUrl=HostUrl,RelayUrl=RelayUrl,CertificateSha256=pin,ServerId="my-room",OwnerKey=OwnerKey,PasswordDraft=Draft };
    }
    private static SavedInputEntry GuestDraft(string alias="guest-room")
    {
        return new SavedInputEntry { Role="guest",RegistryUrl=RegistryUrl,RegistryCertificateSha256=PinB,RegistryIdentity="registry.example:1",ServerId=alias,PasswordDraft=Draft };
    }
    private static SavedInputEntry GuestResolved()
    {
        var e=GuestDraft("resolved-room");e.ServerUrl=HostUrl;e.RelayUrl=RelayUrl;e.CertificateSha256=PinA;e.VerifiedJoinPassword=Verified;
        return e;
    }
    private static string ProfileJson()
    {
        return "{\"Version\":1,\"Name\":\"Synthetic\",\"RendezvousUrl\":\""+HostUrl+"\",\"RelayUrl\":\""+RelayUrl+"\",\"CertificateSha256\":\""+PinA+"\"}";
    }
    private static SettingsState Registered()
    {
        return new SettingsState { ProfileJson=ProfileJson(),IsRegistered=true,DeviceId="1234567890",BearerToken="synthetic-bearer-token",
            TemporaryPassword="AbcdEfghI",PasswordExpiresAt="2099-01-01T00:00:00Z",BoundServerUrl=HostUrl,BoundCertificateSha256=PinA };
    }
    private static bool Same(SavedInputEntry a,SavedInputEntry b)
    {
        return a.Role==b.Role&&a.ServerUrl==b.ServerUrl&&a.RelayUrl==b.RelayUrl&&a.CertificateSha256==b.CertificateSha256&&
            a.RegistryUrl==b.RegistryUrl&&a.RegistryCertificateSha256==b.RegistryCertificateSha256&&a.RegistryIdentity==b.RegistryIdentity&&
            a.ServerId==b.ServerId&&a.OwnerKey==b.OwnerKey&&a.PasswordDraft==b.PasswordDraft&&a.VerifiedJoinPassword==b.VerifiedJoinPassword&&a.Remember==b.Remember;
    }
    private static void WriteProtectedPayload(string path,string payloadJson)
    {
        string blob=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(payloadJson),InputsEntropy,DataProtectionScope.CurrentUser));
        File.WriteAllText(path,"{\"Version\":2,\"ProfileJson\":\"\",\"ProtectedCredentials\":null,\"ProtectedInputs\":\""+blob+"\"}",new UTF8Encoding(false));
    }
    private static string EntryJson(SavedInputEntry e)
    {
        var d=new Dictionary<string,object> {
            {"Role",e.Role},{"ServerUrl",e.ServerUrl},{"RelayUrl",e.RelayUrl},{"CertificateSha256",e.CertificateSha256},{"RegistryUrl",e.RegistryUrl},
            {"RegistryCertificateSha256",e.RegistryCertificateSha256},{"RegistryIdentity",e.RegistryIdentity},{"ServerId",e.ServerId},{"OwnerKey",e.OwnerKey},
            {"PasswordDraft",e.PasswordDraft},{"VerifiedJoinPassword",e.VerifiedJoinPassword},{"Remember",e.Remember}
        };
        return Json.Serialize(d);
    }

    private static void RoundTrip(string dir)
    {
        string path=NewPath(dir);
        var state=Registered();
        state.SavedInputs.Add(Host());state.SavedInputs.Add(GuestDraft());state.SavedInputs.Add(GuestResolved());
        new SettingsStore(path).Save(state);
        string raw=File.ReadAllText(path);
        foreach(var secret in new[]{OwnerKey,Draft,Verified,"guest-room","my-room","synthetic-bearer-token"})
            Check(raw.IndexOf(secret,StringComparison.Ordinal)<0,"plaintext leaked into settings file");
        Check(raw.Contains("\"Version\":2")&&raw.Contains("\"ProtectedInputs\":\""),"v2 envelope expected");
        var loaded=new SettingsStore(path).Load();
        Check(loaded.IsRegistered&&loaded.DeviceId=="1234567890","device registration must still load");
        Check(loaded.SavedInputs.Count==3,"three entries expected");
        Check(Same(loaded.SavedInputs[0],Host())&&Same(loaded.SavedInputs[1],GuestDraft())&&Same(loaded.SavedInputs[2],GuestResolved()),"entries must round-trip exactly");
        var clone=loaded.SavedInputs[0].Clone();
        Check(!ReferenceEquals(clone,loaded.SavedInputs[0])&&Same(clone,loaded.SavedInputs[0]),"Clone must copy members");
    }

    private static void V1Read(string dir)
    {
        string path=NewPath(dir);
        File.WriteAllText(path,"{\"Version\":1,\"ProfileJson\":\"\",\"ProtectedCredentials\":null}",new UTF8Encoding(false));
        var loaded=new SettingsStore(path).Load();
        Check(!loaded.IsRegistered&&loaded.SavedInputs!=null&&loaded.SavedInputs.Count==0,"v1 empty load");
        // v1 with DPAPI registration using the unchanged profile entropy.
        string secrets=Json.Serialize(new Dictionary<string,string> {
            {"DeviceId","1234567890"},{"BearerToken","synthetic-bearer-token"},{"TemporaryPassword","AbcdEfghI"},
            {"PasswordExpiresAt","2099-01-01T00:00:00Z"},{"ServerUrl",HostUrl},{"CertificateSha256",PinA} });
        string blob=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secrets),Encoding.UTF8.GetBytes(HostUrl+"\n"+PinA),DataProtectionScope.CurrentUser));
        File.WriteAllText(path,Json.Serialize(new Dictionary<string,object>{{"Version",1},{"ProfileJson",ProfileJson()},{"ProtectedCredentials",blob}}),new UTF8Encoding(false));
        var store=new SettingsStore(path);
        loaded=store.Load();
        Check(loaded.IsRegistered&&loaded.DeviceId=="1234567890"&&loaded.SavedInputs.Count==0,"v1 registration load");
        loaded.SavedInputs.Add(GuestDraft());
        store.Save(loaded);
        var upgraded=new SettingsStore(path).Load();
        Check(upgraded.IsRegistered&&upgraded.SavedInputs.Count==1,"v1 upgraded to v2 on save");
        Throws(()=>{File.WriteAllText(path,"{\"Version\":1,\"ProfileJson\":\"\",\"ProtectedCredentials\":null,\"ProtectedInputs\":null}");new SettingsStore(path).Load();},"v1 with 4 fields");
        Throws(()=>{File.WriteAllText(path,"{\"Version\":2,\"ProfileJson\":\"\",\"ProtectedCredentials\":null}");new SettingsStore(path).Load();},"v2 with 3 fields");
    }

    private static void Tamper(string dir)
    {
        string path=NewPath(dir);
        var state=new SettingsState { ProfileJson="" };state.SavedInputs.Add(Host());
        new SettingsStore(path).Save(state);
        string raw=File.ReadAllText(path);
        int at=raw.IndexOf("\"ProtectedInputs\":\"",StringComparison.Ordinal)+19+40;
        char c=raw[at];
        string tampered=raw.Substring(0,at)+(c=='A'?'B':'A')+raw.Substring(at+1);
        File.WriteAllText(path,tampered,new UTF8Encoding(false));
        byte[] before=File.ReadAllBytes(path);
        var store=new SettingsStore(path);
        Throws(()=>store.Load(),"tampered ProtectedInputs");
        Throws(()=>store.Save(new SettingsState{ProfileJson=""}),"save after load failure");
        Throws(()=>store.ClearCredentials(),"clear after load failure");
        Check(Convert.ToBase64String(File.ReadAllBytes(path))==Convert.ToBase64String(before),"original must be preserved");
        // Encrypted with the credential purpose instead of the saved-input purpose must also fail.
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(EntryJson(Host()))+"}");
        Check(new SettingsStore(path).Load().SavedInputs.Count==1,"control payload loads");
        string wrongPurpose=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Context0\":"+Json.Serialize(EntryJson(Host()))+"}"),Encoding.UTF8.GetBytes(HostUrl+"\n"+PinA),DataProtectionScope.CurrentUser));
        File.WriteAllText(path,"{\"Version\":2,\"ProfileJson\":\"\",\"ProtectedCredentials\":null,\"ProtectedInputs\":\""+wrongPurpose+"\"}");
        Throws(()=>new SettingsStore(path).Load(),"wrong purpose entropy");
    }

    private static void WrongPin()
    {
        var list=new List<SavedInputEntry>{ Host(PinA),GuestDraft() };
        Check(SettingsStore.FindSavedInput(list,new SavedInputEntry{Role="host",ServerUrl=HostUrl,RelayUrl=RelayUrl,CertificateSha256=PinA.ToLowerInvariant()})!=null,"same context matches case-insensitive pin");
        Check(SettingsStore.FindSavedInput(list,new SavedInputEntry{Role="host",ServerUrl=HostUrl,RelayUrl=RelayUrl,CertificateSha256=PinB})==null,"same URL wrong pin must not match");
        Check(SettingsStore.FindSavedInput(list,new SavedInputEntry{Role="guest",RegistryUrl=RegistryUrl,RegistryCertificateSha256=PinA,RegistryIdentity="registry.example:1",ServerId="guest-room"})==null,"guest wrong registry pin");
        Check(SettingsStore.FindSavedInput(list,new SavedInputEntry{Role="guest",RegistryUrl=RegistryUrl,RegistryCertificateSha256=PinB,RegistryIdentity="registry.example:1",ServerId="other-room"})==null,"guest other alias");
        Check(SettingsStore.FindSavedInput(list,new SavedInputEntry{Role="host",ServerUrl=HostUrl,RelayUrl=RelayUrl,CertificateSha256=PinA}).OwnerKey==OwnerKey,"match returns entry");
    }

    private static void MaxEntries(string dir)
    {
        string path=NewPath(dir);
        var state=new SettingsState { ProfileJson="" };
        for(int i=0;i<16;i++) state.SavedInputs.Add(GuestDraft("room-"+i.ToString("00")));
        new SettingsStore(path).Save(state);
        byte[] before=File.ReadAllBytes(path);
        var store=new SettingsStore(path);
        var loaded=store.Load();
        Check(loaded.SavedInputs.Count==16,"16 entries load");
        loaded.SavedInputs.Add(GuestDraft("room-16"));
        Throws(()=>store.Save(loaded),"17 entries");
        loaded.SavedInputs.RemoveAt(16);loaded.SavedInputs[15]=GuestDraft("room-00");
        Throws(()=>store.Save(loaded),"duplicate context");
        Check(Convert.ToBase64String(File.ReadAllBytes(path))==Convert.ToBase64String(before),"rejected saves preserve file");
        var payload=new StringBuilder("{");
        for(int i=0;i<17;i++) payload.Append(i==0?"":",").Append("\"Context"+i+"\":").Append(Json.Serialize(EntryJson(GuestDraft("room-"+i.ToString("00")))));
        WriteProtectedPayload(path,payload.Append("}").ToString());
        Throws(()=>new SettingsStore(path).Load(),"17 entries on disk");
    }

    private static void Duplicates(string dir)
    {
        string path=NewPath(dir);
        File.WriteAllText(path,"{\"Version\":2,\"Version\":2,\"ProfileJson\":\"\",\"ProtectedCredentials\":null,\"ProtectedInputs\":null}");
        Throws(()=>new SettingsStore(path).Load(),"duplicate envelope key");
        File.WriteAllText(path,"{\"Version\":2,\"ProfileJson\":\"\",\"ProtectedCredentials\":null,\"Protected\\u0049nputs\":null,\"ProtectedInputs\":null}");
        Throws(()=>new SettingsStore(path).Load(),"escaped duplicate envelope key");
        string entry=EntryJson(Host());
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(entry)+",\"Context0\":"+Json.Serialize(entry)+"}");
        Throws(()=>new SettingsStore(path).Load(),"duplicate payload key");
        string dupEntry=entry.Insert(1,"\"Role\":\"host\",");
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(dupEntry)+"}");
        Throws(()=>new SettingsStore(path).Load(),"duplicate entry field");
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(entry.Insert(1,"\"Extra\":null,"))+"}");
        Throws(()=>new SettingsStore(path).Load(),"unknown entry field");
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(entry.Replace("\"Remember\":true","\"Remember\":\"true\""))+"}");
        Throws(()=>new SettingsStore(path).Load(),"Remember wrong type");
        WriteProtectedPayload(path,"{\"Context1\":"+Json.Serialize(entry)+"}");
        Throws(()=>new SettingsStore(path).Load(),"non-contiguous context key");
        WriteProtectedPayload(path,"{\"Context0\":"+Json.Serialize(entry)+",\"Context1\":"+Json.Serialize(entry)+"}");
        Throws(()=>new SettingsStore(path).Load(),"duplicate context on disk");
    }

    private static void TombstoneAndClear(string dir)
    {
        string path=NewPath(dir);
        var state=Registered();
        var withSecret=Host();withSecret.Remember=false;
        state.SavedInputs.Add(withSecret);
        Throws(()=>new SettingsStore(path).Save(state),"tombstone with secrets");
        Check(!File.Exists(path),"no file written on rejected save");
        var tomb=new SavedInputEntry{Role="host",ServerUrl=HostUrl,RelayUrl=RelayUrl,CertificateSha256=PinA,Remember=false,OwnerKey="",PasswordDraft=""};
        state.SavedInputs.Clear();state.SavedInputs.Add(tomb);state.SavedInputs.Add(GuestResolved());
        var store=new SettingsStore(path);
        store.Save(state);
        Check(tomb.OwnerKey=="","caller entry must not be mutated");
        var loaded=new SettingsStore(path).Load();
        Check(loaded.IsRegistered,"device preserved alongside tombstone");
        var t=loaded.SavedInputs[0];
        Check(!t.Remember&&t.OwnerKey==null&&t.PasswordDraft==null&&t.VerifiedJoinPassword==null,"tombstone has no secrets");
        new SettingsStore(path).ClearCredentials();
        var cleared=new SettingsStore(path).Load();
        Check(!cleared.IsRegistered&&cleared.DeviceId==null,"device cleared");
        Check(cleared.SavedInputs.Count==2&&Same(cleared.SavedInputs[1],GuestResolved()),"ClearCredentials preserves saved inputs");
        // Save writes exactly the caller list (no merge from disk).
        cleared.SavedInputs.RemoveAt(1);
        new SettingsStore(path).Save(cleared);
        Check(new SettingsStore(path).Load().SavedInputs.Count==1,"forgotten entry not merged back");
        Throws(()=>new SettingsStore(path).Save(new SettingsState{ProfileJson="",SavedInputs=null}),"null list rejected");
    }

    private static void BoundedFailure(string dir)
    {
        string path=NewPath(dir);var state=Registered();state.SavedInputs.Add(Host());
        new SettingsStore(path).Save(state);byte[] before=File.ReadAllBytes(path);
        var lockedStore=new SettingsStore(path);
        using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        { Throws(()=>lockedStore.Load(),"locked read"); }
        Throws(()=>lockedStore.Save(new SettingsState{ProfileJson=""}),"write after failed read");
        Check(Convert.ToBase64String(before)==Convert.ToBase64String(File.ReadAllBytes(path)),"locked read preserved");
        File.WriteAllBytes(path,new byte[65537]);var oversized=new SettingsStore(path);
        Throws(()=>oversized.Load(),"oversized file");Throws(()=>oversized.Save(state),"oversized original overwrite");
        Check(new FileInfo(path).Length==65537,"oversized original preserved");
        var huge=new SettingsState{ProfileJson=""};
        for(int i=0;i<16;i++){var e=Host();e.ServerUrl="https://"+new string('a',1900)+i+".test/";e.RelayUrl=e.ServerUrl;huge.SavedInputs.Add(e);}
        string target=NewPath(dir);new SettingsStore(target).Save(state);before=File.ReadAllBytes(target);
        Throws(()=>new SettingsStore(target).Save(huge),"oversized save");
        Check(Convert.ToBase64String(before)==Convert.ToBase64String(File.ReadAllBytes(target)),"oversized save preserved");
    }

    private static void InvalidEntries(string dir)
    {
        string path=NewPath(dir);
        var bad=new List<Func<SavedInputEntry>> {
            ()=>{var e=Host();e.Role="Host";return e;},
            ()=>{var e=Host();e.RelayUrl=null;return e;},
            ()=>{var e=Host();e.ServerUrl="https://host.example.test/path";return e;},
            ()=>{var e=Host();e.ServerUrl="http://host.example.test/";return e;},
            ()=>{var e=Host();e.CertificateSha256=PinA.Substring(1);return e;},
            ()=>{var e=Host();e.ServerId="My-Room";return e;},
            ()=>{var e=Host();e.ServerId="ab";return e;},
            ()=>{var e=Host();e.OwnerKey=new string('k',31);return e;},
            ()=>{var e=Host();e.OwnerKey=new string('k',1025);return e;},
            ()=>{var e=Host();e.PasswordDraft="short7!";return e;},
            ()=>{var e=Host();e.PasswordDraft=new string('p',129);return e;},
            ()=>{var e=Host();e.RegistryUrl=RegistryUrl;return e;},
            ()=>{var e=GuestDraft();e.OwnerKey=OwnerKey;return e;},
            ()=>{var e=GuestDraft();e.ServerId=null;return e;},
            ()=>{var e=GuestDraft();e.RegistryIdentity="bad identity";return e;},
            ()=>{var e=GuestDraft();e.ServerUrl=HostUrl;return e;},
            ()=>{var e=GuestDraft();e.VerifiedJoinPassword="1234567";return e;},
        };
        for(int i=0;i<bad.Count;i++)
        {
            var state=new SettingsState{ProfileJson=""};state.SavedInputs.Add(bad[i]());
            int index=i;
            Throws(()=>new SettingsStore(path).Save(state),"invalid entry #"+index);
        }
        Check(!File.Exists(path),"no file written for invalid entries");
        // Boundary values accepted and preserved without normalization.
        var ok=Host();ok.OwnerKey="  "+new string('k',28)+"  ";ok.PasswordDraft=" Pw  8ch";ok.VerifiedJoinPassword=new string('v',128);
        var s=new SettingsState{ProfileJson=""};s.SavedInputs.Add(ok);
        new SettingsStore(path).Save(s);
        var back=new SettingsStore(path).Load().SavedInputs[0];
        Check(back.OwnerKey==ok.OwnerKey&&back.PasswordDraft==ok.PasswordDraft&&back.VerifiedJoinPassword==ok.VerifiedJoinPassword,"secrets stored raw");
    }
}
