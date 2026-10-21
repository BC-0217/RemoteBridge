using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.CompilerServices;

namespace PeerDesk.Windows {
    // Native Button semantics (DialogResult, mnemonics, keyboard and accessibility)
    // are retained; only its surface is drawn here. No timers or background animation.
    internal sealed class SoftButton : Button {
        private bool hovering,pressed,isDefault,dark;
        internal SoftButton(){
            AutoSizeMode=AutoSizeMode.GrowAndShrink;
            Padding=new Padding(10,4,10,4);
            SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        internal void SetTheme(bool value){dark=value;Invalidate();}
        public override void NotifyDefault(bool value){isDefault=value;base.NotifyDefault(value);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){hovering=true;base.OnMouseEnter(e);Invalidate();}
        protected override void OnMouseLeave(EventArgs e){hovering=false;base.OnMouseLeave(e);Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)pressed=true;base.OnMouseDown(e);Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;base.OnMouseUp(e);Invalidate();}
        protected override void OnMouseCaptureChanged(EventArgs e){pressed=false;base.OnMouseCaptureChanged(e);Invalidate();}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space)pressed=true;base.OnKeyDown(e);Invalidate();}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;base.OnKeyUp(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){pressed=false;base.OnLostFocus(e);Invalidate();}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnEnabledChanged(EventArgs e){pressed=false;base.OnEnabledChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e){
            if(SystemInformation.HighContrast||Image!=null){base.OnPaint(e);return;}
            var g=e.Graphics;g.Clear(Parent==null?BackColor:Parent.BackColor);
            if(Width<3||Height<3)return;
            g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale=g.DpiX/96f;
            var rect=new RectangleF(scale/2,scale/2,Width-scale,Height-scale);
            float diameter=Math.Min(14*scale,Math.Min(rect.Width,rect.Height));
            using(var path=new System.Drawing.Drawing2D.GraphicsPath()){
                path.AddArc(rect.Left,rect.Top,diameter,diameter,180,90);
                path.AddArc(rect.Right-diameter,rect.Top,diameter,diameter,270,90);
                path.AddArc(rect.Right-diameter,rect.Bottom-diameter,diameter,diameter,0,90);
                path.AddArc(rect.Left,rect.Bottom-diameter,diameter,diameter,90,90);path.CloseFigure();
                Color fill=dark?Color.FromArgb(40,50,65):Color.FromArgb(237,244,253);
                if(Enabled&&(hovering||pressed))fill=dark?(pressed?Color.FromArgb(58,83,115):Color.FromArgb(48,67,92)):(pressed?Color.FromArgb(208,226,249):Color.FromArgb(222,236,253));
                if(!Enabled)fill=dark?Color.FromArgb(31,38,48):Color.FromArgb(239,241,245);
                using(var brush=new SolidBrush(fill))g.FillPath(brush,path);
                Color edge=Focused||isDefault?(dark?Color.FromArgb(143,190,255):Color.FromArgb(66,119,183)):(dark?Color.FromArgb(78,96,120):Color.FromArgb(193,210,231));
                using(var pen=new Pen(edge,scale))g.DrawPath(pen,path);
            }
            var textRect=Rectangle.Inflate(ClientRectangle,-Padding.Left,-Padding.Top);
            var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak;
            if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;
            if(RightToLeft==RightToLeft.Yes)flags|=TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(g,Text,Font,textRect,Enabled?ForeColor:(dark?Color.FromArgb(151,162,178):Color.FromArgb(105,116,131)),flags);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-5,-5),ForeColor,BackColor);
        }
    }
    internal static class UiTheme {
        private sealed class Meaning {internal string Kind;internal Color Last;}
        private static readonly ConditionalWeakTable<Control,Meaning> meanings=new ConditionalWeakTable<Control,Meaning>();
        private static string Classify(Color color){
            if(color.R>100&&color.R>color.G*1.5&&color.R>color.B*1.5)return "error";
            if(color.R>170&&color.G>65&&color.R>color.B*1.5&&color.G>color.B*1.5)return "warning";
            if(color.G>60&&color.G>color.R*1.35&&color.G>color.B*1.2)return "success";
            return "";
        }
        internal static Color Background(bool dark){return dark?Color.FromArgb(22,26,33):Color.FromArgb(245,247,251);}
        internal static Color Surface(bool dark){return dark?Color.FromArgb(32,39,49):Color.White;}
        internal static Color Foreground(bool dark){return dark?Color.FromArgb(235,240,248):Color.FromArgb(28,38,52);}
        internal static void Apply(Control root,bool dark){
            if(root==null||root.IsDisposed)return;
            var meaning=meanings.GetValue(root,c=>new Meaning{Kind=Classify(c.ForeColor)});
            string current=Classify(root.ForeColor);if(root.ForeColor!=meaning.Last&&current.Length>0)meaning.Kind=current;
            root.BackColor=Background(dark);root.ForeColor=Foreground(dark);
            if(meaning.Kind=="error")root.ForeColor=dark?Color.FromArgb(255,160,160):Color.FromArgb(160,24,34);
            else if(meaning.Kind=="warning")root.ForeColor=dark?Color.FromArgb(255,211,132):Color.FromArgb(130,70,0);
            else if(meaning.Kind=="success")root.ForeColor=dark?Color.FromArgb(146,225,170):Color.FromArgb(0,105,55);
            if(root is TextBoxBase||root is ListBox||root is ComboBox)root.BackColor=Surface(dark);
            var button=root as Button;if(button!=null){button.UseVisualStyleBackColor=false;button.FlatStyle=FlatStyle.Flat;button.BackColor=Surface(dark);button.FlatAppearance.BorderColor=dark?Color.FromArgb(78,96,120):Color.FromArgb(193,210,231);button.FlatAppearance.MouseOverBackColor=dark?Color.FromArgb(48,67,92):Color.FromArgb(222,236,253);button.FlatAppearance.MouseDownBackColor=dark?Color.FromArgb(58,83,115):Color.FromArgb(208,226,249);var soft=button as SoftButton;if(soft!=null)soft.SetTheme(dark);}
            var grid=root as DataGridView;if(grid!=null){
                grid.EnableHeadersVisualStyles=false;grid.BackgroundColor=Surface(dark);
                grid.DefaultCellStyle.BackColor=Surface(dark);grid.DefaultCellStyle.ForeColor=Foreground(dark);
                grid.DefaultCellStyle.SelectionBackColor=dark?Color.FromArgb(48,78,118):Color.FromArgb(210,230,255);
                grid.DefaultCellStyle.SelectionForeColor=dark?Color.White:Color.FromArgb(20,40,70);
                grid.ColumnHeadersDefaultCellStyle.BackColor=Background(dark);grid.ColumnHeadersDefaultCellStyle.ForeColor=Foreground(dark);
            }
            var label=root as Label;if(label!=null&&(label.Name.IndexOf("Warning",StringComparison.OrdinalIgnoreCase)>=0||label.Name=="ExperienceWarning"))
                label.ForeColor=dark?Color.FromArgb(255,207,126):Color.FromArgb(137,65,0);
            meaning.Last=root.ForeColor;
            foreach(Control child in root.Controls)Apply(child,dark);

        }
    }
}
