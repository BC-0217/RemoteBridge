using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    // Public directory lookups/publication over the pinned registry transport. No bearer auth, no secrets persisted.
    internal sealed class DirectoryClient
    {
        private readonly DirectoryBootstrap bootstrap;
        private readonly ControlPlaneClient registry;

        public DirectoryClient(DirectoryBootstrap bootstrap)
        {
            if(bootstrap==null) throw new InvalidOperationException("디렉터리 부트스트랩이 없습니다. 게스트 참가를 사용할 수 없습니다.");
            bootstrap.Validate();
            this.bootstrap=bootstrap;
            var data=new Dictionary<string,object> { {"Version",1},{"Name","PeerDesk Directory"},{"RendezvousUrl",bootstrap.RegistryUrl},{"RelayUrl",bootstrap.RegistryUrl},{"CertificateSha256",bootstrap.CertificateSha256} };
            registry=new ControlPlaneClient(ServerProfile.Parse(new JavaScriptSerializer().Serialize(data)));
        }

        public static bool IsValidAlias(string alias) { return alias!=null&&Regex.IsMatch(alias,"\\A[a-z0-9][a-z0-9-]{1,30}[a-z0-9]\\z"); }

        public async Task<ServerProfile> LookupAsync(string alias)
        {
            if(!IsValidAlias(alias)) throw new ArgumentException("서버 별칭은 소문자 영문/숫자/하이픈 3~32자여야 합니다.","alias");
            var d=await registry.SendPublicJsonAsync("GET","/api/v1/directory/"+alias,null).ConfigureAwait(false);
            return ParseEntry(d,alias);
        }

        public async Task<string> PublishAsync(string grant,ControlPlaneClient host,string ownerSecret)
        {
            if(String.IsNullOrWhiteSpace(grant)||grant.Length>512) throw new ArgumentException("게시 승인 코드를 입력하십시오.","grant");
            if(host==null) throw new ArgumentNullException("host");
            var challenge=await registry.SendPublicJsonAsync("POST","/api/v1/directory/challenge",new Dictionary<string,object> { {"grant",grant} }).ConfigureAwait(false);
            object identity,alias;
            if(!challenge.TryGetValue("registryIdentity",out identity)||!String.Equals(identity as string,bootstrap.RegistryIdentity,StringComparison.Ordinal))
                throw new InvalidOperationException("레지스트리 ID가 부트스트랩과 일치하지 않습니다.");
            if(!challenge.TryGetValue("alias",out alias)||!IsValidAlias(alias as string))
                throw new InvalidOperationException("레지스트리 챌린지 별칭이 잘못되었습니다.");
            // Forward the challenge unchanged; the host server signs it, Windows never holds the private key.
            var proof=await host.OwnerProofAsync(ownerSecret,challenge).ConfigureAwait(false);
            var published=await registry.SendPublicJsonAsync("POST","/api/v1/directory/publish",proof).ConfigureAwait(false);
            ParseEntry(published,(string)alias);
            return (string)alias;
        }

        private static ServerProfile ParseEntry(Dictionary<string,object> d,string alias)
        {
            object a,p,v;
            if(!d.TryGetValue("alias",out a)||!String.Equals(a as string,alias,StringComparison.Ordinal)) throw new InvalidOperationException("디렉터리 응답 별칭이 일치하지 않습니다.");
            if(!d.TryGetValue("version",out v)||!(v is int)||(int)v!=1) throw new InvalidOperationException("디렉터리 응답 버전이 잘못되었습니다.");
            if(!d.TryGetValue("profileJson",out p)||!(p is string)) throw new InvalidOperationException("디렉터리 응답 프로필이 없습니다.");
            return ServerProfile.Parse((string)p);
        }

        // Canonical JSON for the profile returned by the directory (stored as the bound profile after a successful join).
        public static string ProfileJson(ServerProfile p)
        {
            var data=new Dictionary<string,object> { {"Version",1},{"Name",p.Name},{"RendezvousUrl",p.RendezvousUrl},{"RelayUrl",p.RelayUrl},{"CertificateSha256",p.CertificateSha256} };
            return new JavaScriptSerializer().Serialize(data);
        }
    }
}
