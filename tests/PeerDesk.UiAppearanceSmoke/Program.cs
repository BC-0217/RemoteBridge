using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PeerDesk.Windows;

static class Program {
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static T Field<T>(object o,string name){return (T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static IEnumerable<Control> Children(Control c){yield return c;foreach(Control child in c.Controls)foreach(var next in Children(child))yield return next;}
    static bool WithinAncestors(Control control){var rect=control.RectangleToScreen(control.ClientRectangle);for(var p=control.Parent;p!=null;p=p.Parent)if(!p.RectangleToScreen(p.ClientRectangle).Contains(rect))return false;return true;}
    static void Paint(Form form,string path){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}
    [STAThread] static int Main(){
        var watch=Stopwatch.StartNew();
        string dir=Path.Combine(Path.GetTempPath(),"remote-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var deadline=new System.Threading.Timer(_=>{Console.Error.WriteLine("OWN_UI_FIXTURE_DEADLINE_25S");Environment.Exit(124);},null,25000,System.Threading.Timeout.Infinite);
        bool quick=Environment.GetEnvironmentVariable("PEERDESK_UI_QUICK")=="1";
        try{
            Check(UiLocalization.Languages.Select(x=>x.Code).SequenceEqual(new[]{"ko","en","ja","zh-CN","ru","fr","hi"}),"seven supported languages, simplified Chinese only");
            var catalogs=(Dictionary<string,Dictionary<string,string>>)typeof(UiLocalization).GetField("catalogs",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var ko=catalogs["ko"];
            Check(ko.Count>=100,"embedded source catalog loaded");
            foreach(var language in UiLocalization.Languages){
                var catalog=catalogs[language.Code];
                Check(catalog.Count==ko.Count&&catalog.Keys.OrderBy(x=>x).SequenceEqual(ko.Keys.OrderBy(x=>x)),"complete matching keys "+language.Code);
                Check(catalog.Values.All(x=>!String.IsNullOrWhiteSpace(x)),"nonempty translations "+language.Code);
                bool placeholders=true;foreach(var pair in catalog){var original=ko[pair.Key];var a=System.Text.RegularExpressions.Regex.Matches(original,@"\{\d+(?:[^}]*)\}").Cast<System.Text.RegularExpressions.Match>().Select(x=>x.Value).OrderBy(x=>x).ToArray();var b=System.Text.RegularExpressions.Regex.Matches(pair.Value,@"\{\d+(?:[^}]*)\}").Cast<System.Text.RegularExpressions.Match>().Select(x=>x.Value).OrderBy(x=>x).ToArray();placeholders&=a.SequenceEqual(b);}Check(placeholders,"format placeholders preserved "+language.Code);
            }
            UiLocalization.Select("ko",false);
            string settings=Path.Combine(dir,"settings.json");
            using(var form=new MainForm(Path.Combine(dir,"history.json"),settings,null)){
                form.Show();Application.DoEvents();
                var tabs=Field<TabControl>(form,"mainUiTabs");
                var settingsTabs=Field<TabControl>(form,"settingsTabs");
                var dialog=Field<SettingsDialog>(form,"settingsDialog");
                var languages=Field<ComboBox>(form,"languageChoice");
                var themes=Field<ComboBox>(form,"themeChoice");
                var request=Field<Button>(form,"requestButton");
                var target=Field<TextBox>(form,"targetBox");
                var password=Field<TextBox>(form,"targetPasswordBox");
                Check(tabs.TabPages.Count==2&&tabs.TabPages[0].Name=="HomePage"&&tabs.TabPages[1].Name=="SessionManagementPage","main window keeps only home and sessions");
                Check(!Children(form).Contains(settingsTabs)&&settingsTabs.Parent!=null&&!dialog.Visible,"settings removed from main and preserved in hidden separate window");
                Check(languages.DropDownStyle==ComboBoxStyle.DropDownList&&languages.Items.Count==7&&languages.SelectedIndex==0,"language dropdown defaults to Korean");
                Check(themes.DropDownStyle==ComboBoxStyle.DropDownList&&themes.SelectedIndex==0,"theme dropdown defaults to light");
                Check(form.Text.Contains(BuildVersion.Display),"main title displays actual assembly version");
                var menu=Children(form).OfType<Button>().Single(x=>x.Name=="OpenSettingsMenu");menu.PerformClick();Application.DoEvents();
                Check(dialog.Visible&&dialog.Owner==form,"hamburger opens owned settings window");
                Check(settingsTabs.TabPages.Count>=3,"security, server and convenience settings remain available");
                var save=Field<Button>(form,"saveInputsNowButton");
                var forget=Field<Button>(form,"forgetSavedInputsButton");
                var help=Children(dialog).OfType<Button>().Single(x=>x.Name=="HelpSavedInputs");
                int koreanWidth=save.Width;
                Check(save is SoftButton&&save.AutoSizeMode==AutoSizeMode.GrowAndShrink,"soft native buttons resize both ways");
                Check(save.Top==forget.Top&&forget.Top==help.Top,"saved actions share one aligned row");
                using(var probeForm=new Form()){
                    var probe=new SoftButton{Text="Probe",Size=new Size(100,40)};probeForm.Controls.Add(probe);UiTheme.Apply(probeForm,false);probeForm.Show();Application.DoEvents();
                    int clicks=0;probe.Click+=(s,e)=>clicks++;probe.PerformClick();probe.Enabled=false;probe.PerformClick();
                    Check(clicks==1,"soft buttons retain click and disabled semantics");probe.Enabled=true;probe.Focus();
                    typeof(SoftButton).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(probe,new object[]{new KeyEventArgs(Keys.Space)});
                    Check(Field<bool>(probe,"pressed"),"space key displays pressed state");
                    typeof(SoftButton).GetMethod("OnKeyUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(probe,new object[]{new KeyEventArgs(Keys.Space)});
                    Check(!Field<bool>(probe,"pressed")&&probe.Focused&&probe.Region==null,"key release retains focus and rectangular accessible hit area");
                    using(var bitmap=new Bitmap(probe.Width,probe.Height)){probe.DrawToBitmap(bitmap,probe.ClientRectangle);Check(bitmap.GetPixel(0,0).ToArgb()==probeForm.BackColor.ToArgb()&&bitmap.GetPixel(50,5).ToArgb()!=probeForm.BackColor.ToArgb(),"rounded surface leaves soft outer corners");}
                    probeForm.Close();
                }
                target.Text="1234567890";password.Text="private-test-value";
                var ownerName=Field<TextBox>(form,"currentDisplay");var aliasBox=Field<TextBox>(form,"publishedAlias");
                typeof(MainForm).GetMethod("ApplyOwnerSettings",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{new Dictionary<string,object>{{"displayName","설정"},{"joinPasswordConfigured",true}}});
                var profile=ServerProfile.Parse("{\"Version\":1,\"Name\":\"fixture\",\"RendezvousUrl\":\"https://localhost:8443\",\"RelayUrl\":\"https://localhost:8443\",\"CertificateSha256\":\""+new string('A',64)+"\"}");
                typeof(MainForm).GetMethod("SetVerifiedAlias",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{"미확인",profile});
                var room=new OwnerRoomInfo{ServerId="room-test",ServerIds=new List<string>{"room-test"},JoinPasswordConfigured=true,SelfPublishEnabled=true};
                typeof(MainForm).GetMethod("ApplyRoom",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{room,profile,null,"서버에서 확인한 현재 상태입니다."});

                for(int i=0;i<(quick?2:7);i++){
                    var step=Stopwatch.StartNew();languages.SelectedIndex=i;Application.DoEvents();Console.WriteLine("TIMING language="+i+" ms="+step.ElapsedMilliseconds);
                    Check(WithinAncestors(save)&&WithinAncestors(forget)&&WithinAncestors(help),"settings actions stay inside their containers "+i);
                    Check(save.Top==forget.Top&&forget.Top==help.Top,"settings action baselines remain aligned "+i);
                    Check(UiLocalization.Language==UiLocalization.Languages[i].Code,"dropdown applies language immediately "+i);
                    Check(request.Text==UiLocalization.Text("연결 요청"),"existing main button refreshes "+i);
                    Check(WithinAncestors(menu),"hamburger is inside every ancestor viewport "+i);
                    Check(WithinAncestors(request),"connection action is inside every ancestor viewport "+i);
                    Check(Field<TextBox>(form,"deviceBox").Text==UiLocalization.Text("(미등록)"),"unregistered placeholder translates without treating it as an ID "+i);
                    Check(target.Text=="1234567890"&&password.Text=="private-test-value","identifiers and passwords never translated "+i);
                    Check(ownerName.Text=="설정"&&aliasBox.Text=="미확인","server-provided names matching translation keys remain unchanged "+i);
                    Check(Field<TextBox>(form,"roomStatusBox").Text.StartsWith(UiLocalization.Text("서버에서 확인한 현재 상태입니다.")),"open owner summary rerenders without another server request "+i);
                    step.Restart();themes.SelectedIndex=1;Application.DoEvents();Console.WriteLine("TIMING dark="+i+" ms="+step.ElapsedMilliseconds);
                    Check(form.BackColor==UiTheme.Background(true)&&dialog.BackColor==UiTheme.Background(true),"dark theme applies to both open windows "+i);
                    step.Restart();themes.SelectedIndex=0;Application.DoEvents();Console.WriteLine("TIMING light="+i+" ms="+step.ElapsedMilliseconds);
                    Check(form.BackColor==UiTheme.Background(false)&&dialog.BackColor==UiTheme.Background(false),"light theme restores both windows "+i);
                    var reloaded=new UserExperienceOptions(Path.Combine(dir,"experience.json"));
                    Check(reloaded.Language==UiLocalization.Languages[i].Code&&!reloaded.Dark,"language and theme persist together "+i);
                }
                languages.SelectedIndex=0;themes.SelectedIndex=0;Application.DoEvents();
                Check(save.Width==koreanWidth,"long translations do not leave Korean buttons stretched");
                var originalFont=dialog.Font;var originalSize=dialog.ClientSize;
                dialog.ClientSize=new Size(624,441);dialog.Font=new Font(originalFont.FontFamily,originalFont.Size*1.5f);Application.DoEvents();
                Check(WithinAncestors(save)&&WithinAncestors(forget)&&WithinAncestors(help),"settings action layout fits minimum window with 150 percent font");
                dialog.Font=originalFont;dialog.ClientSize=originalSize;Application.DoEvents();
                form.ClientSize=new Size(760,620);form.PerformLayout();Application.DoEvents();
                Check(menu.Visible&&menu.Bounds.Width>0&&menu.FindForm()==form,"hamburger remains reachable at minimum size");
                Check(tabs.ClientSize.Width>500&&tabs.ClientSize.Height>200,"main content remains usable at minimum size");
                Paint(form,Path.Combine(dir,"main-light-ko.png"));Paint(dialog,Path.Combine(dir,"settings-light-ko.png"));
                themes.SelectedIndex=1;languages.SelectedIndex=1;Application.DoEvents();
                Paint(form,Path.Combine(dir,"main-dark-en.png"));Paint(dialog,Path.Combine(dir,"settings-dark-en.png"));
                Check(Directory.GetFiles(dir,"*.png").Length==4,"four owned-window light/dark renders produced");
                languages.SelectedIndex=0;themes.SelectedIndex=0;Application.DoEvents();
                for(int page=0;page<settingsTabs.TabPages.Count;page++){settingsTabs.SelectedIndex=page;Application.DoEvents();Paint(dialog,Path.Combine(dir,"settings-page-"+page+".png"));}
                settingsTabs.SelectedTab=Field<TabPage>(form,"serverPage");Field<ComboBox>(form,"roleBox").SelectedIndex=1;Application.DoEvents();
                Paint(dialog,Path.Combine(dir,"settings-host.png"));
                Field<TabControl>(form,"hostTabs").SelectedIndex=1;Application.DoEvents();
                var hostLoad=Field<Button>(form,"hostLoadButton");var keyHelp=Children(dialog).OfType<Button>().Single(x=>x.Name=="HelpOwnerKey");
                Check(WithinAncestors(hostLoad)&&WithinAncestors(keyHelp),"owner authentication buttons no longer clip in fixed-height row");
                Check(hostLoad.Height>=hostLoad.GetPreferredSize(Size.Empty).Height,"owner authentication action has full preferred height");
                var auth=Children(dialog).OfType<TableLayoutPanel>().Single(x=>x.Name=="OwnerAuthentication");
                var actions=Children(dialog).OfType<FlowLayoutPanel>().Single(x=>x.Name=="OwnerAuthenticationActions");
                Check(auth.GetRowHeights()[1]==actions.GetPreferredSize(new Size(actions.ClientSize.Width,0)).Height,"authentication row has no phantom wrapped blank space");
                dialog.ClientSize=new Size(624,441);
                foreach(int language in new[]{1,4,6}){languages.SelectedIndex=language;Application.DoEvents();Check(WithinAncestors(hostLoad)&&WithinAncestors(keyHelp),"owner actions fit minimum window in language "+language);}
                languages.SelectedIndex=0;dialog.ClientSize=originalSize;Application.DoEvents();
                Paint(dialog,Path.Combine(dir,"settings-host-owner.png"));
                var close=Children(dialog).OfType<Button>().Single(x=>x.Name=="CloseSettings");
                Check(ReferenceEquals(dialog.CancelButton,close)&&close.DialogResult==DialogResult.Cancel,"settings keep native Escape/Cancel contract");
                typeof(Form).GetMethod("ProcessDialogKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{Keys.Escape});Application.DoEvents();
                Check(!dialog.Visible&&!dialog.IsDisposed,"Escape hides settings without destroying controls");
                password.Clear();target.Clear();form.Close();Application.DoEvents();
            }
            string legacy=Path.Combine(dir,"legacy.json");File.WriteAllText(legacy,"{\"version\":1,\"quality\":0,\"peers\":\"[]\"}");
            var old=new UserExperienceOptions(legacy);Check(old.Language=="ko"&&!old.Dark&&old.Warning==null,"version one options migrate without losing existing defaults");
            old.SetAppearance("fr",true);var migrated=new UserExperienceOptions(legacy);Check(migrated.Language=="fr"&&migrated.Dark,"legacy options safely save new appearance schema");
            string invalid=Path.Combine(dir,"invalid.json");File.WriteAllText(invalid,"{\"broken\":true}");var broken=new UserExperienceOptions(invalid);bool refused=false;try{broken.SetAppearance("en",true);}catch(InvalidDataException){refused=true;}
            Check(refused&&File.ReadAllText(invalid)=="{\"broken\":true}","invalid original options are never overwritten");
            UiLocalization.Select("ko",false);
            Console.WriteLine("RENDERS "+dir);Console.WriteLine("PASS total="+count+" elapsedMs="+watch.ElapsedMilliseconds);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{deadline.Dispose();}
    }
}
