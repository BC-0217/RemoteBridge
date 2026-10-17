using System;using System.IO;using System.Linq;using PeerDesk.Windows;
class Program {
static int n;static void Check(bool b,string s){if(!b)throw new Exception(s);Console.WriteLine("PASS "+s);n++;}
static int Main(){try{string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeHistory-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string p=Path.Combine(dir,"history.json"),url="https://localhost:4321",pin=new string('A',64);var h=new ConnectionHistory(p);
h.Add(url,pin,"1111111111","Pending");Check(h.ForServer(url,pin).Count==0&&!File.Exists(p),"pending does not create recent connection");
h.Add(url,pin,"1111111111","Connected");h.Add(url,pin,"2222222222","Connected");h.Add(url,pin,"1111111111","Connected");Check(h.ForServer(url,pin).Count==2&&h.ForServer(url,pin)[0].TargetDeviceId=="1111111111","same peer upsert latest first");
for(int i=0;i<70;i++)h.Add(url,pin,"3333333333","Rejected");Check(h.ForServer(url,pin).Count==2,"failed attempts never evict connected peers");
h.Add(url,new string('B',64),"1111111111","Connected");Check(h.ForServer(url,new string('B',64)).Count==1,"pin contexts separate");
for(int i=0;i<60;i++)h.Add(url,pin,(4000000000L+i).ToString(),"Connected");Check(h.ForServer(url,pin).Count==50,"max50 connected peers");
var reload=new ConnectionHistory(p);Check(reload.ForServer(url,pin).Count==50&&reload.ForServer(url,pin)[0].TargetDeviceId=="4000000059","roundtrip order and bound");
string legacy=Path.Combine(dir,"legacy.json");File.WriteAllText(legacy,"[{\"ServerUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\",\"TargetDeviceId\":\"1\",\"Utc\":\"2026-10-01T00:00:00Z\",\"Status\":\"Connected\"},{\"ServerUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\",\"TargetDeviceId\":\"1\",\"Utc\":\"2026-10-02T00:00:00Z\",\"Status\":\"Revoked\"}]");string before=File.ReadAllText(legacy);var old=new ConnectionHistory(legacy);Check(old.ForServer(url,pin).Count==1&&old.ForServer(url,pin)[0].Status=="Connected","legacy events projected to connected only");Check(File.ReadAllText(legacy)==before,"load preserves legacy disk source");Check(old.ForServer(url,pin)[0].ToString().StartsWith("1"),"ID first display");
var cases=new ConnectionHistory(Path.Combine(dir,"case.json"));cases.Add(url+"/Room",pin,"1234567890","Connected");cases.Add(url+"/room",pin,"2234567890","Connected");Check(cases.ForServer(url+"/Room",pin).Single().TargetDeviceId=="1234567890"&&!cases.Contains(url+"/room",pin,"1234567890","Connected"),"URL case contexts isolated in listing and Contains");
Console.WriteLine("HISTORY_RECENT_PASS "+n);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
