using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;

namespace PeerDesk.Windows
{
    internal sealed class CaptureScope
    {
        private readonly bool desktop;
        private readonly IntPtr hwnd;
        private readonly int pid;
        private readonly string monitorDevice; private readonly Rectangle monitorBounds;
        private CaptureScope(string device,Rectangle bounds) {desktop=true;hwnd=IntPtr.Zero;pid=0;monitorDevice=device;monitorBounds=bounds;}
        internal static CaptureScope ForMonitorShare(string device) {
            foreach(var screen in Screen.AllScreens)if(String.Equals(screen.DeviceName,device,StringComparison.Ordinal))return new CaptureScope(device,screen.Bounds);
            throw new InvalidOperationException("선택한 모니터가 연결되어 있지 않습니다.");
        }
        private Rectangle DesktopBounds(){if(monitorDevice==null)return SystemInformation.VirtualScreen;if(!IsCurrent())throw new InvalidOperationException("모니터 구성이 변경되어 화면 공유를 중지합니다.");return monitorBounds;}
        private CaptureScope(bool isDesktop,IntPtr window,int processId) { desktop=isDesktop; hwnd=window; pid=processId; }
        internal static CaptureScope ForDesktopShare() { return new CaptureScope(true,IntPtr.Zero,0); }
        internal static CaptureScope ForOwnedWindow(IntPtr window,int processId)
        {
            if(window==IntPtr.Zero || processId!=Process.GetCurrentProcess().Id || !IsWindow(window)) throw new InvalidOperationException("fixture capture scope must be an owned live window.");
            uint actual; GetWindowThreadProcessId(window,out actual);
            if(actual!=(uint)processId) throw new InvalidOperationException("window/process ownership mismatch.");
            return new CaptureScope(false,window,processId);
        }
        internal bool IsDesktop { get { return desktop; } }
        internal bool ForegroundMatches()
        {
            if(desktop)return true;
            if(!IsCurrent() || GetForegroundWindow()!=hwnd)return false;
            uint actual; GetWindowThreadProcessId(hwnd,out actual);
            return actual==(uint)pid;
        }
        internal bool IsCurrent()
        {
            if(desktop){if(monitorDevice==null)return true;foreach(var screen in Screen.AllScreens)if(screen.DeviceName==monitorDevice)return screen.Bounds==monitorBounds;return false;}
            if(!IsWindow(hwnd))return false;
            uint actual; GetWindowThreadProcessId(hwnd,out actual);
            return actual==(uint)pid && pid==Process.GetCurrentProcess().Id;
        }
        internal Bitmap Capture() { return Capture(1920,1080); }
        internal Bitmap Capture(int maxWidth,int maxHeight)
        {
            if(maxWidth<1||maxWidth>1920||maxHeight<1||maxHeight>1080)throw new ArgumentOutOfRangeException("capture output bounds");
            if(!IsCurrent()) throw new InvalidOperationException("capture scope is no longer valid.");
            Rectangle bounds;
            if(desktop) bounds=DesktopBounds();
            else { RECT r; if(!GetClientRect(hwnd,out r))throw new InvalidOperationException("GetClientRect failed."); bounds=new Rectangle(0,0,r.Right-r.Left,r.Bottom-r.Top); }
            if(bounds.Width<=0||bounds.Height<=0||(long)bounds.Width*bounds.Height>16777216)throw new InvalidOperationException("capture source bounds invalid or exceed 16MP safety cap.");
            double scale=Math.Min(1.0,Math.Min((double)maxWidth/bounds.Width,(double)maxHeight/bounds.Height));
            int w=Math.Max(1,(int)Math.Floor(bounds.Width*scale)), h=Math.Max(1,(int)Math.Floor(bounds.Height*scale));
            if((long)w*h>2073600)throw new InvalidOperationException("capture pixel budget exceeded.");
            var bmp=new Bitmap(w,h,PixelFormat.Format24bppRgb);
            try { using(var g=Graphics.FromImage(bmp)) {
                g.Clear(Color.Black);
                // No resize: capture directly into the outgoing bitmap instead of allocating/copying a second full frame.
                if(w==bounds.Width&&h==bounds.Height){
                    if(desktop)g.CopyFromScreen(bounds.Left,bounds.Top,0,0,bounds.Size,CopyPixelOperation.SourceCopy);
                    else {
                        IntPtr directHdc=g.GetHdc();bool ok;
                        try{ok=PrintWindow(hwnd,directHdc,1);}finally{g.ReleaseHdc(directHdc);}
                        if(!ok)throw new InvalidOperationException("PrintWindow(PW_CLIENTONLY) failed.");
                    }
                    return bmp;
                }
                if(desktop) {
                    using(var full=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb))
                    using(var fg=Graphics.FromImage(full)) {
                        fg.CopyFromScreen(bounds.Left,bounds.Top,0,0,bounds.Size,CopyPixelOperation.SourceCopy);
                        g.DrawImage(full,new Rectangle(0,0,w,h));
                    }
                } else {
                    RECT r; GetClientRect(hwnd,out r);
                    using(var full=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb))
                    using(var fg=Graphics.FromImage(full)) {
                        IntPtr hdc=fg.GetHdc(); bool ok;
                        try { ok=PrintWindow(hwnd,hdc,1); } finally { fg.ReleaseHdc(hdc); }
                        if(!ok)throw new InvalidOperationException("PrintWindow(PW_CLIENTONLY) failed.");
                        g.DrawImage(full,new Rectangle(0,0,w,h));
                    }
                }
            }
            return bmp;
            } catch { bmp.Dispose();throw; }
        }
        internal Rectangle GetInputBounds()
        {
            if(desktop)return DesktopBounds();
            if(!IsCurrent())throw new InvalidOperationException("capture window is no longer valid.");
            RECT r; if(!GetClientRect(hwnd,out r))throw new InvalidOperationException("GetClientRect failed.");
            POINT p=new POINT { X=0,Y=0 }; if(!ClientToScreen(hwnd,ref p))throw new InvalidOperationException("ClientToScreen failed.");
            return new Rectangle(p.X,p.Y,r.Right-r.Left,r.Bottom-r.Top);
        }
        internal bool OwnsPoint(int x,int y)
        {
            if(!IsCurrent())return false;
            var b=GetInputBounds();
            return x>=b.Left&&y>=b.Top&&x<b.Right&&y<b.Bottom;
        }
        internal bool OwnsMouseTarget(int x,int y)
        {
            if(!OwnsPoint(x,y))return false;
            if(desktop)return true;
            IntPtr hit=WindowFromPoint(new POINT { X=x,Y=y });
            if(hit==IntPtr.Zero || GetAncestor(hit,2)!=hwnd)return false;
            uint hitPid; GetWindowThreadProcessId(hit,out hitPid);
            return hitPid==(uint)pid && ForegroundMatches();
        }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X,Y; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd,out uint processId);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool GetClientRect(IntPtr hWnd,out RECT rect);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool ClientToScreen(IntPtr hWnd,ref POINT point);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
    }
}
