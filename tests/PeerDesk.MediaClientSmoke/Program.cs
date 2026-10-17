using System;using System.Collections.Generic;using System.Web.Script.Serialization;using PeerDesk.Windows;
class Program {
static int count;static Dictionary<string,object> D(string s){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(s);}
static void Check(bool x,string n){if(!x)throw new Exception(n);count++;Console.WriteLine("PASS "+n);}
static void Reject(Action a,string n){bool rejected=false;try{a();}catch(InvalidOperationException){rejected=true;}Check(rejected,n);}
static string Event(long seq,long gen=1){return "{\"generation\":"+gen+",\"sequence\":"+seq+",\"kind\":\"text\",\"x\":0,\"y\":0,\"text\":\"q\"}";}
static string Batch(string rows){return "{\"generation\":1,\"leaseMs\":100,\"events\":["+rows+"]}";}
static int Main(){try{
Check(!MediaWire.ParseCapabilities(D("{\"generation\":1}")).Negotiated,"old server uses legacy fallback");
var caps=MediaWire.ParseCapabilities(D("{\"generation\":1,\"minFrameIntervalMs\":100,\"inputLeasePollSupported\":true}"));
Check(caps.Negotiated&&caps.MinFrameIntervalMs==100,"new capability100ms enabled");
Reject(()=>MediaWire.ParseCapabilities(D("{\"generation\":1,\"minFrameIntervalMs\":99,\"inputLeasePollSupported\":true}")),"cap faster than10fps rejected");
Reject(()=>MediaWire.ParseCapabilities(D("{\"generation\":1,\"inputLeasePollSupported\":true}")),"partial capability rejected");
Reject(()=>MediaWire.ParseCapabilities(D("{\"generation\":1,\"minFrameIntervalMs\":\"100\"}")),"string capability rejected");
Check(MediaWire.ParseInputLease(D(Batch(""))).Events.Count==0,"empty input batch valid");
Check(MediaWire.ParseInputLease(D(Batch(Event(1)))).Events.Count==1,"valid input lease accepted");
var rows=new List<string>();for(int i=1;i<=32;i++)rows.Add(Event(i));
Check(MediaWire.ParseInputLease(D(Batch(string.Join(",",rows)))).Events.Count==32,"exact32 input rows accepted");
rows.Add(Event(33));Reject(()=>MediaWire.ParseInputLease(D(Batch(string.Join(",",rows)))),"33 input rows rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)).Replace("\"leaseMs\":100","\"leaseMs\":101"))),"lease101 rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)).Replace("\"leaseMs\":100","\"leaseMs\":0"))),"leasezero rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)).Replace("\"leaseMs\":100","\"leaseMs\":\"100\""))),"string lease rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)+",null"))),"whole malformed batch rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)).Replace("\"x\":0","\"x\":2147483648"))),"coordinate overflow rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1)+","+Event(1)))),"duplicate event sequence rejected");
Reject(()=>MediaWire.ParseInputLease(D(Batch(Event(1,2)))),"row generation mismatch rejected");
Console.WriteLine("MEDIA_CLIENT_PASS "+count);return 0;
}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}