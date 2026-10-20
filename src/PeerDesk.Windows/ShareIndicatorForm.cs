using System;
using System.Drawing;
using System.Windows.Forms;
namespace PeerDesk.Windows {
    internal sealed class ShareIndicatorForm:Form {
        private bool finished,requested;
        private readonly Action stop;
        internal Func<System.Threading.Tasks.Task> SendText,SendFile;
        protected override bool ShowWithoutActivation {get{return true;}}
        internal ShareIndicatorForm(string peer,Action stopAction,bool inputAllowed=true){
            stop=stopAction;Name="ActiveShareIndicator";Text="RemoteBridge — 공유 중";TopMost=true;ShowInTaskbar=false;
            FormBorderStyle=FormBorderStyle.FixedToolWindow;MaximizeBox=false;MinimizeBox=false;ClientSize=new Size(480,104);
            StartPosition=FormStartPosition.Manual;var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(Math.Max(area.Left,area.Right-Width-12),area.Top+12);
            var message=new Label{Name="SharingPeer",Text="● "+peer+(inputAllowed?" 님에게 화면·마우스·키보드 공유 중":" 님에게 화면만 공유 중"),AutoSize=false,Left=10,Top=10,Width=365,Height=42};
            var button=new Button{Name="StopActiveShare",Text="공유 중지",ForeColor=Color.DarkRed,Left=380,Top=14,Width=85,Height=32};
            button.Click+=(s,e)=>{if(requested)return;requested=true;button.Enabled=false;stop();};
            FormClosing+=(s,e)=>{if(finished)return;e.Cancel=true;if(!requested){requested=true;button.Enabled=false;stop();}};
            Controls.Add(message);Controls.Add(button);
            var text=new Button{Name="ShareSendText",Text="텍스트 보내기",Left=10,Top=64,Width=125};
            var file=new Button{Name="ShareSendFile",Text="파일 보내기",Left=145,Top=64,Width=125};
            text.Click+=async(s,e)=>{text.Enabled=false;try{if(SendText!=null)await SendText();}finally{if(!text.IsDisposed)text.Enabled=true;}};
            file.Click+=async(s,e)=>{file.Enabled=false;try{if(SendFile!=null)await SendFile();}finally{if(!file.IsDisposed)file.Enabled=true;}};
            Controls.Add(text);Controls.Add(file);
        }
        internal void Dismiss(){if(IsDisposed)return;finished=true;Close();Dispose();}
    }
}
