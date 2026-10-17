using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using PeerDesk.Windows;
internal static class HistorySmoke {
 public static void Run(Action<bool,string> check) {
  string dir=Path.Combine(Path.GetTempPath(),"PeerDesk-history-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  string path=Path.Combine(dir,"history.json"), pin=new string('A',64), url="https://localhost/";
  var h=new ConnectionHistory(path);
  for(int i=0;i<55;i++)h.Add(url,pin,"target-"+i,"Pending");
  var loaded=new ConnectionHistory(path);
  check(loaded.ForServer(url,pin).Count==50,"history50 cap and reload");
  h.Add("https://other/",pin,"other","Connected");
  loaded=new ConnectionHistory(path);
  check(loaded.ForServer("https://other/",pin).Count==1&&loaded.ForServer(url,pin).Count==49,"history server separation");
  var rows=new JavaScriptSerializer().Deserialize<List<Dictionary<string,object>>>(File.ReadAllText(path));
  string[] allowed={"ServerUrl","CertificateSha256","TargetDeviceId","Utc","Status"};
  check(rows.All(r=>r.Keys.All(k=>allowed.Contains(k))&&r.Count==5),"history exact public field whitelist");
  string longUrl="https://"+new string('a',2000)+"/";
  for(int i=0;i<50;i++)h.Add(longUrl,pin,"long-"+i,"Connected");
  loaded=new ConnectionHistory(path);
  check(new FileInfo(path).Length<=65536&&loaded.Warning==null&&loaded.ForServer(longUrl,pin).Count<50,"history byte bound prunes oldest and reloads");
  string bad=Path.Combine(dir,"malformed.json");File.WriteAllText(bad,"not JSON");
  var broken=new ConnectionHistory(bad);bool denied=false;try{broken.Add(url,pin,"target","Pending");}catch(InvalidOperationException){denied=true;}
  check(denied&&broken.Warning!=null&&File.ReadAllText(bad)=="not JSON","malformed history original preserved after add");
  string oversized=Path.Combine(dir,"oversized.json");File.WriteAllBytes(oversized,new byte[65537]);
  var big=new ConnectionHistory(oversized);denied=false;try{big.Add(url,pin,"target","Pending");}catch(InvalidOperationException){denied=true;}
  check(denied&&big.Warning!=null&&new FileInfo(oversized).Length==65537,"oversized history remains unchanged");
 }
}
