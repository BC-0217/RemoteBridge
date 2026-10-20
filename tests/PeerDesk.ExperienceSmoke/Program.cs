using System;using System.IO;using System.Linq;using System.Reflection;using System.Windows.Forms;using PeerDesk.Windows;
internal static class Program {
 static int count;static void Check(bool b,string s){if(!b)throw new Exception(s);Console.WriteLine("PASS "+s);count++;}
 static bool Reject(Action a){try{a();return false;}catch(InvalidDataException){return true;}}
 static void LogScroll(string stage,Form form,FlowLayoutPanel flow,Button rollback){
  var flowScreen=flow.RectangleToScreen(flow.ClientRectangle);var buttonScreen=rollback.RectangleToScreen(rollback.ClientRectangle);var overlap=System.Drawing.Rectangle.Intersect(flowScreen,buttonScreen);
  Console.WriteLine("SCROLL_DIAG stage="+stage+" flowClient="+flow.ClientRectangle+" display="+flow.DisplayRectangle+" autoScroll="+flow.AutoScrollPosition+" verticalVisible="+flow.VerticalScroll.Visible+" verticalValue="+flow.VerticalScroll.Value+" verticalMaximum="+flow.VerticalScroll.Maximum+" verticalLargeChange="+flow.VerticalScroll.LargeChange+" rollbackBounds="+rollback.Bounds+" rollbackClient="+rollback.ClientSize+" rollbackVisible="+rollback.Visible+" rollbackEnabled="+rollback.Enabled+" flowScreen="+flowScreen+" buttonScreen="+buttonScreen+" intersect="+overlap+" activeControl="+(form.ActiveControl==null?"null":form.ActiveControl.Name+"/"+form.ActiveControl.GetType().Name)+" flowContainsFocus="+flow.ContainsFocus);
 }
 static void LayoutCheck(MainForm form,string renderDir){
  var use=(CheckBox)form.Controls.Find("UseSecureSession",true).Single();
  var flow=use.Parent as FlowLayoutPanel;
  Check(flow!=null,"experience options are in a FlowLayoutPanel");
  var page=flow.Parent as TabPage;var tabs=page==null?null:page.Parent as TabControl;
  Check(page!=null&&tabs!=null&&page.Text=="화면·편의","experience page is selectable");
  tabs.SelectedTab=page;form.StartPosition=FormStartPosition.Manual;form.Location=new System.Drawing.Point(40,40);form.Show();Application.DoEvents();
  Directory.CreateDirectory(renderDir);var baseFont=form.Font;
  foreach(var test in new[]{new{W=760,H=620,F=1.0f,Name="760x620-font1"},new{W=760,H=620,F=1.5f,Name="760x620-font1_5"},new{W=900,H=720,F=1.0f,Name="900x720-font1"},new{W=900,H=720,F=1.5f,Name="900x720-font1_5"}}){
   var scaled=new System.Drawing.Font(baseFont.FontFamily,baseFont.Size*test.F,baseFont.Style,baseFont.Unit);
   try{
    form.Font=scaled;form.Size=new System.Drawing.Size(test.W,test.H);tabs.SelectedTab=page;flow.PerformLayout();form.PerformLayout();Application.DoEvents();
    int rightLimit=flow.ClientSize.Width-flow.Padding.Right;
    foreach(Control child in flow.Controls){
     Check(child.Right<=rightLimit,"experience child right bound "+test.Name+" "+child.Name);
     if((child is Label||child is CheckBox)&&!String.IsNullOrWhiteSpace(child.Text)){
      int glyph=child is CheckBox?18:0;var measure=TextRenderer.MeasureText(child.Text,child.Font,new System.Drawing.Size(Math.Max(1,child.ClientSize.Width-glyph),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl);
      if(measure.Height>child.ClientSize.Height){Console.WriteLine("LAYOUT_TEXT_FAIL type="+child.GetType().Name+" name="+child.Name+" textLength="+child.Text.Length+" text="+child.Text+" bounds="+child.Bounds+" client="+child.ClientSize+" measure="+measure+" fontSize="+child.Font.Size+" max="+child.MaximumSize+" flags=WordBreak|TextBoxControl");using(var diagnostic=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(diagnostic,new System.Drawing.Rectangle(0,0,diagnostic.Width,diagnostic.Height));diagnostic.Save(Path.Combine(renderDir,"experience-layout-failure-"+test.Name+".png"),System.Drawing.Imaging.ImageFormat.Png);}}
      Check(measure.Height<=child.ClientSize.Height,"wrapped text fits actual height "+test.Name+" "+child.Text);
     }
    }
    Check(!flow.HorizontalScroll.Visible,"no horizontal scrollbar "+test.Name);
    var rollback=(Button)flow.Controls.Find("RollbackSignedUpdate",true).Single();
    LogScroll("A-before-scroll",form,flow,rollback);
    flow.ScrollControlIntoView(rollback);
    LogScroll("B-after-scroll-before-DoEvents",form,flow,rollback);
    Application.DoEvents();
    LogScroll("C-after-DoEvents",form,flow,rollback);
    var visible=System.Drawing.Rectangle.Intersect(flow.RectangleToScreen(flow.ClientRectangle),rollback.RectangleToScreen(rollback.ClientRectangle));
    if(visible.Width!=rollback.ClientSize.Width||visible.Height!=rollback.ClientSize.Height){using(var diagnostic=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(diagnostic,new System.Drawing.Rectangle(0,0,diagnostic.Width,diagnostic.Height));diagnostic.Save(Path.Combine(renderDir,"experience-scroll-failure-"+test.Name+".png"),System.Drawing.Imaging.ImageFormat.Png);}}
    Check(visible.Width==rollback.ClientSize.Width&&visible.Height==rollback.ClientSize.Height,"rollback button fully visible after scrolling "+test.Name);
    var quality=(ComboBox)form.Controls.Find("QualityChoice",true).Single();int longest=quality.Items.Cast<object>().Select((x,i)=>new{Text=x.ToString(),Index=i}).OrderByDescending(x=>TextRenderer.MeasureText(x.Text,quality.Font).Width).First().Index;
    int previous=quality.SelectedIndex;quality.SelectedIndex=longest;flow.PerformLayout();Application.DoEvents();
    int textWidth=TextRenderer.MeasureText(quality.Text,quality.Font).Width,availableText=Math.Max(0,quality.ClientSize.Width-SystemInformation.VerticalScrollBarWidth);
    Check(textWidth<=availableText,"longest quality item visible in combo "+test.Name+" textWidth="+textWidth+" available="+availableText);quality.SelectedIndex=previous;
    flow.ScrollControlIntoView(flow.Controls[0]);Application.DoEvents();
    using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(renderDir,"experience-layout-"+test.Name+".png"),System.Drawing.Imaging.ImageFormat.Png);}
    if(test.W==760){flow.ScrollControlIntoView(rollback);Application.DoEvents();using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(renderDir,"experience-layout-bottom-font"+test.F.ToString("0.0").Replace('.','_')+".png"),System.Drawing.Imaging.ImageFormat.Png);}}
   }finally{form.Font=baseFont;scaled.Dispose();}
  }
  form.Hide();
 }
 [STAThread]static int Main(){Console.OutputEncoding=new System.Text.UTF8Encoding(false,true);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var root=Path.Combine(Path.GetTempPath(),"RemoteBridge.Experience."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
 try{
 var file=Path.Combine(root,"experience.json");string url="https://localhost:19999/",pin=new string('A',64),id="1234567890";
 var p=new UserExperienceOptions(file);Check(p.Quality==QualityMode.Auto,"default auto");
 p.SetQuality(QualityMode.Fast);p.Set(url,pin,id,"집 컴퓨터",true);var loaded=new UserExperienceOptions(file);
 Check(loaded.Quality==QualityMode.Fast,"quality persisted");Check(loaded.Find(url,pin,id).Alias=="집 컴퓨터"&&loaded.Find(url,pin,id).Favorite,"alias favorite persisted");
 Check(loaded.Find(url,new string('B',64),id)==null,"full pin isolated");Check(loaded.Find("https://other/",pin,id)==null,"server isolated");
 var copy=loaded.Find(url,pin,id);copy.Alias="mutated";Check(loaded.Find(url,pin,id).Alias=="집 컴퓨터","defensive bookmark");
 Check(Reject(()=>loaded.Set(url,pin,id,"bad\nname",false)),"control chars rejected");
 Check(Reject(()=>loaded.Set(url,pin,id,new string('x',41),false)),"long aliases rejected");
 Check(Reject(()=>loaded.Set("http://localhost/",pin,id,"x",false)),"insecure bookmark origin rejected");
 var historyPath=Path.Combine(root,"history.json");var settings=Path.Combine(root,"settings.json");
 using(var form=new MainForm(historyPath,settings,null)){
 Check(((CheckBox)form.Controls.Find("UseSecureSession",true).Single()).Checked,"secure sessions default on");
    Check(!((CheckBox)form.Controls.Find("AllowSessionText",true).Single()).Checked&&!((CheckBox)form.Controls.Find("AllowSessionFiles",true).Single()).Checked,"optional text file permissions default off");
    Check(!((CheckBox)form.Controls.Find("UseDirectScreen",true).Single()).Checked&&!((CheckBox)form.Controls.Find("RetryNetworkConnection",true).Single()).Checked,"direct and reconnect require opt in");
    Check(form.Controls.Find("VerifySignedUpdate",true).Length==1&&form.Controls.Find("RollbackSignedUpdate",true).Length==1,"signed update explicit user actions wired");
    Check(MainForm.IsRecoverableNetworkFailure(new System.Net.WebException("synthetic",System.Net.WebExceptionStatus.ConnectionClosed)),"confirmed network failure eligible");
    Check(!MainForm.IsRecoverableNetworkFailure(new System.Net.WebException("synthetic",System.Net.WebExceptionStatus.TrustFailure)),"TLS trust failure not reconnectable");
    Check(!MainForm.IsRecoverableNetworkFailure(new System.Security.Cryptography.CryptographicException("synthetic",new System.Net.WebException("synthetic",System.Net.WebExceptionStatus.Timeout))),"crypto failure cannot hide behind network inner exception");
    Check(!MainForm.IsRecoverableNetworkFailure(new System.IO.EndOfStreamException()),"unclassified EOF not reconnectable");
    var quality=(ComboBox)form.Controls.Find("QualityChoice",true).Single();
 Check(quality.SelectedIndex==2,"UI restores saved fast");
 quality.SelectedIndex=1;Check(new UserExperienceOptions(file).Quality==QualityMode.Sharp,"UI quality change saves");
 var h=(ConnectionHistory)typeof(MainForm).GetField("history",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
 h.Add(url,pin,id,"Connected");var profile=ServerProfile.Parse("{\"Version\":1,\"Name\":\"test\",\"RendezvousUrl\":\""+url+"\",\"RelayUrl\":\""+url+"\",\"CertificateSha256\":\""+pin+"\"}");
 typeof(MainForm).GetField("client",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,new ControlPlaneClient(profile));
 typeof(MainForm).GetMethod("UpdateHistoryList",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
 var list=(ListBox)typeof(MainForm).GetField("historyList",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
 Check(list.Items.Count==1&&list.Items[0].ToString().StartsWith("★ 집 컴퓨터 [1234567890]"),"UI shows alias favorite without changing ID");
 Check(!File.ReadAllText(historyPath).Contains("집 컴퓨터"),"cosmetic alias not duplicated into credential/history storage");
 LayoutCheck(form,Path.GetFullPath("artifacts/build/phase28-ui"));
 var monitors=(ComboBox)form.Controls.Find("SharedMonitorChoice",true).Single();
 Check(monitors.Items.Count==Screen.AllScreens.Length+1,"monitor choices enumerate current displays");
 monitors.SelectedIndex=1;
 var scope=(CaptureScope)typeof(MainForm).GetField("autoCaptureScope",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
 Check(scope.GetInputBounds()==Screen.AllScreens[0].Bounds,"selected monitor input origin and bounds match metadata");
 var method=typeof(CaptureScope).GetMethod("ForMonitorShare",BindingFlags.NonPublic|BindingFlags.Static);
 bool missing=false;try{method.Invoke(null,new object[]{"missing-monitor-device"});}catch(TargetInvocationException ex){missing=ex.InnerException is InvalidOperationException;}Check(missing,"missing monitor fails closed");
 using(var view=new ViewerForm(new ControlPlaneClient(profile),"synthetic-session")){
     view.Display.Image=new System.Drawing.Bitmap(640,360);view.ClientSize=new System.Drawing.Size(900,650);view.PerformLayout();
     var zoom=(ComboBox)view.Controls.Find("ViewerZoom",true).Single();
     zoom.SelectedIndex=1;Check(view.Display.Size==new System.Drawing.Size(640,360),"100 percent image dimensions");
     zoom.SelectedIndex=4;Check(view.Display.Size==new System.Drawing.Size(1280,720),"200 percent image dimensions");
     zoom.SelectedIndex=0;Check(view.Display.Dock==DockStyle.Fill,"fit mode restores dock mapping");
     Check(view.Controls.Find("ViewerStop",true).Length==1,"visible viewer stop control");
     view.Display.Image.Dispose();view.Display.Image=null;
 }
 int stopped=0;using(var bar=new ShareIndicatorForm("1234567890",()=>stopped++)){
     bar.Show();Application.DoEvents();
     ((Button)bar.Controls.Find("StopActiveShare",true).Single()).PerformClick();
     ((Button)bar.Controls.Find("StopActiveShare",true).Single()).PerformClick();
     Check(stopped==1,"share indicator stop is one shot");bar.Dismiss();
 }Check(stopped==1,"programmatic indicator cleanup is not another stop");
 }
 loaded.Set(url,pin,id,"",false);Check(new UserExperienceOptions(file).Find(url,pin,id)==null,"clear bookmark persisted");
 File.WriteAllText(file,"broken");var bad=new UserExperienceOptions(file);Check(bad.Warning!=null,"corruption visible");
 Check(Reject(()=>bad.SetQuality(QualityMode.Fast))&&File.ReadAllText(file)=="broken","corrupt original never overwritten");
 Console.WriteLine("EXPERIENCE_SMOKE_PASS count="+count);return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}
 finally { foreach(var file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root); }
 }
}