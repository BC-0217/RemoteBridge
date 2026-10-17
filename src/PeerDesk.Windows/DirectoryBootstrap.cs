using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    // Public, non-secret registry trust anchor. Embedded in the executable; a directory-bootstrap.json sidecar next to it overrides.
    internal sealed class DirectoryBootstrap
    {
        public const string SidecarFileName="directory-bootstrap.json";
        public string RegistryUrl { get; set; }
        public string CertificateSha256 { get; set; }
        public string RegistryIdentity { get; set; }

        public DirectoryBootstrap() { }
        public DirectoryBootstrap(string registryUrl,string certificateSha256,string registryIdentity)
        {
            RegistryUrl=registryUrl;CertificateSha256=certificateSha256;RegistryIdentity=registryIdentity;
        }

        public const string EmbeddedResourceName="PeerDesk.Windows.DirectoryBootstrap.json";
        private const int MaxBytes=8192;

        // Sidecar next to the executable overrides the embedded anchor. Only a missing sidecar falls back to the
        // embedded resource; an existing sidecar that cannot be read or is invalid returns null (guest join fails closed).
        public static DirectoryBootstrap LoadDefault()
        {
            return LoadDefaultFromDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        internal static DirectoryBootstrap LoadDefaultFromDirectory(string directory)
        {
            try { return Load(Path.Combine(directory,SidecarFileName)); }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
            catch { return null; }
            try { return LoadEmbedded(); }
            catch { return null; }
        }

        public static DirectoryBootstrap Load(string path)
        {
            using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                return Load(fs);
        }

        private static DirectoryBootstrap LoadEmbedded()
        {
            using(var stream=typeof(DirectoryBootstrap).Assembly.GetManifestResourceStream(EmbeddedResourceName))
            {
                if(stream==null) throw new InvalidDataException("내장 디렉터리 부트스트랩이 없습니다.");
                return Load(stream);
            }
        }

        private static DirectoryBootstrap Load(Stream stream)
        {
            // Read at most MaxBytes+1 regardless of reported length so a growing or unseekable stream cannot exceed the cap.
            var buffer=new byte[MaxBytes+1];int read=0;
            while(read<buffer.Length){int n=stream.Read(buffer,read,buffer.Length-read);if(n==0)break;read+=n;}
            if(read>MaxBytes) throw new InvalidDataException("디렉터리 부트스트랩 파일이 너무 큽니다.");
            string text=new UTF8Encoding(false,true).GetString(buffer,0,read);
            var serializer=new JavaScriptSerializer { MaxJsonLength=8192 };
            ServerProfile.ValidateFlatObjectKeys(text,serializer);
            var data=serializer.Deserialize<Dictionary<string,object>>(text);
            if(data==null) throw new InvalidDataException("디렉터리 부트스트랩 형식이 잘못되었습니다.");
            foreach(var key in data.Keys)
                if(key!="RegistryUrl"&&key!="CertificateSha256"&&key!="RegistryIdentity") throw new InvalidDataException("알 수 없는 부트스트랩 필드: "+key);
            var b=new DirectoryBootstrap(Str(data,"RegistryUrl"),Str(data,"CertificateSha256"),Str(data,"RegistryIdentity"));
            b.Validate();
            return b;
        }

        public void Validate()
        {
            Uri uri;
            if(String.IsNullOrWhiteSpace(RegistryUrl)||!Uri.TryCreate(RegistryUrl,UriKind.Absolute,out uri)||uri.Scheme!=Uri.UriSchemeHttps||
               !String.IsNullOrEmpty(uri.UserInfo)||!String.IsNullOrEmpty(uri.Query)||!String.IsNullOrEmpty(uri.Fragment)||RegistryUrl.Length>2048||uri.AbsolutePath!="/")
                throw new InvalidDataException("레지스트리 주소는 https 절대 주소여야 합니다.");
            if(CertificateSha256==null||!Regex.IsMatch(CertificateSha256,"\\A[0-9A-Fa-f]{64}\\z"))
                throw new InvalidDataException("레지스트리 인증서 지문은 SHA-256 64자리여야 합니다.");
            if(String.IsNullOrWhiteSpace(RegistryIdentity)||RegistryIdentity.Length>128||!Regex.IsMatch(RegistryIdentity,"\\A[A-Za-z0-9._:-]+\\z"))
                throw new InvalidDataException("레지스트리 ID가 잘못되었습니다.");
        }

        private static string Str(Dictionary<string,object> d,string key)
        {
            object v;
            if(!d.TryGetValue(key,out v)||!(v is string)) throw new InvalidDataException("부트스트랩 필드가 잘못되었습니다: "+key);
            return (string)v;
        }
    }
}
