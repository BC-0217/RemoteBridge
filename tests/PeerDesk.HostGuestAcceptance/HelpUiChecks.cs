using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PeerDesk.Windows;
internal static class HelpUiChecks {
 static T F<T>(Control root,string name)where T:Control{return root.Controls.Find(name,true).OfType<T>().Single();}
 static int count;static void Check(bool pass,string text){if(!pass)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
 static bool Fits(Control c){if(!c.Visible||c.Width<1||c.Height<1)return false;var r=c.RectangleToScreen(c.ClientRectangle);for(Control p=c.Parent;p!=null;p=p.Parent)if(!p.RectangleToScreen(p.ClientRectangle).Contains(r)){Console.WriteLine("BOUNDS "+c.Name+" "+r+" ancestor "+p.Name+" "+p.RectangleToScreen(p.ClientRectangle));return false;}return true;}
 static void Shot(Form f,string path){using(var image=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}
 static void Tab(MainForm f,string page){
  var outer=F<TabControl>(f,"ServerSettingsTabs");outer.SelectedTab=outer.TabPages.Cast<TabPage>().Single(p=>p.Name=="ServerSettingsPage");F<ComboBox>(f,"ServerRole").SelectedIndex=1;
  var host=F<TabControl>(f,"HostSettingsTabs");host.SelectedTab=host.TabPages.Cast<TabPage>().Single(p=>p.Name==(page=="HostConnectionPage"?page:"HostOwnerPage"));
  if(page!="HostConnectionPage"){var management=F<TabControl>(f,"OwnerManagementTabs");management.SelectedTab=management.TabPages.Cast<TabPage>().Single(p=>p.Name==page);}f.PerformLayout();
 }
 internal static int Run(string output){
  Directory.CreateDirectory(output);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int result=1;
  var bootstrap=new DirectoryBootstrap("https://localhost:8443",new string('A',64),"owned-help-offline");
  using(var form=new MainForm(Path.Combine(output,"history.json"),Path.Combine(output,"settings-"+Guid.NewGuid().ToString("N")+".json"),bootstrap)){
   form.Shown+=(s,e)=>{try{
    const string key="OWNED_HELP_KEY_SENTINEL_ABCDEFGHIJKLMNOPQRSTUVWXYZ",pw="OWNED_HELP_PASSWORD_SENTINEL";
    F<TextBox>(form,"OwnerSecret").Text=key;F<TextBox>(form,"HostJoinPassword").Text=pw;
    var cases=new[]{
     new[]{"HelpHostConnection","HostConnectionPage","\uC11C\uBC84","\uC9C0\uBB38"},
     new[]{"HelpOwnerKey","OwnerSettingsPage","AWS","SSH"},
     new[]{"HelpDisplayName","OwnerSettingsPage","\uD45C\uC2DC","\uC811\uC18D"},
     new[]{"HelpJoinPassword","OwnerSettingsPage","8","128"},
     new[]{"HelpAliasInput","OwnerPublishingPage","\uC18C\uBB38\uC790","32"},
     new[]{"HelpVerifiedAlias","OwnerPublishingPage","\uD655\uC778","\uBCF5\uC0AC"},
     new[]{"HelpPublishGrant","OwnerPublishingPage","공통","관리자"},
     new[]{"HelpRegisteredDevices","OwnerDevicesPage","\uB4F1\uB85D","\uD574\uC81C"},
     new[]{"HelpServerAddress","HostConnectionPage","https://","Lightsail"},
     new[]{"HelpCertificateFingerprint","HostConnectionPage","SHA-256","openssl"},
     new[]{"HelpSetupFile","HostConnectionPage","파일","자동"},
     new[]{"HelpLegacyInvitation","HostConnectionPage","이전","비밀"},
     new[]{"HelpProfileJson","HostConnectionPage","JSON","비밀번호"},
     new[]{"HelpRoomSettings","RoomSettingsPage","서버 ID","8"}
    };
    foreach(var size in new[]{new Size(760,620),new Size(900,720)}){
     form.Size=size;
     foreach(var item in cases){
      Tab(form,item[1]);F<CheckBox>(form,"ShowAdvancedProfile").Checked=item[0]=="HelpLegacyInvitation"||item[0]=="HelpProfileJson";form.PerformLayout();Application.DoEvents();if(item[0]=="HelpHostConnection"){foreach(var name in new[]{"ServerAddress","ServerFingerprint","CheckServerConnection","ImportSetupFile"})Check(Fits(F<Control>(form,name)),"Basic connection control remains visible "+name+" width="+size.Width);Check(!F<Control>(form,"ServerName").Visible&&!F<Control>(form,"InvitationCode").Visible&&!F<Control>(form,"ProfileJson").Visible,"Local name, legacy invitation and raw JSON are advanced-only");}var button=F<Button>(form,item[0]);if(item[0]=="HelpLegacyInvitation"||item[0]=="HelpProfileJson"||item[0]=="HelpRoomSettings"){for(Control p=button.Parent;p!=null;p=p.Parent){var scroll=p as ScrollableControl;if(scroll!=null&&scroll.AutoScroll)scroll.ScrollControlIntoView(button);}Application.DoEvents();}Check(Fits(button),"Visible help button "+item[0]+" width="+size.Width);button.PerformClick();Application.DoEvents();
      var help=Application.OpenForms.Cast<Form>().Single(f=>f.GetType().Name=="HostHelpForm");Check(help.Visible&&!help.Modal,"Real help form is visible and nonmodal "+item[0]);
      var text=F<TextBox>(help,"HostHelpText");Check(text.ReadOnly&&text.Multiline&&text.ScrollBars!=ScrollBars.None&&Fits(text),"Help text is readonly and visibly scrollable "+item[0]);
      Check(text.Text.Contains(item[2])&&text.Text.Contains(item[3]),"Help explains the expected topic "+item[0]);
      if(item[0]=="HelpOwnerKey"){Check(text.Text.Contains("PEERDESK_OWNER_CONFIG")&&text.Text.Contains("ownerSecret"),"Owner key help names the actual private config source");Check(text.Text.Contains("비공개")&&text.Text.Contains("분실")&&text.Text.Contains("참가"),"Owner key help explains private delivery, loss and participant distinction");}
      Check(!text.Text.Contains(key)&&!text.Text.Contains(pw),"Static help never includes live field secrets "+item[0]);
      var close=F<Button>(help,"CloseHostHelp");Check(Fits(close),"Help close action is visible "+item[0]);if(size.Width==900&&(item[0]=="HelpPublishGrant"||item[0]=="HelpRegisteredDevices"))Shot(help,Path.Combine(output,item[0]+".png"));
      close.PerformClick();Application.DoEvents();Check(help.IsDisposed,"Actual help close disposes the owned help form "+item[0]);
      Check(F<TextBox>(form,"OwnerSecret").Text==key&&F<TextBox>(form,"HostJoinPassword").Text==pw,"Viewing help preserves user-entered values "+item[0]);
     }
    }
    Tab(form,"HostConnectionPage");F<CheckBox>(form,"ShowAdvancedProfile").Checked=false;Application.DoEvents();Check(!F<Control>(form,"HelpProfileJson").Visible&&!F<Control>(form,"HelpLegacyInvitation").Visible&&!F<Control>(form,"InvitationCode").Visible,"Advanced help and invitation hide again after collapse");
    Tab(form,"OwnerSettingsPage");
    F<TextBox>(form,"ProfileJson").Text="{\"Version\":1,\"Name\":\"Owned local validation\",\"RendezvousUrl\":\"https://localhost:8443\",\"RelayUrl\":\"https://localhost:8443\",\"CertificateSha256\":\""+new string('A',64)+"\"}";
    const string participant="Aa1!Bb2@Cc3";
    foreach(var badKey in new[]{"","short-key"}){
     F<TextBox>(form,"HostJoinPassword").Text=participant;F<TextBox>(form,"OwnerSecret").Text=badKey;
     F<Button>(form,"HostSaveSettings").PerformClick();Application.DoEvents();
     Check(F<TextBox>(form,"HostJoinPassword").Text==participant,"Local missing/short management key preserves the 11-character participant password");
     Check(F<TextBox>(form,"OwnerSecret").Focused,"Local management-key error focuses the actual management-key field");
     var error=F<Label>(form,"ConnectionStatus").Text;
     Check(error.Contains("\uAD00\uB9AC\uD0A4")&&error.Contains("\uCC38\uAC00")&&!error.Contains("\uC18C\uC720\uC790 \uBE44\uBC00\uAC12"),"Inline error distinguishes management key from participant password");
     Check(F<Button>(form,"HostSaveSettings").Enabled,"Local key validation leaves the settings editable");
    }
    F<TextBox>(form,"OwnerSecret").Clear();F<TextBox>(form,"HostJoinPassword").Clear();Tab(form,"OwnerPublishingPage");Shot(form,Path.Combine(output,"host-help-buttons.png"));
    Console.WriteLine("HOST_HELP_UI_PASS count="+count);result=0;
   }catch(Exception ex){Shot(form,Path.Combine(output,"help-ui-failure.png"));Console.Error.WriteLine("HOST_HELP_UI_FAIL "+ex.GetType().Name+": "+ex.Message);}finally{form.Close();}};
   Application.Run(form);
  }return result;
 }
}
