using System;
using System.Windows.Forms;
namespace PeerDesk.Windows {
 internal static class Program {
  [STAThread] private static int Main(string[] args) {
   if(args.Length==1&&args[0]=="--apply-signed-update")return SignedUpdateCoordinator.RunHelper();
   if(args.Length!=0)return 2;
   // Local diagnostic log for the ordinary app only (never the signed update helper above); bounded flush on normal exit.
   DiagnosticLog.Initialize();
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   Application.Run(new MainForm());
   DiagnosticLog.Shutdown(1500);return 0;
  }
 }
}
