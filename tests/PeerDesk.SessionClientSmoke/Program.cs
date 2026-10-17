using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
class Program {
 static int Main(){
 var a=Assembly.Load("RemoteBridge");var t=a.GetType("PeerDesk.Windows.ControlPlaneClient",true);
 var obj=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
 t.GetField("json",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(obj,new JavaScriptSerializer {MaxJsonLength=65536});
 var parse=t.GetMethod("ParseSessionList",BindingFlags.NonPublic|BindingFlags.Instance);
 var rows=new List<object>();var json=new JavaScriptSerializer();int passed=0;
 Func<string,bool> accepts=s=>{try{parse.Invoke(obj,new object[]{s,true});return true;}catch(TargetInvocationException){return false;}};
 for(int i=0;i<500;i++)rows.Add(new{sessionId=Guid.NewGuid().ToString("N"),initiatorDeviceId="1234567890",targetDeviceId="2345678901",status="Pending",createdAt=DateTimeOffset.UtcNow.ToString("O")});
 string body=json.Serialize(new{sessions=rows});
 bool ordinaryRejected=false;try{parse.Invoke(obj,new object[]{body,false});}catch(TargetInvocationException){ordinaryRejected=true;}
 if(!ordinaryRejected)throw new Exception("ordinary64KB regression");Console.WriteLine("PASS ordinary64KB retained");passed++;
 if(body.Length<=65536||!accepts(body))throw new Exception("500 row parse");
 Console.WriteLine("PASS bounded500 snapshot larger than64KB parsed");passed++;
 rows.Add(rows[0]);if(accepts(json.Serialize(new{sessions=rows})))throw new Exception("501 accepted");Console.WriteLine("PASS 501 rejected");passed++;
 rows.RemoveAt(500);rows[499]=rows[0];if(accepts(json.Serialize(new{sessions=rows})))throw new Exception("duplicate accepted");Console.WriteLine("PASS duplicate sid rejected");passed++;
 foreach(var status in new[]{"Bogus","Connected"}){string s=json.Serialize(new{sessions=new[]{new{sessionId="bad",initiatorDeviceId="1",targetDeviceId="2",status=status,createdAt="bad"}}});if(accepts(s))throw new Exception("malformed accepted");passed++;}
 Console.WriteLine("SESSION_CLIENT_PASS "+passed);return 0;
 }
}
