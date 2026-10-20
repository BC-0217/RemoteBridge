using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    internal sealed class SessionTransportDescriptor
    {
        internal SessionTransportDescriptor(string sid, string initiator, string target, string epoch,
            long generation, int bits, int permissionRevision, int geometryVersion)
        {
            SessionId=sid; InitiatorDeviceId=initiator; TargetDeviceId=target; TransportEpoch=epoch;
            SharingGeneration=generation; PermissionBits=bits; PermissionRevision=permissionRevision; GeometryVersion=geometryVersion;
        }
        // Strict capture v1 descriptor: all four fields are present together with exact values, or all absent (legacy 8-field shape).
        internal SessionTransportDescriptor(string sid, string initiator, string target, string epoch,
            long generation, int bits, int permissionRevision, int geometryVersion,
            int captureLeaseVersion, int captureLeaseMs, int minFrameIntervalMs, int transportKeepaliveVersion)
            : this(sid,initiator,target,epoch,generation,bits,permissionRevision,geometryVersion)
        {
            CaptureLeaseVersion=captureLeaseVersion; CaptureLeaseMs=captureLeaseMs; MinFrameIntervalMs=minFrameIntervalMs; TransportKeepaliveVersion=transportKeepaliveVersion;
        }
        public string SessionId { get; private set; }
        public string InitiatorDeviceId { get; private set; }
        public string TargetDeviceId { get; private set; }
        public string TransportEpoch { get; private set; }
        public long SharingGeneration { get; private set; }
        public int PermissionBits { get; private set; }
        public int PermissionRevision { get; private set; }
        public int GeometryVersion { get; private set; }
        public int CaptureLeaseVersion { get; private set; }
        public int CaptureLeaseMs { get; private set; }
        public int MinFrameIntervalMs { get; private set; }
        public int TransportKeepaliveVersion { get; private set; }
        public bool IsStrictCaptureV1 { get { return CaptureLeaseVersion==1&&CaptureLeaseMs==250&&MinFrameIntervalMs==100&&TransportKeepaliveVersion==1; } }
    }

    internal sealed class OpaquePacketHttpException : InvalidOperationException
    {
        internal OpaquePacketHttpException(int statusCode,Exception inner)
            : base("Opaque endpoint returned HTTP "+statusCode+".",inner) { StatusCode=statusCode; }
        internal readonly int StatusCode;
    }

    internal sealed class SessionTransportClient : IDisposable
    {
        private static readonly int[] ChannelCaps={16384,524288,16384,65536,262144};
        private readonly ControlPlaneClient control;
        private readonly string sessionId, deviceId, token, serverUrl, certificatePin;
        private readonly long credentialRevision;
        private readonly object sendQueueLock=new object();
        private Task sendTail=Task.CompletedTask;
        private int queuedPackets, queuedBytes;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private SessionTransportDescriptor current;
        private int receiving, faulted, disposed;

        public SessionTransportClient(ControlPlaneClient client,string sid)
        {
            if(client==null) throw new ArgumentNullException("client");
            Guid parsed;
            if(String.IsNullOrEmpty(sid)||sid.Length!=32||!Guid.TryParseExact(sid,"N",out parsed))
                throw new ArgumentException("Invalid session identifier.","sid");
            control=client; sessionId=sid;
            long before=client.CredentialRevision;
            string id=client.DeviceId, bearer=client.Token;
            long after=client.CredentialRevision;
            if(before<=0||(before&1)!=0||before!=after||String.IsNullOrWhiteSpace(id)||String.IsNullOrWhiteSpace(bearer))
                throw new InvalidOperationException("Device credentials are not in a stable registered state.");
            deviceId=id; token=bearer; credentialRevision=before;
            serverUrl=client.ServerUrl; certificatePin=client.CertificateSha256;
            ValidateEndpoint(serverUrl,certificatePin);
        }

        public Task<SessionTransportDescriptor> ConfigureAsync(int permissionBits,int permissionRevision,int geometryVersion,CancellationToken cancellationToken)
        {
            if(permissionBits<1||permissionBits>31||(permissionBits&1)==0||permissionRevision<=0||geometryVersion<=0)
                throw new ArgumentOutOfRangeException("permissionBits");
            var payload=new Dictionary<string,object> {
                {"permissionBits",permissionBits},{"permissionRevision",permissionRevision},{"geometryVersion",geometryVersion}
            };
            return Task.Run(() => {
                EnsureUsable(cancellationToken);
                var raw=SendRequest("POST",Path("/opaque/configure"),Serialize(payload),8192,10000,cancellationToken,null);
                var descriptor=ParseDescriptor(raw);
                if(descriptor.SessionId!=sessionId||descriptor.TargetDeviceId!=deviceId||
                    descriptor.PermissionBits!=permissionBits||descriptor.PermissionRevision!=permissionRevision||
                    descriptor.GeometryVersion!=geometryVersion) throw new InvalidOperationException("Opaque configure response does not match this target request.");
                EnsureUsable(cancellationToken);
                current=descriptor; return descriptor;
            },cancellationToken);
        }

        public Task<SessionTransportDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => {
                EnsureUsable(cancellationToken);
                var raw=SendRequest("GET",Path("/opaque/descriptor"),null,8192,10000,cancellationToken,null);
                var descriptor=ParseDescriptor(raw);
                ValidateParticipant(descriptor);
                EnsureUsable(cancellationToken); current=descriptor; return descriptor;
            },cancellationToken);
        }

        // authorizationGuard is re-invoked at the queued tail and after the request stream is acquired, before any body byte.
        // Any guard failure here is post-Seal and therefore faults this transport epoch.
        public Task SendAsync(SessionTransportDescriptor descriptor,byte channel,byte[] ciphertext,CancellationToken cancellationToken,Action authorizationGuard=null)
        {
            if(descriptor==null) throw new ArgumentNullException("descriptor");
            ValidateDescriptorIdentity(descriptor);
            if(channel>=ChannelCaps.Length||ciphertext==null||ciphertext.Length<84||ciphertext.Length>ChannelCaps[channel]+84)
                throw new ArgumentException("Opaque packet is outside the channel bounds.","ciphertext");
            EnsureUsable(cancellationToken);
            return SendOrdered(descriptor,channel,ciphertext,cancellationToken,authorizationGuard);
        }

        private Task SendOrdered(SessionTransportDescriptor descriptor,byte channel,byte[] ciphertext,CancellationToken ct,Action guard)
        {
            lock(sendQueueLock)
            {
                if(queuedPackets>=32 || queuedBytes+ciphertext.Length>2*1024*1024)
                { Fault(); throw new InvalidOperationException("Opaque send queue is full; a new authorized epoch is required."); }
                var copy=(byte[])ciphertext.Clone();
                queuedPackets++; queuedBytes+=copy.Length;
                Task previous=sendTail;
                Task next=SendAfter(previous,descriptor,channel,copy,ct,guard);
                sendTail=next;
                return next;
            }
        }

        private async Task SendAfter(Task previous,SessionTransportDescriptor descriptor,byte channel,byte[] ciphertext,CancellationToken ct,Action guard)
        {
            try
            {
                await previous.ConfigureAwait(false);
                EnsureUsable(ct); ValidateDescriptorIdentity(descriptor);
                if(guard!=null) guard();
                var body=new byte[ciphertext.Length+1]; body[0]=channel; Buffer.BlockCopy(ciphertext,0,body,1,ciphertext.Length);
                await Task.Run(() => SendRequest("POST",Path("/opaque/packet")+"?epoch="+descriptor.TransportEpoch,body,0,10000,ct,202,guard),CancellationToken.None).ConfigureAwait(false);
                EnsureUsable(ct);
            }
            catch { Fault(); throw; }
            finally { lock(sendQueueLock) { queuedPackets--; queuedBytes-=ciphertext.Length; } }
        }

        public Task ReceiveAsync(SessionTransportDescriptor descriptor,Func<byte,byte[],Task> handler,CancellationToken cancellationToken)
        {
            if(descriptor==null) throw new ArgumentNullException("descriptor");
            if(handler==null) throw new ArgumentNullException("handler");
            ValidateDescriptorIdentity(descriptor);
            if(Interlocked.CompareExchange(ref receiving,1,0)!=0) throw new InvalidOperationException("Only one opaque stream reader is allowed.");
            return Task.Run(() => ReceiveCore(descriptor,handler,cancellationToken),CancellationToken.None);
        }

        private void ReceiveCore(SessionTransportDescriptor descriptor,Func<byte,byte[],Task> handler,CancellationToken ct)
        {
            var request=(HttpWebRequest)WebRequest.Create(BuildUri(Path("/opaque/stream")+"?epoch="+descriptor.TransportEpoch));
            Configure(request,"GET",35000);
            request.ConnectionGroupName="PeerDesk.OpaqueStream.v1"; // TEMPORARY group-b A/B test variation
            var readToken=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
            CancellationToken readCancellation=readToken.Token;
            var abort=readToken.Token.Register(() => { try { request.Abort(); } catch { } });
            var credentialWatch=new Timer(delegate
            {
                try { EnsureUsable(readCancellation); }
                catch { Fault(); try { request.Abort(); } catch { } }
            },null,100,100);
            try
            {
                EnsureUsable(readToken.Token); ValidateDescriptorIdentity(descriptor);
                using(var response=(HttpWebResponse)request.GetResponse())
                {
                    if(response.StatusCode!=HttpStatusCode.OK) throw new InvalidOperationException("Opaque stream returned HTTP "+(int)response.StatusCode+".");
                    using(var stream=response.GetResponseStream())
                    {
                        if(stream==null) throw new EndOfStreamException("Opaque stream has no response body.");
                        byte[] header=new byte[4];
                        while(true)
                        {
                            EnsureUsable(readToken.Token);
                            int got=ReadSome(stream,header,0,4,readToken.Token,true);
                            if(got==0) return;
                            if(got!=4) throw new EndOfStreamException("Partial opaque frame header.");
                            uint length=ReadU32(header);
                            if(length==0)
                            {
                                // Explicit keepalive (strict capture v1 only): resets the 35 s read timeout, nothing else.
                                // It carries no crypto, authority or peer-liveness meaning and is never handed to the adapter.
                                if(!descriptor.IsStrictCaptureV1) throw new InvalidDataException("Keepalive was not negotiated for this descriptor.");
                                EnsureUsable(readToken.Token); ValidateDescriptorIdentity(descriptor);
                                continue;
                            }
                            if(length<85||length>524288+85) throw new InvalidDataException("Opaque frame length is outside bounds.");
                            byte[] frame=new byte[(int)length];
                            ReadExact(stream,frame,0,frame.Length,readToken.Token);
                            byte channel=frame[0];
                            if(channel>=ChannelCaps.Length||frame.Length>ChannelCaps[channel]+85)
                                throw new InvalidDataException("Opaque frame channel or size is invalid.");
                            byte[] ciphertext=new byte[frame.Length-1]; Buffer.BlockCopy(frame,1,ciphertext,0,ciphertext.Length);
                            if(ciphertext.Length<84) throw new InvalidDataException("Opaque ciphertext is shorter than the crypto overhead.");
                            EnsureUsable(readToken.Token); ValidateDescriptorIdentity(descriptor);
                            handler(channel,ciphertext).GetAwaiter().GetResult();
                            EnsureUsable(readToken.Token);
                        }
                    }
                }
            }
            catch(WebException ex)
            {
                bool alreadyCancelled=lifetime.IsCancellationRequested;
                Fault();
                ct.ThrowIfCancellationRequested();
                if(alreadyCancelled) throw new OperationCanceledException("Opaque transport was invalidated.",ex,lifetime.Token);
                var response=ex.Response as HttpWebResponse;
                if(response!=null) { int code=(int)response.StatusCode; response.Close(); throw new InvalidOperationException("Opaque stream returned HTTP "+code+".",ex); }
                throw new InvalidOperationException("Opaque stream request failed.",ex);
            }
            catch
            {
                Fault();
                throw;
            }
            finally { credentialWatch.Dispose(); abort.Dispose(); readToken.Dispose(); Interlocked.Exchange(ref receiving,0); }
        }

        private int ReadSome(Stream stream,byte[] buffer,int offset,int count,CancellationToken ct,bool allowCleanEof)
        {
            int total=0;
            while(total<count)
            {
                EnsureUsable(ct);
                int n=stream.Read(buffer,offset+total,count-total);
                if(n==0) return total==0&&allowCleanEof?0:total;
                total+=n;
            }
            return total;
        }

        private void ReadExact(Stream stream,byte[] buffer,int offset,int count,CancellationToken ct)
        {
            int n=ReadSome(stream,buffer,offset,count,ct,false);
            if(n!=count) throw new EndOfStreamException("Partial opaque frame body.");
        }

        private string SendRequest(string method,string path,byte[] body,int maxResponse,int timeoutMs,CancellationToken ct,int? expectedStatus,Action guard=null)
        {
            EnsureUsable(ct);
            var request=(HttpWebRequest)WebRequest.Create(BuildUri(path));
            Configure(request,method,timeoutMs);
            if(body!=null) { request.ContentType="application/octet-stream"; request.ContentLength=body.Length; }
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token))
            using(linked.Token.Register(() => { try { request.Abort(); } catch { } }))
            {
                try
                {
                    EnsureUsable(linked.Token);
                    if(body!=null)
                    {
                        EnsureUsable(linked.Token);
                        Exception refused=null;
                        var output=request.GetRequestStream();
                        try
                        {
                            // Request stream acquired; re-check before the first body byte. Abort so a partial body is never completed.
                            try { EnsureUsable(linked.Token); if(guard!=null) guard(); }
                            catch(Exception ex) { refused=ex; try { request.Abort(); } catch { } }
                            if(refused==null) output.Write(body,0,body.Length);
                        }
                        finally
                        {
                            // Closing an aborted stream throws WebException; keep the guard's original exception as the cause.
                            try { output.Dispose(); } catch when(refused!=null) { }
                        }
                        if(refused!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(refused).Throw();
                    }
                    using(var response=(HttpWebResponse)request.GetResponse())
                    {
                        if(expectedStatus.HasValue && (int)response.StatusCode!=expectedStatus.Value)
                            throw new InvalidOperationException("Opaque endpoint returned HTTP "+(int)response.StatusCode+".");
                        if(maxResponse==0) return String.Empty;
                        using(var input=response.GetResponseStream())
                        using(var memory=new MemoryStream())
                        {
                            byte[] buffer=new byte[2048]; int n;
                            while((n=input.Read(buffer,0,buffer.Length))>0)
                            { if(memory.Length+n>maxResponse) throw new InvalidDataException("Opaque descriptor exceeds the 8 KiB limit."); memory.Write(buffer,0,n); }
                            return new UTF8Encoding(false,true).GetString(memory.ToArray());
                        }
                    }
                }
                catch(WebException ex)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var response=ex.Response as HttpWebResponse;
                    if(response!=null)
                    {
                        int code=(int)response.StatusCode; response.Close();
                        if(IsOpaquePacketRequest(method,path)&&(code==404||code==409)) throw new OpaquePacketHttpException(code,ex);
                        throw new InvalidOperationException("Opaque endpoint returned HTTP "+code+".",ex);
                    }
                    throw new InvalidOperationException("Opaque endpoint request failed.",ex);
                }
            }
        }

        private void Configure(HttpWebRequest request,string method,int timeout)
        {
            ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
            request.Method=method; request.AllowAutoRedirect=false; request.Proxy=null;
            request.Timeout=timeout; request.ReadWriteTimeout=timeout; request.KeepAlive=true;
            request.ServicePoint.ConnectionLimit=4; request.ServicePoint.Expect100Continue=false;
            request.ServerCertificateValidationCallback=ValidateCertificate;
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+token;
            request.Headers["X-PeerDesk-Device"]=deviceId;
        }

        private bool ValidateCertificate(object sender,X509Certificate certificate,X509Chain chain,SslPolicyErrors errors)
        {
            if(certificate==null||(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))!=0||
                (errors&~SslPolicyErrors.RemoteCertificateChainErrors)!=SslPolicyErrors.None) return false;
            using(var cert=new X509Certificate2(certificate))
            {
                DateTime now=DateTime.UtcNow;
                if(now<cert.NotBefore.ToUniversalTime()||now>cert.NotAfter.ToUniversalTime()) return false;
            }
            using(var sha=SHA256.Create())
            {
                string actual=BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-","");
                return String.Equals(actual,certificatePin,StringComparison.OrdinalIgnoreCase);
            }
        }

        private void EnsureUsable(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if(Volatile.Read(ref disposed)!=0||Volatile.Read(ref faulted)!=0) throw new InvalidOperationException("Opaque transport is closed or faulted.");
            long revision=control.CredentialRevision;
            if(revision!=credentialRevision||(revision&1)!=0||
                !String.Equals(control.DeviceId,deviceId,StringComparison.Ordinal)||!String.Equals(control.Token,token,StringComparison.Ordinal))
            { Fault(); throw new OperationCanceledException("Device credentials changed during opaque transport.",ct); }
        }

        private void Fault()
        {
            if(Interlocked.Exchange(ref faulted,1)==0) { try { lifetime.Cancel(); } catch { } }
        }

        private void ValidateParticipant(SessionTransportDescriptor d)
        {
            if(d.SessionId!=sessionId||(d.InitiatorDeviceId!=deviceId&&d.TargetDeviceId!=deviceId))
                throw new InvalidOperationException("Opaque descriptor does not identify this participant.");
        }
        private void ValidateDescriptorIdentity(SessionTransportDescriptor d)
        {
            EnsureUsable(CancellationToken.None);
            if(!Object.ReferenceEquals(d,current))
                throw new InvalidOperationException("Opaque descriptor is stale or was not obtained by this transport.");
            ValidateParticipant(d);
        }

        private string Path(string suffix) { return "/api/v2/sessions/"+sessionId+suffix; }

        private bool IsOpaquePacketRequest(string method,string path)
        {
            if(method!="POST"||path==null) return false;
            int q=path.IndexOf('?');
            if(q<0) return false;
            return String.Equals(path.Substring(0,q),Path("/opaque/packet"),StringComparison.Ordinal)&&
                Regex.IsMatch(path.Substring(q+1),"\\Aepoch=[0-9a-f]{32}\\z");
        }
        private Uri BuildUri(string path)
        {
            Uri root;
            if(!Uri.TryCreate(serverUrl,UriKind.Absolute,out root)||root.Scheme!="https"||!String.IsNullOrEmpty(root.UserInfo)||!String.IsNullOrEmpty(root.Query)||!String.IsNullOrEmpty(root.Fragment))
                throw new InvalidOperationException("The configured rendezvous endpoint is invalid.");
            return new Uri(root.ToString().TrimEnd('/')+path,UriKind.Absolute);
        }
        private static void ValidateEndpoint(string url,string pin)
        {
            Uri parsed;
            if(!Uri.TryCreate(url,UriKind.Absolute,out parsed)||parsed.Scheme!="https"||String.IsNullOrEmpty(parsed.Host)||
                String.IsNullOrEmpty(pin)||!Regex.IsMatch(pin,"^[0-9A-Fa-f]{64}$"))
                throw new InvalidOperationException("A trusted HTTPS server profile and full certificate pin are required.");
        }

        private static byte[] Serialize(object value) { return Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength=8192 }.Serialize(value)); }
        private static uint ReadU32(byte[] b) { return ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3]; }

        private SessionTransportDescriptor ParseDescriptor(string json)
        {
            if(json==null||Encoding.UTF8.GetByteCount(json)>8192) throw new InvalidDataException("Opaque descriptor exceeds 8 KiB.");
            var values=StrictObject.Parse(json);
            return ParseDescriptorValues(values);
        }

        // Exactly the legacy 8 fields, or the legacy 8 plus all four strict capture v1 fields with exact values (12 total).
        internal static SessionTransportDescriptor ParseDescriptorValues(Dictionary<string,object> values)
        {
            bool strict=values.Count==12;
            if(values.Count!=8&&!strict) throw new InvalidDataException("Opaque descriptor has missing or extra fields.");
            if(!strict&&(values.ContainsKey("captureLeaseVersion")||values.ContainsKey("captureLeaseMs")||values.ContainsKey("minFrameIntervalMs")||values.ContainsKey("transportKeepaliveVersion")))
                throw new InvalidDataException("Opaque descriptor has partial capture fields.");
            if(strict&&(NumberValue(values,"captureLeaseVersion")!=1||NumberValue(values,"captureLeaseMs")!=250||
                NumberValue(values,"minFrameIntervalMs")!=100||NumberValue(values,"transportKeepaliveVersion")!=1))
                throw new InvalidDataException("Opaque descriptor capture v1 values are invalid.");
            string sid=StringValue(values,"sessionId"), initiator=StringValue(values,"initiatorDeviceId"),
                target=StringValue(values,"targetDeviceId"), epoch=StringValue(values,"transportEpoch");
            long generation=NumberValue(values,"sharingGeneration");
            long bits=NumberValue(values,"permissionBits"), rev=NumberValue(values,"permissionRevision"), geo=NumberValue(values,"geometryVersion");
            Guid id;
            if(sid.Length!=32||!Guid.TryParseExact(sid,"N",out id)||!Regex.IsMatch(sid,"^[0-9a-fA-F]{32}$")||
                initiator.Length<1||initiator.Length>128||target.Length<1||target.Length>128||initiator==target||
                epoch.Length!=32||!Regex.IsMatch(epoch,"^[0-9a-f]{32}$")||generation<=0||bits<1||bits>31||(bits&1)==0||
                rev<=0||rev>Int32.MaxValue||geo<=0||geo>Int32.MaxValue)
                throw new InvalidDataException("Opaque descriptor values are invalid.");
            return strict
                ? new SessionTransportDescriptor(sid,initiator,target,epoch,generation,(int)bits,(int)rev,(int)geo,1,250,100,1)
                : new SessionTransportDescriptor(sid,initiator,target,epoch,generation,(int)bits,(int)rev,(int)geo);
        }

        private static string StringValue(Dictionary<string,object> values,string key)
        {
            object value;
            if(!values.TryGetValue(key,out value)||!(value is string)||String.IsNullOrEmpty((string)value)) throw new InvalidDataException("Invalid opaque descriptor field: "+key);
            return (string)value;
        }
        private static long NumberValue(Dictionary<string,object> values,string key)
        {
            object value; long number;
            if(!values.TryGetValue(key,out value)||!(value is long)) throw new InvalidDataException("Invalid opaque descriptor field: "+key);
            number=(long)value; return number;
        }

        private sealed class StrictObject
        {
            private readonly string text; private int position;
            private StrictObject(string json) { text=json; }
            internal static Dictionary<string,object> Parse(string json) { return new StrictObject(json).ReadRoot(); }
            private Dictionary<string,object> ReadRoot()
            {
                Skip(); Expect('{'); var result=new Dictionary<string,object>(StringComparer.Ordinal); Skip();
                if(Peek('}')) throw new InvalidDataException("Empty opaque descriptor.");
                while(true)
                {
                    Skip(); string key=ReadString(); Skip(); Expect(':'); Skip();
                    object value;
                    if(Peek('"')) value=ReadString();
                    else value=ReadInteger();
                    if(!IsKnown(key)||result.ContainsKey(key)) throw new InvalidDataException("Unknown or duplicate opaque descriptor property.");
                    result.Add(key,value); Skip();
                    if(Peek('}')) { position++; break; }
                    Expect(','); Skip();
                }
                Skip(); if(position!=text.Length) throw new InvalidDataException("Trailing data in opaque descriptor.");
                return result;
            }
            private static bool IsKnown(string key)
            { return key=="sessionId"||key=="initiatorDeviceId"||key=="targetDeviceId"||key=="transportEpoch"||key=="sharingGeneration"||key=="permissionBits"||key=="permissionRevision"||key=="geometryVersion"||
                key=="captureLeaseVersion"||key=="captureLeaseMs"||key=="minFrameIntervalMs"||key=="transportKeepaliveVersion"; }
            private long ReadInteger()
            {
                int start=position;
                while(position<text.Length&&text[position]>='0'&&text[position]<='9') position++;
                if(start==position) throw new InvalidDataException("Opaque descriptor numeric value is not an unsigned integer.");
                long value;
                if(position-start>1&&text[start]=='0') throw new InvalidDataException("Opaque descriptor integer has a leading zero.");
                if(!Int64.TryParse(text.Substring(start,position-start),NumberStyles.None,CultureInfo.InvariantCulture,out value)) throw new InvalidDataException("Opaque descriptor integer overflow.");
                return value;
            }
            private string ReadString()
            {
                int start=position; Expect('"'); bool escaped=false;
                while(position<text.Length)
                {
                    char c=text[position++];
                    if(escaped) { escaped=false; continue; }
                    if(c=='\\') { escaped=true; continue; }
                    if(c=='"')
                    {
                        string raw=text.Substring(start,position-start);
                        try { return new JavaScriptSerializer { MaxJsonLength=8192 }.Deserialize<string>(raw); }
                        catch { throw new InvalidDataException("Malformed JSON string."); }
                    }
                    if(c<0x20) throw new InvalidDataException("Control character in JSON string.");
                }
                throw new InvalidDataException("Unterminated JSON string.");
            }
            private void Skip() { while(position<text.Length&&(text[position]==' '||text[position]=='\r'||text[position]=='\n'||text[position]=='\t')) position++; }
            private bool Peek(char c) { return position<text.Length&&text[position]==c; }
            private void Expect(char c) { if(!Peek(c)) throw new InvalidDataException("Malformed opaque descriptor JSON."); position++; }
        }

        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0) return;
            Fault();
        }
    }
}
