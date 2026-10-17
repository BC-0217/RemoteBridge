using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using PeerDesk.Windows;
class Program {
static BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
static FieldInfo Field(Type t,string n){return t.GetField(n,F);}
static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
[STAThread] static int Main(){
 try {
 Application.EnableVisualStyles();
 string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeStopped-"+Guid.NewGuid().ToString("N"));
 using(var form=new MainForm(Path.Combine(dir,"history.json"),Path.Combine(dir,"settings.json"))) {
 var handle=form.Handle; // Hidden owned form: no focus, capture or network.
 var t=typeof(MainForm);var mt=typeof(MediaSharingSession);
 // Component fault injection only: no capture, registration or network is started.
 var dead=(MediaSharingSession)FormatterServices.GetUninitializedObject(mt);
 Field(mt,"<LastError>k__BackingField").SetValue(dead,"TEST_INJECTED_CAPTURE_FAILURE");
 Field(t,"sharing").SetValue(form,dead);Field(t,"monitoredSharing").SetValue(form,dead);
 var gt=t.GetNestedType("ConsentGrant",BindingFlags.NonPublic);
 var grant=Activator.CreateInstance(gt,true);
 Field(gt,"SessionId").SetValue(grant,"test-session");
 Field(gt,"StartAttempted").SetValue(grant,true);
 var grants=(IDictionary)Field(t,"consentGrants").GetValue(form);grants.Add("test-session",grant);
 t.GetMethod("CheckSharingStopped",F).Invoke(form,null);
 var sw=Stopwatch.StartNew();
 while((bool)Field(t,"sharingStopCleanupBusy").GetValue(form)&&sw.ElapsedMilliseconds<2000){Application.DoEvents();Thread.Sleep(10);}
 Check(Field(t,"sharing").GetValue(form)==null,"dead sharing reference cleared");
 Check(Field(t,"monitoredSharing").GetValue(form)==null,"monitor reference cleared");
 Check(form.ShareStopDiagnostic!=null&&form.ShareStopDiagnostic.Contains("TEST_INJECTED_CAPTURE_FAILURE"),"injected LastError exposed");
 var status=(Label)Field(t,"status").GetValue(form);
 Check(status.Text==form.ShareStopDiagnostic,"actual UI label displays diagnostic");
 Check((bool)Field(gt,"StartAttempted").GetValue(grant)&&ReferenceEquals(grants["test-session"],grant),"one-shot consent remains consumed");
 sw.Restart();while(sw.ElapsedMilliseconds<550){Application.DoEvents();Thread.Sleep(10);}
 Check(!form.SharingIsRunning&&Field(t,"sharing").GetValue(form)==null&&(bool)Field(gt,"StartAttempted").GetValue(grant),"timer does not restart consumed sharing");
 foreach(var reason in new[]{"user-stopped","session-ended","error-stopped","expired"}) {
 var stopped=(MediaSharingSession)FormatterServices.GetUninitializedObject(mt);
 Field(mt,"<EndReason>k__BackingField").SetValue(stopped,reason);
 bool normal=reason=="user-stopped"||reason=="session-ended";
 if(!normal)Field(mt,"<LastError>k__BackingField").SetValue(stopped,"TEST_"+reason);
 Field(t,"sharing").SetValue(form,stopped);Field(t,"monitoredSharing").SetValue(form,stopped);
 t.GetMethod("CheckSharingStopped",F).Invoke(form,null);
 sw.Restart();while((bool)Field(t,"sharingStopCleanupBusy").GetValue(form)&&sw.ElapsedMilliseconds<2000){Application.DoEvents();Thread.Sleep(5);}
 Check(Field(t,"sharing").GetValue(form)==null,"typed stop reference cleared "+reason);
 Check(normal?form.ShareStopDiagnostic==null&&status.Text.Contains("정상적으로"):reason=="expired"?status.Text.Contains("만료"):form.ShareStopDiagnostic!=null&&status.Text.Contains("TEST_"+reason),"typed stop actual label "+reason);
}
var conflicted=(MediaSharingSession)FormatterServices.GetUninitializedObject(mt);
Field(mt,"<EndReason>k__BackingField").SetValue(conflicted,"session-ended");
Field(mt,"<LastError>k__BackingField").SetValue(conflicted,"TEST_ERROR_PRIORITY");
Field(t,"sharing").SetValue(form,conflicted);Field(t,"monitoredSharing").SetValue(form,conflicted);
t.GetMethod("CheckSharingStopped",F).Invoke(form,null);
sw.Restart();while((bool)Field(t,"sharingStopCleanupBusy").GetValue(form)&&sw.ElapsedMilliseconds<2000){Application.DoEvents();Thread.Sleep(5);}
Check(status.Text.Contains("TEST_ERROR_PRIORITY"),"LastError takes precedence over normal typed reason");
Check((bool)Field(gt,"StartAttempted").GetValue(grant),"all typed ends preserve consumed consent");
foreach(bool fail in new[]{false,true}) {
 var manual=(MediaSharingSession)FormatterServices.GetUninitializedObject(mt);
 if(fail)Field(mt,"<LastError>k__BackingField").SetValue(manual,"TEST_STOP_CONFIRM_FAILURE");
 Field(t,"sharing").SetValue(form,manual);
 var stop=(System.Threading.Tasks.Task)t.GetMethod("StopShareCore",F).Invoke(form,null);
 sw.Restart();while(!stop.IsCompleted&&sw.ElapsedMilliseconds<2000){Application.DoEvents();Thread.Sleep(5);}
 stop.GetAwaiter().GetResult();
 Check(fail?status.Text.Contains("TEST_STOP_CONFIRM_FAILURE")&&!status.Text.Equals("화면 공유를 중지했습니다."):status.Text.Equals("화면 공유를 중지했습니다."),"manual stop distinguishes confirmation failure "+fail);
}
form.Close();Application.DoEvents();
 }
 Console.WriteLine("STOPPED_SHARING_UI_PASS 18 COMPONENT_INJECTION_NOT_PHYSICAL_B");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
}
}
