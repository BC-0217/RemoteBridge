using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
namespace PeerDesk.Windows {
    internal sealed class TrustedPeer {
        public string ServerUrl {get;set;} public string ServerPin {get;set;} public string PeerId {get;set;} public string IdentityPin {get;set;}
        internal TrustedPeer Clone(){return (TrustedPeer)MemberwiseClone();}
        public override string ToString(){return PeerId+" · "+new Uri(ServerUrl).Authority+" · "+IdentityPin.Substring(0,12)+"…";}
    }
    // User-approved identity pins, not server-delivered device labels. DPAPI also protects local integrity.
    internal sealed class PeerTrustStore {
        readonly string path;readonly List<TrustedPeer> peers=new List<TrustedPeer>();
        static readonly byte[] Entropy=Encoding.UTF8.GetBytes("RemoteBridge/trusted-peers/v1");
        readonly JavaScriptSerializer json=new JavaScriptSerializer {MaxJsonLength=65536,RecursionLimit=8};
        bool invalid;internal string Warning {get;private set;}
        internal PeerTrustStore(string path){
            this.path=path;if(!File.Exists(path))return;
            byte[] protectedBytes=null,clear=null;
            try{
                using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){if(s.Length<1||s.Length>131072)throw new InvalidDataException("Trust store bound.");
                    protectedBytes=new byte[(int)s.Length];int o=0;while(o<protectedBytes.Length){int n=s.Read(protectedBytes,o,protectedBytes.Length-o);if(n==0)throw new EndOfStreamException();o+=n;}}
                clear=ProtectedData.Unprotect(protectedBytes,Entropy,DataProtectionScope.CurrentUser);
                if(clear.Length>65536)throw new InvalidDataException("Trust store bound.");
                var loaded=json.Deserialize<List<TrustedPeer>>(new UTF8Encoding(false,true).GetString(clear));
                if(loaded==null||loaded.Count>100)throw new InvalidDataException("Trust store peer count.");
                foreach(var p in loaded){Validate(p);if(loaded.Count(x=>Same(x,p.ServerUrl,p.ServerPin,p.PeerId))!=1)throw new InvalidDataException("Duplicate trusted peer.");}
                peers.AddRange(loaded);
            }catch(Exception ex){invalid=true;Warning="PC 신뢰 정보를 읽지 못했습니다. 원본을 보존하며 보안 연결을 시작하지 않습니다: "+ex.Message;}
            finally{if(clear!=null)Array.Clear(clear,0,clear.Length);if(protectedBytes!=null)Array.Clear(protectedBytes,0,protectedBytes.Length);}
        }
        internal string Find(string server,string pin,string peer){DemandValid();var p=peers.FirstOrDefault(x=>Same(x,server,pin,peer));return p==null?null:p.IdentityPin;}
        internal IList<TrustedPeer> List(){DemandValid();return peers.Select(x=>x.Clone()).ToList();}
        internal void Trust(string server,string pin,string peer,string identity){
            DemandValid();var value=new TrustedPeer{ServerUrl=server,ServerPin=pin,PeerId=peer,IdentityPin=identity};Validate(value);
            var existing=peers.FirstOrDefault(x=>Same(x,server,pin,peer));
            if(existing!=null){if(!String.Equals(existing.IdentityPin,identity,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("상대 PC의 보안 키가 변경되었습니다. 기존 신뢰를 자동 교체하지 않습니다.");return;}
            if(peers.Count>=100)throw new InvalidOperationException("신뢰한 PC는 최대 100대입니다.");
            var next=peers.Select(x=>x.Clone()).ToList();next.Add(value);Save(next);peers.Add(value);
        }
        internal void Forget(string server,string pin,string peer){
            DemandValid();var next=peers.Where(x=>!Same(x,server,pin,peer)).Select(x=>x.Clone()).ToList();Save(next);peers.Clear();peers.AddRange(next);
        }
        void DemandValid(){if(invalid)throw new InvalidDataException(Warning);}
        static bool Same(TrustedPeer p,string server,string pin,string peer){return p.ServerUrl==server&&String.Equals(p.ServerPin,pin,StringComparison.OrdinalIgnoreCase)&&p.PeerId==peer;}
        static void Validate(TrustedPeer p){
            Uri u;if(p==null||p.ServerUrl==null||p.ServerUrl.Length>2048||!Uri.TryCreate(p.ServerUrl,UriKind.Absolute,out u)||u.Scheme!="https"||u.UserInfo.Length>0||u.AbsolutePath!="/"||u.Query.Length>0||u.Fragment.Length>0||
                !IsPin(p.ServerPin)||!IsPin(p.IdentityPin)||!CredentialFormat.IsStoredDeviceId(p.PeerId))throw new InvalidDataException("Invalid trusted peer identity.");
        }
        static bool IsPin(string value){return value!=null&&value.Length==64&&value.All(Uri.IsHexDigit);}
        void Save(List<TrustedPeer> list){
            byte[] clear=Encoding.UTF8.GetBytes(json.Serialize(list)),bytes=null;
            try{
                if(clear.Length>65536)throw new InvalidDataException("Trust store bound.");
                bytes=ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{using(var s=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){s.Write(bytes,0,bytes.Length);s.Flush(true);}
                    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
                finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
            }finally{Array.Clear(clear,0,clear.Length);if(bytes!=null)Array.Clear(bytes,0,bytes.Length);}
        }
    }
}
