using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static class RoomUiChecks {
 static int checks;
 static T F<T>(Control root,string name)where T:Control{return root.Controls.Find(name,true).OfType<T>().Single();}
 static void Check(bool pass,string name){if(!pass)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
 static void Select(MainForm f,string page){
  F<TabControl>(f,"ServerSettingsTabs").SelectedIndex=2;F<ComboBox>(f,"ServerRole").SelectedIndex=1;
  var h=F<TabControl>(f,"HostSettingsTabs");h.SelectedTab=h.TabPages.Cast<TabPage>().Single(p=>p.Name==(page=="HostConnectionPage"?page:"HostOwnerPage"));
  if(page!="HostConnectionPage"){var m=F<TabControl>(f,"OwnerManagementTabs");m.SelectedTab=m.TabPages.Cast<TabPage>().Single(p=>p.Name==page);}f.PerformLayout();
 }
 static bool Fits(Control c){if(!c.Visible||c.Width<1||c.Height<1)return false;var bounds=c.RectangleToScreen(c.ClientRectangle);for(Control p=c.Parent;p!=null;p=p.Parent)if(!p.RectangleToScreen(p.ClientRectangle).Contains(bounds))return false;return true;}
 static void Shot(Form f,string path){using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}
 static void Help(MainForm f,string button,string[] words){
  var b=F<Button>(f,button);Check(Fits(b),"Visible beginner help "+button);b.PerformClick();Application.DoEvents();
  var h=Application.OpenForms.Cast<Form>().Single(x=>x.GetType().Name=="HostHelpForm");var t=F<TextBox>(h,"HostHelpText");
  Check(h.Visible&&!h.Modal&&t.ReadOnly,"Actual nonmodal readable explanation "+button);
  Check(words.All(w=>t.Text.Contains(w)),"Explanation includes meaning source and concrete example "+button);
  F<Button>(h,"CloseHostHelp").PerformClick();Application.DoEvents();Check(h.IsDisposed,"Beginner help closes normally "+button);
 }
 internal static int Run(string output){
  Directory.CreateDirectory(output);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int result=1;
  var bootstrap=new DirectoryBootstrap("https://localhost:8443",new string('A',64),"owned-room-ui");
  using(var form=new MainForm(Path.Combine(output,"history.json"),Path.Combine(output,"settings-"+Guid.NewGuid().ToString("N")+".json"),bootstrap)){
   form.Shown+=(s,e)=>{try{
    string validSetup="{\"Version\":1,\"Name\":\"Owned copy fixture\",\"RendezvousUrl\":\"https://localhost:8443\",\"RelayUrl\":\"https://localhost:8443\",\"CertificateSha256\":\""+new string('A',64)+"\"}";F<TextBox>(form,"ProfileJson").Text=validSetup;
    var role=F<ComboBox>(form,"ServerRole");Check(role.Items[0].ToString().Contains("서버 참가")&&role.Items[1].ToString().Contains("서버 만들기"),"Mode names no longer confuse remote host and guest");
    foreach(var size in new[]{new Size(760,620),new Size(900,720)}){
     form.Size=size;F<TabControl>(form,"ServerSettingsTabs").SelectedIndex=2;role.SelectedIndex=0;form.PerformLayout();
     foreach(var n in new[]{"GuestServerAlias","GuestJoinPassword","GuestJoin"})Check(Fits(F<Control>(form,n)),"Join uses visible server ID and password controls width="+size.Width+" "+n);
     foreach(var n in new[]{"ServerAddress","ServerFingerprint","OwnerSecret","ProfileJson","InvitationCode"})Check(!F<Control>(form,n).Visible,"Join hides technical or administrative input "+n);
     Select(form,"HostConnectionPage");
     Check(!F<TextBox>(form,"ServerName").Visible,"Setup does not expose a second ambiguous server-name field");
     Check(!F<TextBox>(form,"ProfileJson").Visible&&!F<TextBox>(form,"InvitationCode").Visible,"Raw JSON and legacy invitation are hidden by default");
     foreach(var n in new[]{"ServerAddress","ServerFingerprint","CheckServerConnection"})Check(Fits(F<Control>(form,n)),"Creator connection fields fit width="+size.Width+" "+n);
     Help(form,"HelpServerAddress",new[]{"Lightsail","8443"});
     Help(form,"HelpCertificateFingerprint",new[]{"SHA","64"});
     Help(form,"HelpSetupFile",new[]{"파일","JSON"});
     if(size.Width==900)Shot(form,Path.Combine(output,"creator-connection.png"));
     Select(form,"RoomSettingsPage");
     foreach(var n in new[]{"RoomServerId","RoomJoinPassword","RoomSave"})Check(Fits(F<Control>(form,n)),"Creator has a single shared-ID/password operation width="+size.Width+" "+n);
     Check(!F<Button>(form,"RoomCopy").Enabled,"Unverified room information cannot be shared");
     const string password="Aa1!Bb2@Cc3";F<TextBox>(form,"RoomJoinPassword").Text=password;F<TextBox>(form,"RoomServerId").Text="ChanServer-1";F<TextBox>(form,"OwnerSecret").Clear();
     F<Button>(form,"RoomSave").PerformClick();Application.DoEvents();
     Check(F<TextBox>(form,"RoomJoinPassword").Text==password&&F<TextBox>(form,"OwnerSecret").Focused,"Missing owner key preserves room password and guides the user to the right field");
     F<TextBox>(form,"RoomJoinPassword").Clear();if(size.Width==900)Shot(form,Path.Combine(output,"creator-room.png"));
    }
    // White-box unit of the successful-response copy path; not a substitute for real owner API tests.
    Select(form,"RoomSettingsPage");
    string localProfile=validSetup;
    F<TextBox>(form,"ProfileJson").Text=localProfile;
    var fixture=new OwnerRoomInfo{ServerId="owned-copy",ServerIds=new[]{"owned-copy"},JoinPasswordConfigured=true,SelfPublishEnabled=true,ProfileJson=localProfile};
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    var apply=typeof(MainForm).GetMethod("ApplyRoom",flags);var enabled=typeof(MainForm).GetMethod("SetOwnerEnabled",flags);
    string oldClipboard=Clipboard.ContainsText()?Clipboard.GetText():null;
    try{
     apply.Invoke(form,new object[]{fixture,ServerProfile.Parse(localProfile),"Aa1!Bb2@Cc3","Synthetic response unit"});enabled.Invoke(form,null);
     F<Button>(form,"RoomCopy").PerformClick();
     Check(Clipboard.GetText()=="서버 ID : owned-copy\r\n서버 PW : Aa1!Bb2@Cc3","Synthetic successful-response unit copies the ID and explicitly supplied password with readable labels");
     apply.Invoke(form,new object[]{fixture,ServerProfile.Parse(localProfile),null,"Synthetic readback unit"});enabled.Invoke(form,null);
     F<Button>(form,"RoomCopy").PerformClick();
     Check(Clipboard.GetText()=="owned-copy","Synthetic readback unit never reuses the previous response password");
     foreach(bool invalidId in new[]{true,false}){
      apply.Invoke(form,new object[]{fixture,ServerProfile.Parse(localProfile),"Aa1!Bb2@Cc3","Synthetic local error unit"});enabled.Invoke(form,null);
      F<TextBox>(form,"RoomServerId").Text=invalidId?"x":"owned-copy";F<TextBox>(form,"RoomJoinPassword").Text=invalidId?"Aa1!Bb2@Cc3":"short";
      F<Button>(form,"RoomSave").PerformClick();Application.DoEvents();
      Check(!F<Button>(form,"RoomCopy").Enabled&&typeof(MainForm).GetField("roomSharedPassword",flags).GetValue(form)==null,"Local invalid "+(invalidId?"server ID":"server password")+" clears the previous verified copy cache");
     }
     F<TextBox>(form,"ProfileJson").Text=localProfile.Replace("localhost","127.0.0.1");
     Check(!F<Button>(form,"RoomCopy").Enabled&&typeof(MainForm).GetField("roomSharedPassword",flags).GetValue(form)==null,"Changing server binding invalidates room copy and clears the memory-only password");
     foreach(string mismatch in new[]{localProfile.Replace("localhost","127.0.0.1"),localProfile.Replace(new string('A',64),new string('B',64))}){
      F<TextBox>(form,"ProfileJson").Text=mismatch;fixture.ProfileJson=mismatch;
      apply.Invoke(form,new object[]{fixture,ServerProfile.Parse(mismatch),"Aa1!Bb2@Cc3","Synthetic foreign-directory unit"});enabled.Invoke(form,null);
      Check(!F<Button>(form,"RoomCopy").Enabled&&typeof(MainForm).GetField("roomSharedPassword",flags).GetValue(form)==null,"Self-published ID on a different directory URL or pin is not marked shareable");
      F<TextBox>(form,"RoomJoinPassword").Text="Aa1!Bb2@Cc3";F<TextBox>(form,"OwnerSecret").Text=new string('S',43);
      F<Button>(form,"RoomSave").PerformClick();Application.DoEvents();
      Check(F<Button>(form,"RoomSave").Enabled&&F<TextBox>(form,"RoomJoinPassword").Text=="Aa1!Bb2@Cc3"&&F<TextBox>(form,"RoomStatus").Text.Contains("공통"),"Mismatched directory is blocked before network mutation while preserving the entered password");
     }
    }finally{if(oldClipboard==null)Clipboard.Clear();else Clipboard.SetText(oldClipboard);}
    Console.WriteLine("ROOM_UI_PASS count="+checks);result=0;
   }catch(Exception ex){Shot(form,Path.Combine(output,"room-ui-failure.png"));Console.Error.WriteLine("ROOM_UI_FAIL "+ex.GetType().Name+": "+ex.Message);}finally{form.Close();}};
   Application.Run(form);
  }return result;
 }
}
