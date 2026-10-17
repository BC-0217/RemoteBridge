using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using PeerDesk.Windows;
internal static class Program {
 delegate bool WindowCallback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")]static extern bool EnumWindows(WindowCallback callback,IntPtr param);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr h,uint cmd);
 [DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
 static bool DismissOwnedError(MainForm f){bool found=false;EnumWindows((h,p)=>{uint pid;GetWindowThreadProcessId(h,out pid);var c=new StringBuilder(64);GetClassName(h,c,64);if(pid==(uint)System.Diagnostics.Process.GetCurrentProcess().Id&&c.ToString()=="#32770"){found=true;PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);return found;}
 static int checks; static string pin=new string('A',64);
 static string profile="{\"Version\":1,\"Name\":\"Synthetic\",\"RendezvousUrl\":\"https://localhost:49999/\",\"RelayUrl\":\"https://localhost:49999/\",\"CertificateSha256\":\""+pin+"\"}";
 static string key="Synthetic-Owner-Key-0123456789abcdef";
 static Control Find(Control c,string n){if(c.Name==n)return c;foreach(Control x in c.Controls){var f=Find(x,n);if(f!=null)return f;}return null;}
 static T C<T>(MainForm f,string n)where T:Control {var x=Find(f,n)as T;if(x==null)throw new Exception("missing "+n);return x;}
 static void Check(bool b,string n){if(!b)throw new Exception(n);checks++;Console.WriteLine("PASS "+n);}
 static void Pump(){Application.DoEvents();}
 static void Call(MainForm f,string n){var b=C<Button>(f,n);Check(b.Visible&&b.Enabled,"actual button available "+n);b.PerformClick();Pump();}
 static void Fits(Control c,string name){var r=c.RectangleToScreen(c.ClientRectangle);for(Control p=c.Parent;p!=null;p=p.Parent)Check(p.RectangleToScreen(p.ClientRectangle).Contains(r),"visible "+name+" in "+p.Name);}
 static object Field(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static MainForm Open(string dir){var f=new MainForm(Path.Combine(dir,"history.json"),Path.Combine(dir,"settings.json"),new DirectoryBootstrap(ServerProfile.Parse(profile).RendezvousUrl,pin,"synthetic-registry"));f.Show();Pump();return f;}
 static void Host(MainForm f){C<ComboBox>(f,"ServerRole").SelectedIndex=1;Pump();C<TabControl>(f,"HostSettingsTabs").SelectedIndex=1;Pump();}
 static void Until(Func<bool> condition){var sw=System.Diagnostics.Stopwatch.StartNew();while(!condition()){Application.DoEvents();if(sw.ElapsedMilliseconds>6000)throw new Exception("async UI timeout");System.Threading.Thread.Sleep(10);}Pump();}
 static async Task UntilAsync(Func<bool> condition){var sw=System.Diagnostics.Stopwatch.StartNew();while(!condition()){if(sw.ElapsedMilliseconds>6000)throw new Exception("async UI timeout");await Task.Delay(10);}}
 static MainForm OpenNetwork(string dir){var f=new MainForm(Path.Combine(dir,"history.json"),Path.Combine(dir,"settings.json"),new DirectoryBootstrap(ServerProfile.Parse(profile).RendezvousUrl,pin,"synthetic-registry"));f.Show();return f;}
 static async Task CloseNetwork(MainForm f){f.Close();await UntilAsync(()=>f.IsDisposed);}
 static async Task<int> Network(string dir){
  profile=Console.ReadLine();key=Console.ReadLine();string pw=Console.ReadLine();pin=ServerProfile.Parse(profile).CertificateSha256;
  var f=OpenNetwork(dir);C<TextBox>(f,"ProfileJson").Text=profile;C<ComboBox>(f,"ServerRole").SelectedIndex=1;C<TabControl>(f,"HostSettingsTabs").SelectedIndex=1;C<TextBox>(f,"OwnerSecret").Text=key;C<TextBox>(f,"RoomServerId").Text="saved-room";C<TextBox>(f,"RoomJoinPassword").Text=pw;
  C<Button>(f,"RoomSave").PerformClick();await UntilAsync(()=>C<Button>(f,"RoomSave").Enabled&&C<Button>(f,"RoomCopy").Enabled);
  Check(new SettingsStore(Path.Combine(dir,"settings.json")).Load().SavedInputs[0].VerifiedJoinPassword==pw,"real owner server password success encrypted");
  await CloseNetwork(f);f=OpenNetwork(dir);C<ComboBox>(f,"ServerRole").SelectedIndex=1;C<TabControl>(f,"HostSettingsTabs").SelectedIndex=1;Check(C<TextBox>(f,"OwnerSecret").Text==key&&!((bool)Field(f,"roomPasswordDirty")),"owner restored unverified draft non-dirty");
  C<TextBox>(f,"RoomServerId").Text="saved-room-two";C<Button>(f,"RoomSave").PerformClick();await UntilAsync(()=>C<Button>(f,"RoomSave").Enabled&&C<Button>(f,"RoomCopy").Enabled);
  var probe=new ControlPlaneClient(ServerProfile.Parse(profile));await Task.Run(()=>probe.Register(pw));Check(probe.DeviceId.Length==10,"ID-only room save retains original server PW");
  await CloseNetwork(f);
  string guestDir=Path.Combine(dir,"guest");Directory.CreateDirectory(guestDir);f=OpenNetwork(guestDir);
  C<TextBox>(f,"GuestServerAlias").Text="saved-room";C<TextBox>(f,"GuestJoinPassword").Text=pw;C<Button>(f,"GuestJoin").PerformClick();await UntilAsync(()=>C<Button>(f,"RequestConnection").Enabled);
  var stored=new SettingsStore(Path.Combine(guestDir,"settings.json")).Load();Check(stored.IsRegistered&&stored.SavedInputs[0].VerifiedJoinPassword==pw,"real guest lookup/register saves registration and verified input");
  string id=stored.DeviceId;await CloseNetwork(f);if(Environment.GetCommandLineArgs().Contains("--restart-server")){Console.WriteLine("REQUEST_PERSISTENT_SERVER_RESTART");string resumed=await Task.Run(()=>Console.ReadLine());Check(resumed=="PERSISTENT_SERVER_RESTARTED","fixture server actually stopped and restarted with durable state");}f=OpenNetwork(guestDir);await UntilAsync(()=>C<Button>(f,"RequestConnection").Enabled);
  Check(C<TextBox>(f,"MyDeviceId").Text==id&&C<TextBox>(f,"GuestJoinPassword").Text==pw,"guest reopen retains registered ID and remembered join input");
  await CloseNetwork(f);
  var countBefore=(await probe.OwnerListDevicesAsync(key)).Count;
  string mismatchDir=Path.Combine(dir,"mismatch");Directory.CreateDirectory(mismatchDir);
  var mismatch=stored.SavedInputs[0].Clone();mismatch.ServerUrl="https://localhost:49999/";mismatch.RelayUrl=mismatch.ServerUrl;mismatch.CertificateSha256=new string('B',64);
  new SettingsStore(Path.Combine(mismatchDir,"settings.json")).Save(new SettingsState{ProfileJson="",SavedInputs=new System.Collections.Generic.List<SavedInputEntry>{mismatch}});
  f=OpenNetwork(mismatchDir);Check(C<Button>(f,"GuestJoin").Visible&&C<Button>(f,"GuestJoin").Enabled&&C<TextBox>(f,"GuestServerAlias").Text=="saved-room"&&C<TextBox>(f,"GuestJoinPassword").Text==pw,"mismatch fixture restores bound guest input before explicit join");bool dismissed=false;using(var timer=new Timer{Interval=50}){timer.Tick+=(s,e)=>{dismissed|=DismissOwnedError(f);};timer.Start();C<Button>(f,"GuestJoin").PerformClick();try{await UntilAsync(()=>dismissed&&C<Button>(f,"GuestJoin").Enabled);}catch{Console.WriteLine("MISMATCH_DIAG dismissed="+dismissed+" enabled="+C<Button>(f,"GuestJoin").Enabled+" busy="+Field(f,"lifecycleBusy")+" hasError="+((Label)Field(f,"status")).Text.StartsWith("실패")+" changedTarget="+((Label)Field(f,"status")).Text.Contains("이전"));throw;}timer.Stop();}
  Check(((Label)Field(f,"status")).Text.Contains("이전")&&C<TextBox>(f,"GuestJoinPassword").TextLength==0,"changed resolved target blocks remembered password and clears UI");
  Check((await probe.OwnerListDevicesAsync(key)).Count==countBefore&&!new SettingsStore(Path.Combine(mismatchDir,"settings.json")).Load().IsRegistered,"changed target lookup does not register a device");
  await CloseNetwork(f);Console.WriteLine("SAVED_INPUTS_NETWORK_PASS "+checks);return 0;
 }
 static void Close(MainForm f){f.Close();for(int i=0;i<100&&!f.IsDisposed;i++){Application.DoEvents();System.Threading.Thread.Sleep(10);}if(!f.IsDisposed)throw new Exception("close timeout");}
 [STAThread] static int Main(string[] args){
  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);Application.ThreadException+=(sender,eventArgs)=>{Console.WriteLine("UI_THREAD_ERROR "+eventArgs.Exception.ToString());Environment.Exit(2);};Application.EnableVisualStyles();string dir=Path.Combine(Path.GetTempPath(),"RemoteBridgeSavedUi-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try{
   if(args.Length>0){int result=1;using(var pump=new Form{Text="RemoteBridge owned saved-input test",Width=320,Height=120}){pump.Shown+=async(s,e)=>{try{result=await Network(dir);}catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+" "+ex.Message);}finally{pump.Close();}};Application.Run(pump);}return result;}
   var f=Open(dir);C<TextBox>(f,"ProfileJson").Text=profile;Host(f);
   foreach(var size in new[]{new System.Drawing.Size(760,620),new System.Drawing.Size(900,720)}){f.Size=size;Pump();foreach(var name in new[]{"SaveInputsNow","ForgetSavedInputs","RoomServerId","RoomJoinPassword","RoomSave","RoomLoad"})Fits(C<Control>(f,name),name);}
   C<TextBox>(f,"OwnerSecret").Text=key;C<TextBox>(f,"RoomServerId").Text="saved-room";C<TextBox>(f,"RoomJoinPassword").Text="SyntheticPw11";
   Call(f,"SaveInputsNow");
   var state=new SettingsStore(Path.Combine(dir,"settings.json")).Load();
   Check(state.SavedInputs.Count==1&&state.SavedInputs[0].OwnerKey==key,"host draft encrypted save");
   Check((bool)Field(f,"roomPasswordDirty"),"local save preserves server password edit intent");
   Check(state.ProfileJson.Length>0,"unregistered host profile persisted with input");
   Close(f);f=Open(dir);Host(f);
   Check(C<TextBox>(f,"OwnerSecret").Text==key&&C<TextBox>(f,"RoomJoinPassword").Text=="SyntheticPw11","host input restored");
   Check(!(bool)Field(f,"roomPasswordDirty"),"restored password is not remote change intent");
   Check(!C<Button>(f,"RoomCopy").Enabled,"restore does not grant verified room copy");
   C<TextBox>(f,"ProfileJson").Text=profile.Replace(pin,new string('B',64));Pump();
   Check(C<TextBox>(f,"OwnerSecret").TextLength==0&&C<TextBox>(f,"RoomJoinPassword").TextLength==0,"different pin clears secret UI");
   C<TextBox>(f,"ProfileJson").Text=profile;Pump();Host(f);
   Check(C<TextBox>(f,"OwnerSecret").Text==key,"exact original profile restores its own secret");
   Call(f,"ForgetSavedInputs");Close(f);f=Open(dir);Host(f);
   Check(C<TextBox>(f,"OwnerSecret").TextLength==0&&C<TextBox>(f,"RoomJoinPassword").TextLength==0,"forgotten host not resurrected on close/reopen");
   C<ComboBox>(f,"ServerRole").SelectedIndex=0;Pump();
   C<TextBox>(f,"GuestServerAlias").Text="guest-room";C<TextBox>(f,"GuestJoinPassword").Text="SyntheticGuest11";
   Call(f,"SaveInputsNow");Close(f);f=Open(dir);
   Check(C<TextBox>(f,"GuestServerAlias").Text=="guest-room"&&C<TextBox>(f,"GuestJoinPassword").Text=="SyntheticGuest11","unresolved guest draft restores without lookup");
   Close(f);
   string two=Path.Combine(dir,"two");Directory.CreateDirectory(two);f=Open(two);C<TextBox>(f,"ProfileJson").Text=profile;Host(f);
   C<TextBox>(f,"OwnerSecret").Text=key;C<TextBox>(f,"RoomServerId").Text="host-two";C<TextBox>(f,"RoomJoinPassword").Text="HostDraftPW11";
   C<ComboBox>(f,"ServerRole").SelectedIndex=0;Pump();C<TextBox>(f,"GuestServerAlias").Text="guest-two";C<TextBox>(f,"GuestJoinPassword").Text="GuestDraftPW11";Close(f);
   f=Open(two);Check(C<TextBox>(f,"GuestServerAlias").Text=="guest-two"&&C<TextBox>(f,"GuestJoinPassword").Text=="GuestDraftPW11","guest draft saved on close after role switch");
   Host(f);Check(C<TextBox>(f,"OwnerSecret").Text==key&&C<TextBox>(f,"RoomJoinPassword").Text=="HostDraftPW11","host draft survives guest switch and close");
   long oldRevision=(long)Field(f,"savedInputsRevision");string binding=(string)Field(f,"ownerKeyBinding");Call(f,"ForgetSavedInputs");
   typeof(MainForm).GetMethod("RecordOwnerSuccess",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,new object[]{key,binding,oldRevision});
   var forgotten=new SettingsStore(Path.Combine(two,"settings.json")).Load();Check(forgotten.SavedInputs.Find(x=>x.Role=="host").OwnerKey==null,"late async result cannot resurrect forgotten secret");
   Close(f);
   Console.WriteLine("SAVED_INPUTS_UI_PASS "+checks);return 0;
  }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+" "+ex.Message);return 1;}
 }
}
