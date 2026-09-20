using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows.Automation;
using System.Web.Script.Serialization;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WorkCalendar;

public sealed class CaptureResult {
  public string App = "", Title = "", Text = "", Method = "", Error = "";
  public int UiaTextLength, OcrTextLength;
  public bool IsNotification;
  public string Conversation = "";
  public List<CapturedMessage> Messages = new List<CapturedMessage>();
}
public sealed class CapturedMessage { public string Text = "", SentAt = ""; }
class CaptureWorker {
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder text,int count);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT rect);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback,IntPtr parameter);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUT info);
  delegate bool EnumCallback(IntPtr h,IntPtr p);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUT { public uint Size,Time; }
  static string Name(IntPtr h) { uint id; GetWindowThreadProcessId(h,out id); try { return Process.GetProcessById((int)id).ProcessName; } catch { return ""; } }
  static string ImagePath(IntPtr h) { uint id; GetWindowThreadProcessId(h,out id); try { return Process.GetProcessById((int)id).MainModule.FileName; } catch { return ""; } }
  static string Title(IntPtr h) { var b=new StringBuilder(1024); GetWindowText(h,b,b.Capacity); return b.ToString(); }
  static string Class(IntPtr h) { var b=new StringBuilder(256); GetClassName(h,b,b.Capacity); return b.ToString(); }
  static string ReadUia(IntPtr h,CaptureResult result) {
    var root=AutomationElement.FromHandle(h); if(root==null) return "";
    var values=new List<string>(); var unique=new HashSet<string>();
    var queue=new Queue<AutomationElement>(); queue.Enqueue(root);
    var watch=Stopwatch.StartNew(); int visited=0;
    while(queue.Count>0 && visited++<450 && watch.ElapsedMilliseconds<2000) {
      var element=queue.Dequeue();
      try {
        var c=element.Current;
        if(c.IsPassword || c.IsOffscreen) continue;
        if(c.ControlType==ControlType.ListItem) {
          object selection;
          if(element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out selection) && ((SelectionItemPattern)selection).Current.IsSelected) {
            string label=(c.Name ?? "").Split('\n')[0].Trim();
            if(label.Length>0 && label.Length<=80 && !System.Text.RegularExpressions.Regex.IsMatch(label,"今天|明天|后天|[0-9]+[:：][0-9]+")) result.Conversation=label;
          }
        }
        if(c.ControlType==ControlType.Text || c.ControlType==ControlType.ListItem || c.ControlType==ControlType.Document) {
          string name=c.Name;
          if(!string.IsNullOrWhiteSpace(name) && name.Length>=4 && name.Length<2000 && unique.Add(name)) values.Add(name);
          object pattern;
          if(c.ControlType==ControlType.Document && element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) {
            object value; bool editable=element.TryGetCurrentPattern(ValuePattern.Pattern,out value) && !((ValuePattern)value).Current.IsReadOnly;
            if(!editable) { string text=((TextPattern)pattern).DocumentRange.GetText(14000); if(!string.IsNullOrWhiteSpace(text) && unique.Add(text)) values.Add(text); }
          }
        }
        if(c.ControlType==ControlType.Edit) continue;
        var child=TreeWalker.ControlViewWalker.GetFirstChild(element);
        int siblings=0;
        while(child!=null && siblings++<150 && queue.Count<500) { queue.Enqueue(child); child=TreeWalker.ControlViewWalker.GetNextSibling(child); }
      } catch(ElementNotAvailableException) {} catch(InvalidOperationException) {}
    }
    foreach(string value in values) if(value.Length<=6500 && WorkCalendar.CloudTextGate.IsCandidate(value)) {
      string body=System.Text.RegularExpressions.Regex.Replace(value,@"(?im)^\s*(?:消息时间|发送时间|接收时间|Sent|Received)\s*[:：]\s*20\d{2}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}[:：]\d{2}(?:[:：]\d{2})?\s*(?:\r?\n|$)","").Trim();
      if(body.Length>0 && !result.Messages.Any(x=>x.Text==body)) result.Messages.Add(new CapturedMessage { Text=body,SentAt=MessageTime(value) });
    }
    return string.Join("\n",values).Substring(0,Math.Min(24000,string.Join("\n",values).Length));
  }
  static string MessageTime(string text) {
    var matches=System.Text.RegularExpressions.Regex.Matches(text,@"(?:消息时间|发送时间|接收时间|Sent|Received)\s*[:：]\s*(20\d{2}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}[:：]\d{2}(?:[:：]\d{2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    DateTime parsed;
    return matches.Count==1 && DateTime.TryParse(matches[0].Groups[1].Value.Replace('：',':'),out parsed) ? parsed.ToString("o") : "";
  }
  static string Ocr(Bitmap bitmap) {
    OcrEngine engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
    if(engine==null) throw new InvalidOperationException("系统未安装可用 OCR 语言");
    using(var memory=new MemoryStream()) {
      bitmap.Save(memory,ImageFormat.Png); memory.Position=0;
      using(var random=new Windows.Storage.Streams.InMemoryRandomAccessStream()) {
        using(var writer=new Windows.Storage.Streams.DataWriter(random.GetOutputStreamAt(0))) {
          writer.WriteBytes(memory.ToArray()); Wait<uint>(writer.StoreAsync()); writer.DetachStream();
        }
        random.Seek(0);
        var decoder=Wait<BitmapDecoder>(BitmapDecoder.CreateAsync(random));
        using(var software=Wait<SoftwareBitmap>(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied))) {
          var result=Wait<OcrResult>(engine.RecognizeAsync(software));
          return string.Join("\n",result.Lines.Select(line=>string.Concat(line.Words.Select(word=>word.Text))));
        }
      }
    }
  }
  static T Wait<T>(Windows.Foundation.IAsyncOperation<T> operation) {
    var timer=Stopwatch.StartNew();
    while(operation.Status==Windows.Foundation.AsyncStatus.Started && timer.ElapsedMilliseconds<5000) System.Threading.Thread.Sleep(10);
    if(operation.Status!=Windows.Foundation.AsyncStatus.Completed) { operation.Cancel(); throw new InvalidOperationException("OCR 操作未完成：" + operation.Status); }
    return operation.GetResults();
  }
  static string ScreenOcr(IntPtr h) {
    if(GetForegroundWindow()!=h) return "";
    RECT r; if(!GetWindowRect(h,out r)) return "";
    var bounds=Rectangle.Intersect(new Rectangle(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),System.Windows.Forms.SystemInformation.VirtualScreen);
    if(bounds.Width<20 || bounds.Height<20 || bounds.Width>6000 || bounds.Height>4000) return "";
    using(var full=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb)) {
      using(var g=Graphics.FromImage(full)) g.CopyFromScreen(bounds.Left,bounds.Top,0,0,bounds.Size);
      if(GetForegroundWindow()!=h) return "";
      double scale=Math.Min(1.0,2400.0/Math.Max(full.Width,full.Height));
      if(scale==1) return Ocr(full);
      using(var small=new Bitmap(full,new Size((int)(full.Width*scale),(int)(full.Height*scale)))) return Ocr(small);
    }
  }
  static CaptureResult Capture(IntPtr h,bool ocr,bool toast) {
    var result=new CaptureResult { App=Name(h), Title=Title(h), IsNotification=toast };
    if(CapturePolicy.IsExcluded(result.App,result.Title,ImagePath(h))) return new CaptureResult();
    try { result.Text=ReadUia(h,result); result.UiaTextLength=result.Text.Length; result.Method="窗口文字"; } catch(Exception ex) { result.Error="窗口文字："+ex.Message; }
    if(toast && CapturePolicy.IsExcludedNotification(result.Text)) return new CaptureResult();
    if(!toast && GetForegroundWindow()!=h) return new CaptureResult();
    if(ocr && !toast && result.Messages.Count==0) {
      try { string recognized=ScreenOcr(h); result.OcrTextLength=recognized.Length; result.Method="本机 OCR"; if(recognized.Length>0) { result.Text=recognized; result.Messages.Clear(); foreach(string line in recognized.Split('\n')) if(WorkCalendar.CloudTextGate.IsCandidate(line)) result.Messages.Add(new CapturedMessage { Text=line,SentAt="" }); } }
      catch(Exception ex) { result.Error+=" OCR："+ex.Message; }
    }
    return result;
  }
  [STAThread] static int Main(string[] args) {
    Console.OutputEncoding=new UTF8Encoding(false); SetProcessDPIAware();
    try {
      if(args.Contains("--ocr-test")) {
        using(var sampleImage=new Bitmap(1000,250)) {
          using(var canvas=Graphics.FromImage(sampleImage)) {
            canvas.Clear(Color.White);
            using(var sampleFont=new Font("Microsoft YaHei UI",30)) {
              canvas.DrawString("明天上午十点评审\n下周二18:10交付报告",sampleFont,Brushes.Black,new PointF(30,30));
            }
          }
          Console.WriteLine(Ocr(sampleImage));
        }
        return 0;
      }
      var output=new List<CaptureResult>();
      bool ocr=!args.Contains("--no-ocr"), toasts=args.Contains("--toasts");
      string excludes=args.FirstOrDefault(x=>x.StartsWith("--exclude=")) ?? "--exclude=";
      var excluded=new HashSet<string>(excludes.Substring(10).Split(','),StringComparer.OrdinalIgnoreCase);
      excluded.UnionWith(new[]{"WorkCalendar","CaptureWorker","Codex","ChatGPT","LockApp","CredentialUIBroker","LogonUI"});
      var foreground=GetForegroundWindow();
      LASTINPUT last=new LASTINPUT { Size=(uint)Marshal.SizeOf(typeof(LASTINPUT)) }; GetLastInputInfo(ref last);
      bool idle=unchecked((uint)Environment.TickCount-last.Time)>180000;
      string name=Name(foreground),title=Title(foreground);
      string required=args.FirstOrDefault(x=>x.StartsWith("--require-app="));
      if(required!=null && !string.Equals(name,required.Substring(14),StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("当前前台不是指定的程序："+required.Substring(14)); return 2; }
      if(!idle && foreground!=IntPtr.Zero && !excluded.Contains(name) && !IsIconic(foreground) && !CapturePolicy.IsExcluded(name,title,ImagePath(foreground))) output.Add(Capture(foreground,ocr,false));
      if(toasts) {
        var handles=new List<IntPtr>();
        EnumWindows(delegate(IntPtr h,IntPtr p) {
          if(!IsWindowVisible(h) || IsIconic(h) || h==foreground) return true;
          string app=Name(h),cls=Class(h),caption=Title(h);
          RECT r; if(!GetWindowRect(h,out r)) return true;
          bool toast=(app=="ShellExperienceHost" && r.Right-r.Left<1300 && r.Bottom-r.Top<650) || cls.IndexOf("Toast",StringComparison.OrdinalIgnoreCase)>=0;
          if(toast && !excluded.Contains(app) && r.Right-r.Left>50 && r.Bottom-r.Top>50) handles.Add(h);
          return true;
        },IntPtr.Zero);
        foreach(var h in handles.Take(3)) output.Add(Capture(h,ocr,true));
      }
      Console.WriteLine(new JavaScriptSerializer().Serialize(output)); return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
  }
}
