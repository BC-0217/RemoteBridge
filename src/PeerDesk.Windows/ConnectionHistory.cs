using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PeerDesk.Windows
{
    internal sealed class ConnectionHistoryRecord
    {
        public string ServerUrl { get; set; }
        public string CertificateSha256 { get; set; }
        public string TargetDeviceId { get; set; }
        public string Utc { get; set; }
        public string Status { get; set; }
        public override string ToString()
        {
            DateTime parsed;
            string when=DateTime.TryParse(Utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Utc;
            return TargetDeviceId + "    (마지막 연결 " + when + ")";
        }
    }

    internal sealed class ConnectionHistory
    {
        private const int MaxBytes=65536;
        private const int MaxRecords=50;
        private const string ConnectedStatus="Connected";
        private readonly string path;
        private readonly JavaScriptSerializer serializer=new JavaScriptSerializer { MaxJsonLength=MaxBytes*8 };
        private bool malformed;
        public string Warning { get; private set; }

        public ConnectionHistory(string filePath)
        {
            path=filePath;
            Load();
        }

        public IList<ConnectionHistoryRecord> ForServer(string serverUrl,string pin)
        {
            var result=new List<ConnectionHistoryRecord>();
            foreach(var r in records)
                if(String.Equals(r.ServerUrl,serverUrl,StringComparison.Ordinal) &&
                   String.Equals(r.CertificateSha256,pin,StringComparison.Ordinal))
                    result.Add(r);
            return result;
        }

        public bool Contains(string serverUrl,string pin,string target,string status)
        {
            return records.Any(r=>String.Equals(r.ServerUrl,serverUrl,StringComparison.Ordinal) &&
                String.Equals(r.CertificateSha256,pin,StringComparison.Ordinal) &&
                String.Equals(r.TargetDeviceId,target,StringComparison.Ordinal) &&
                String.Equals(r.Status,status,StringComparison.Ordinal));
        }

        private readonly List<ConnectionHistoryRecord> records=new List<ConnectionHistoryRecord>();

        public void Add(string serverUrl,string pin,string target,string status)
        {
            if(malformed) throw new InvalidOperationException("기존 연결 기록이 손상되어 원본을 보존 중입니다. 기록 파일을 확인하기 전에는 덮어쓰지 않습니다.");
            // 실제로 연결된 기록만 남긴다. Pending/Rejected/Revoked 같은 상태 변화가 연결 기록을 밀어내지 않도록 한다.
            if(!String.Equals(status,ConnectedStatus,StringComparison.Ordinal)) return;
            var item=new ConnectionHistoryRecord {
                ServerUrl=serverUrl, CertificateSha256=pin, TargetDeviceId=target,
                Utc=DateTime.UtcNow.ToString("o"), Status=status
            };
            records.RemoveAll(r=>SameEndpoint(r,serverUrl,pin,target));
            records.Insert(0,item);
            if(records.Count>MaxRecords) records.RemoveRange(MaxRecords,records.Count-MaxRecords);
            SaveAtomic();
        }

        private void Load()
        {
            if(!File.Exists(path)) return;
            try
            {
                byte[] bytes;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var bounded=new MemoryStream())
                {
                    byte[] chunk=new byte[4096]; int n;
                    while((n=stream.Read(chunk,0,Math.Min(chunk.Length,MaxBytes+1-(int)bounded.Length)))>0)
                    {
                        if(bounded.Length+n>MaxBytes) throw new InvalidDataException("history.json exceeds 64KiB.");
                        bounded.Write(chunk,0,n);
                    }
                    bytes=bounded.ToArray();
                }
                var text=new UTF8Encoding(false,true).GetString(bytes);
                var list=serializer.Deserialize<List<ConnectionHistoryRecord>>(text);
                if(list==null || list.Count>MaxRecords) throw new InvalidDataException("history record count is invalid.");
                foreach(var r in list)
                {
                    if(r==null || String.IsNullOrWhiteSpace(r.ServerUrl) || String.IsNullOrWhiteSpace(r.CertificateSha256) ||
                       String.IsNullOrWhiteSpace(r.TargetDeviceId) || String.IsNullOrWhiteSpace(r.Utc) ||
                       String.IsNullOrWhiteSpace(r.Status) || r.ServerUrl.Length>2048 || r.CertificateSha256.Length!=64 ||
                       r.TargetDeviceId.Length>256 || r.Utc.Length>64 || r.Status.Length>32)
                        throw new InvalidDataException("history record fields are invalid.");
                    DateTime parsed;
                    if(!DateTime.TryParse(r.Utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out parsed))
                        throw new InvalidDataException("history timestamp is invalid.");
                }
                // 예전 파일은 상태 변화마다 한 줄씩 쌓였다. 읽을 때만 정규화하고 원본은 다음 Add 전까지 그대로 둔다.
                foreach(var r in list.Where(x=>String.Equals(x.Status,ConnectedStatus,StringComparison.Ordinal))
                                     .OrderByDescending(x=>DateTime.Parse(x.Utc,null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()))
                    if(!records.Any(x=>SameEndpoint(x,r.ServerUrl,r.CertificateSha256,r.TargetDeviceId))) records.Add(r);
            }
            catch(Exception ex)
            {
                malformed=true;
                Warning="연결 기록을 읽지 못했습니다. 원본은 덮어쓰지 않습니다: "+ex.Message;
            }
        }

        private static bool SameEndpoint(ConnectionHistoryRecord r,string serverUrl,string pin,string target)
        {
            return String.Equals(r.ServerUrl,serverUrl,StringComparison.Ordinal) &&
                String.Equals(r.CertificateSha256,pin,StringComparison.Ordinal) &&
                String.Equals(r.TargetDeviceId,target,StringComparison.Ordinal);
        }

        private void SaveAtomic()
        {
            var directory=Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            byte[] bytes;
            do
            {
                string json=serializer.Serialize(records);
                bytes=new UTF8Encoding(false).GetBytes(json);
                if(bytes.Length<=MaxBytes) break;
                if(records.Count<=1) throw new InvalidOperationException("연결 기록 한 건이 64KiB 한도를 넘습니다.");
                records.RemoveAt(records.Count-1);
            } while(true);
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var fs=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    fs.Write(bytes,0,bytes.Length);
                    fs.Flush(true);
                }
                if(File.Exists(path)) File.Replace(temp,path,null);
                else File.Move(temp,path);
            }
            finally { try { if(File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }
}
