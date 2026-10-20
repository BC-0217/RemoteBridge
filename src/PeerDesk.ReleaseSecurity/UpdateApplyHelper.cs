using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace PeerDesk.ReleaseSecurity
{
    public sealed class UpdateApplyPolicy
    {
        internal readonly string DirectoryPath,Destination,FloorPath,KeyId,Channel,Platform,KeyFingerprint;
        internal readonly byte[] ManifestHash;
        internal readonly RSAParameters PublicKey;
        internal readonly Version Os;
        internal readonly int Schema;
        // Must be constructed from the installed application's trusted release anchor,
        // never from public-key fields supplied by a downloaded bundle or helper job.
        public UpdateApplyPolicy(string installDirectory,string fixedExecutableName,string floorPath,
            RSAParameters trustedPublicKey,string keyId,string channel,string platform,
            byte[] approvedManifestSha256,Version os,int schema)
        {
            DirectoryPath=Path.GetFullPath(installDirectory);
            if(fixedExecutableName!="RemoteBridge.exe"||approvedManifestSha256==null||approvedManifestSha256.Length!=32||
                trustedPublicKey.Modulus==null||trustedPublicKey.Modulus.Length<256||trustedPublicKey.Exponent==null)
                throw new InvalidDataException("Release anchor or fixed executable policy is unavailable.");
            Destination=Path.Combine(DirectoryPath,fixedExecutableName);FloorPath=Path.GetFullPath(floorPath);
            PublicKey=new RSAParameters {Modulus=(byte[])trustedPublicKey.Modulus.Clone(),Exponent=(byte[])trustedPublicKey.Exponent.Clone()};
            KeyId=keyId;Channel=channel;Platform=platform;ManifestHash=(byte[])approvedManifestSha256.Clone();Os=os;Schema=schema;
            using(var sha=SHA256.Create()){
                byte[] bytes=new byte[PublicKey.Modulus.Length+PublicKey.Exponent.Length];
                Buffer.BlockCopy(PublicKey.Modulus,0,bytes,0,PublicKey.Modulus.Length);
                Buffer.BlockCopy(PublicKey.Exponent,0,bytes,PublicKey.Modulus.Length,PublicKey.Exponent.Length);
                KeyFingerprint=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
            }
        }
        public UpdateFloorStore OpenFloor() { return new UpdateFloorStore(FloorPath,Destination,KeyFingerprint); }
    }
    public sealed class UpdateApplyJob
    {
        public byte[] Manifest,Signature;
        public string StagedBasename;
        public int ParentProcessId;
        public long ParentStartUtcTicks;
    }
    public static class UpdateApplyHelper
    {
        // Does not kill, elevate, execute the new binary, or choose arbitrary destinations.
        public static string Apply(UpdateApplyPolicy trustedPolicy,UpdateApplyJob job,CancellationToken ct)
        {
            if(trustedPolicy==null||job==null||job.Manifest==null||job.Signature==null||
                job.ParentProcessId<=0||job.ParentStartUtcTicks<=0)throw new InvalidDataException("Invalid update job.");
            byte[] manifest=(byte[])job.Manifest.Clone(),signature=(byte[])job.Signature.Clone();
            string stageName=job.StagedBasename;
            if(String.IsNullOrEmpty(stageName)||!stageName.StartsWith(".remotebridge-stage-",StringComparison.Ordinal)||
                stageName!=Path.GetFileName(stageName)||stageName.IndexOfAny(new[]{':','/','\\'})>=0)
                throw new InvalidDataException("Invalid staged artifact.");
            using(var sha=SHA256.Create())
                if(!ReleaseManifest.Equal(sha.ComputeHash(manifest),trustedPolicy.ManifestHash))
                    throw new InvalidDataException("Helper manifest does not match approved request.");
            VerifiedRelease Verify() { return SignedReleaseVerifier.Verify(manifest,signature,trustedPolicy.PublicKey,
                trustedPolicy.KeyId,trustedPolicy.Channel,trustedPolicy.Platform,DateTimeOffset.UtcNow,trustedPolicy.Os,trustedPolicy.Schema); }
            var release=Verify();var store=trustedPolicy.OpenFloor();store.LoadRequired().DemandCandidate(release);
            WaitForExactParent(job.ParentProcessId,job.ParentStartUtcTicks,ct);
            ct.ThrowIfCancellationRequested();release=Verify();
            string stage=Path.Combine(trustedPolicy.DirectoryPath,stageName);
            string backup=Path.Combine(trustedPolicy.DirectoryPath,".remotebridge-known-good-"+Guid.NewGuid().ToString("N"));
            UpdateFiles.Apply(release,stage,trustedPolicy.Destination,backup,store.LoadRequired(),floor => store.Commit(release));
            return backup;
        }
        static void WaitForExactParent(int pid,long expectedStart,CancellationToken ct)
        {
            if(pid==Process.GetCurrentProcess().Id)throw new InvalidDataException("Helper cannot replace itself.");
            Process parent;
            try{parent=Process.GetProcessById(pid);}catch(ArgumentException){return;}
            using(parent){
                if(parent.StartTime.ToUniversalTime().Ticks!=expectedStart)throw new InvalidDataException("Parent process identity changed.");
                var timer=Stopwatch.StartNew();
                while(!parent.WaitForExit(50)){
                    ct.ThrowIfCancellationRequested();
                    if(timer.ElapsedMilliseconds>=30000)throw new TimeoutException("Parent is still running; update not applied.");
                }
            }
        }
    }
}
