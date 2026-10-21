using System;
using System.Drawing;
using System.Windows.Forms;

namespace PeerDesk.Windows {
    internal sealed class SettingsDialog : Form {
        internal SettingsDialog(TabControl pages,ComboBox languages,ComboBox themes){
            Name="RemoteBridgeSettings";Text=UiLocalization.Text("설정");StartPosition=FormStartPosition.CenterParent;
            ClientSize=new Size(820,680);MinimumSize=new Size(640,480);Font=new Font("Segoe UI",10);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var appearance=new FlowLayoutPanel{Name="AppearanceChoices",AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0,0,0,10)};
            appearance.Controls.Add(Choice("언어",languages));
            appearance.Controls.Add(Choice("화면 모드",themes));
            layout.Controls.Add(appearance,0,0);pages.Dock=DockStyle.Fill;layout.Controls.Add(pages,0,1);
            appearance.Controls.Add(new Label{Text="v"+BuildVersion.Display,AutoSize=true,Margin=new Padding(8,7,0,4)});
            var close=new SoftButton{Name="CloseSettings",Text="닫기",AutoSize=true,DialogResult=DialogResult.Cancel,Anchor=AnchorStyles.Right,Margin=new Padding(0,10,0,0)};
            close.Click+=(s,e)=>Hide();layout.Controls.Add(close,0,2);CancelButton=close;Controls.Add(layout);
            FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
            UiLocalization.Apply(this);
        }
        private static Control Choice(string caption,ComboBox choice){
            var group=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=new Padding(0,0,16,0)};
            group.Controls.Add(new Label{Text=caption,AutoSize=true,Margin=new Padding(0,7,8,4)});
            choice.Margin=new Padding(0,3,0,3);group.Controls.Add(choice);return group;
        }
        internal void ShowPage(Form owner,TabControl pages,TabPage page){
            if(page!=null&&pages.TabPages.Contains(page))pages.SelectedTab=page;
            UiLocalization.Apply(this);if(!Visible)Show(owner);else Activate();
        }
    }
}
