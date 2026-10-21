using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
namespace PeerDesk.Windows {
    internal sealed class PeerBookmark {
        public string ServerUrl {get;set;} public string Pin {get;set;} public string DeviceId {get;set;}
        public string Alias {get;set;} public bool Favorite {get;set;}
        internal PeerBookmark Clone(){return (PeerBookmark)MemberwiseClone();}
    }
    // Cosmetic choices only. Nothing loaded here is a credential, session approval, or trusted identity.
    internal sealed class UserExperienceOptions {
        readonly string path; readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=65536};
        readonly List<PeerBookmark> peers=new List<PeerBookmark>(); bool invalid;
        internal QualityMode Quality {get;private set;}
        internal string Warning {get;private set;}
        internal string Language {get;private set;} = "ko";
        internal bool Dark {get;private set;}
        internal void SetAppearance(string language,bool dark){
            if(!new[]{"ko","en","ja","zh-CN","ru","fr","hi"}.Contains(language))throw new ArgumentException("Unsupported language.");
            Save(Quality,peers,language,dark);Language=language;Dark=dark;
        }
        internal UserExperienceOptions(string file){
            path=file;Quality=QualityMode.Auto;
            if(!File.Exists(path))return;
            try {
                byte[] bytes;using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                    if(s.Length<1||s.Length>65536)throw new InvalidDataException("Options size invalid.");
                    bytes=new byte[(int)s.Length];int o=0;while(o<bytes.Length){int n=s.Read(bytes,o,bytes.Length-o);if(n==0)throw new EndOfStreamException();o+=n;}
                }
                var text=new UTF8Encoding(false,true).GetString(bytes);ServerProfile.ValidateFlatObjectKeys(text,json);
                var root=json.Deserialize<Dictionary<string,object>>(text);
                if(!root.ContainsKey("version")||!(root["version"] is int)||!((int)root["version"]==1&&root.Count==3||(int)root["version"]==2&&root.Count==5)||
                   !root.ContainsKey("quality")||!(root["quality"] is int)||!Enum.IsDefined(typeof(QualityMode),(int)root["quality"])||!root.ContainsKey("peers")||!(root["peers"] is string))
                    throw new InvalidDataException("Options schema invalid.");
                var loaded=json.Deserialize<List<PeerBookmark>>((string)root["peers"]);
                if(loaded==null||loaded.Count>50)throw new InvalidDataException("Too many peers.");
                foreach(var peer in loaded){Validate(peer);if(loaded.Count(x=>Same(x,peer.ServerUrl,peer.Pin,peer.DeviceId))!=1)throw new InvalidDataException("Duplicate peer.");}
                if((int)root["version"]==2){
                    if(!root.ContainsKey("language")||!(root["language"] is string)||!new[]{"ko","en","ja","zh-CN","ru","fr","hi"}.Contains((string)root["language"])||!root.ContainsKey("dark")||!(root["dark"] is bool))throw new InvalidDataException("Appearance schema invalid.");
                    Language=(string)root["language"];Dark=(bool)root["dark"];
                }
                Quality=(QualityMode)(int)root["quality"];peers.AddRange(loaded);
            } catch(Exception ex){invalid=true;Warning=UiLocalization.Text("편의 설정을 읽지 못해 원본을 보존합니다: ")+ex.Message;}
        }
        internal PeerBookmark Find(string url,string pin,string id){var b=peers.FirstOrDefault(x=>Same(x,url,pin,id));return b==null?null:b.Clone();}
        internal void SetQuality(QualityMode q){if(!Enum.IsDefined(typeof(QualityMode),q))throw new ArgumentOutOfRangeException("q");Save(q,peers);Quality=q;}
        internal void Set(string url,string pin,string id,string alias,bool favorite){
            var item=new PeerBookmark{ServerUrl=url,Pin=pin,DeviceId=id,Alias=alias??"",Favorite=favorite};Validate(item);
            var next=peers.Where(x=>!Same(x,url,pin,id)).Select(x=>x.Clone()).ToList();
            if(item.Alias.Length>0||favorite)next.Add(item);
            if(next.Count>50)throw new InvalidDataException(UiLocalization.Text("즐겨찾기·별명은 최대 50개입니다."));
            Save(Quality,next);peers.Clear();peers.AddRange(next);
        }
        static bool Same(PeerBookmark p,string u,string h,string id){return p.ServerUrl==u&&p.Pin==h&&p.DeviceId==id;}
        static void Validate(PeerBookmark p){
            Uri uri;
            if(p==null||p.ServerUrl==null||p.ServerUrl.Length>2048||!Uri.TryCreate(p.ServerUrl,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new InvalidDataException("Invalid peer server.");
            if(p.Pin==null||p.Pin.Length!=64||p.Pin.Any(c=>!Uri.IsHexDigit(c))||!CredentialFormat.IsStoredDeviceId(p.DeviceId)||p.Alias==null||p.Alias.Length>40||p.Alias.Any(Char.IsControl))throw new InvalidDataException("Invalid peer label.");
        }
        void Save(QualityMode q,List<PeerBookmark> list){Save(q,list,Language,Dark);}
        void Save(QualityMode q,List<PeerBookmark> list,string language,bool dark){
            if(invalid)throw new InvalidDataException(UiLocalization.Text("손상된 편의 설정 원본을 덮어쓰지 않습니다."));
            var bytes=new UTF8Encoding(false).GetBytes(json.Serialize(new{version=2,quality=(int)q,peers=json.Serialize(list),language=language,dark=dark}));
            if(bytes.Length>65536)throw new InvalidDataException(UiLocalization.Text("편의 설정은 64KiB 이하만 저장합니다."));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var s=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){s.Write(bytes,0,bytes.Length);s.Flush(true);}
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
        }
    }
}
