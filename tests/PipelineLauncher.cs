using System;
using System.Diagnostics;
using System.IO;
class PipelineLauncher {
  [STAThread] static void Main() {
    string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
    Process.Start(new ProcessStartInfo(Path.Combine(root,"WorkCalendar.exe"),"--qa-pipeline") { UseShellExecute=true,WorkingDirectory=root });
  }
}
