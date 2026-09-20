using System;
using System.Diagnostics;
using System.IO;
class DemoLauncher {
  [STAThread] static void Main() {
    string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
    Process.Start(new ProcessStartInfo(Path.Combine(root,"WorkCalendar.exe"),"--demo --render") { UseShellExecute=true,WorkingDirectory=root });
  }
}
