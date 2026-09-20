using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WorkCalendar {
  public sealed class IncomingLine { public string Text="",SentAt=""; }
  public sealed class Frame { public string App,Title,Text,Method,Error,Conversation; public bool IsNotification; public List<IncomingLine> Messages=new List<IncomingLine>(); }
  public class DesktopApp {
    Window window; Store store; Forms.NotifyIcon tray;
    DateTime selectedDay=DateTime.Today, month=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
    string selectedId=""; bool capturing,busy,quitting; int generation;
    DispatcherTimer captureTimer,reminderTimer;
    Process worker; HashSet<string> openReminders=new HashSet<string>();
    bool cloudBusy; DateTime nextCloud=DateTime.MinValue;
    CancellationTokenSource cloudCancel=new CancellationTokenSource();
    bool qaPipeline; Process qaFixture; DateTime qaStarted; string qaDirectory=""; int displayedReminders;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
    string basePath=AppDomain.CurrentDomain.BaseDirectory; bool demo;
    static Mutex mutex;
    T Find<T>(string name) where T:class { return window.FindName(name) as T; }
    static SolidColorBrush Brush(string s) { return (SolidColorBrush)new BrushConverter().ConvertFromString(s); }
    static TextBlock Text(string text,double size=13,string color="#294849") { return new TextBlock { Text=text,FontSize=size,Foreground=Brush(color),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10) }; }
    static Button Button(string label,Action action) { var b=new Button { Content=label,Margin=new Thickness(0,0,0,10),HorizontalAlignment=HorizontalAlignment.Stretch }; b.Click+=(s,e)=>action(); return b; }
    void Status(string text) { Find<TextBlock>("Footer").Text=text; }
    [STAThread] public static void Main(string[] args) {
      try {
        bool owns; string scope=args.Contains("--qa-pipeline") ? "Pipeline" : args.Contains("--demo") ? "Demo" : args.Contains("--reminder-test") ? "ReminderTest" : "Main";
        mutex=new Mutex(true,"Local\\WorkCalendar_"+scope,out owns);
        if(!owns) { MessageBox.Show("知时已在运行，可双击系统托盘图标打开。","知时"); return; }
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException+=(s,e)=>{ MessageBox.Show(e.Exception.Message,"知时 · 操作未完成"); e.Handled=true; };
        new DesktopApp().Run(app,args);
        mutex.ReleaseMutex();
      } catch(Exception ex) { MessageBox.Show(ex.ToString(),"知时 · 启动失败"); }
    }
    void Run(Application app,string[] args) {
      qaPipeline=args.Contains("--qa-pipeline"); demo=args.Contains("--demo") || args.Contains("--reminder-test");
      string directory=Path.Combine(basePath,qaPipeline ? "qa\\pipeline-"+DateTime.Now.ToString("yyyyMMdd-HHmmss") : demo ? "qa\\"+(args.Contains("--reminder-test") ? "reminder-session" : "demo-session") : "data"); qaDirectory=directory;
      store=new Store(directory);
      int blockedJobs=store.Data.CloudQueue.RemoveAll(x=>CapturePolicy.IsExcluded(x.Source,x.Window,""));
      foreach(var message in store.Data.Messages.Where(x=>CapturePolicy.IsExcluded(x.Source,x.Window,""))) message.State="ignored";
      if(blockedJobs>0) store.Save();
      using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window=(Window)XamlReader.Load(stream);
      app.MainWindow=window;
      AddSettingsControls();
      if(demo) { window.Title+=" · 演示数据"; Seed(args.Contains("--reminder-test")); }
      Find<TextBlock>("HeaderDate").Text=DateTime.Today.ToString("yyyy年 M月 d日  dddd",CultureInfo.GetCultureInfo("zh-CN"))+(demo ? "  ·  演示模式，独立数据" : "  ·  你的工作日历");
      Find<Button>("ToggleCapture").Click+=(s,e)=>Toggle();
      Find<Button>("AddMessage").Click+=(s,e)=>InputMessage();
      Find<Button>("Export").Click+=(s,e)=>Export();
      Find<Button>("PrevMonth").Click+=(s,e)=>{ month=month.AddMonths(-1); Refresh(); };
      Find<Button>("NextMonth").Click+=(s,e)=>{ month=month.AddMonths(1); Refresh(); };
      Find<Button>("Today").Click+=(s,e)=>{ selectedDay=DateTime.Today; month=new DateTime(selectedDay.Year,selectedDay.Month,1); Refresh(); };
      Find<Button>("NavCalendar").Click+=(s,e)=>Page("Calendar");
      Find<Button>("NavInbox").Click+=(s,e)=>Page("Inbox");
      Find<Button>("NavLogs").Click+=(s,e)=>Page("Logs");
      Find<Button>("NavSettings").Click+=(s,e)=>Page("Settings");
      Find<Button>("SaveSettings").Click+=(s,e)=>SaveSettings();
      Find<Button>("Quit").Click+=(s,e)=>Quit();
      LoadSettings();
      tray=new Forms.NotifyIcon { Icon=System.Drawing.SystemIcons.Information,Visible=true,Text="知时 · 工作日历（采集已暂停）" };
      var menu=new Forms.ContextMenuStrip(); menu.Items.Add("打开日历",null,(s,e)=>Show()); menu.Items.Add("开始 / 暂停记录",null,(s,e)=>Toggle()); menu.Items.Add("彻底退出",null,(s,e)=>Quit()); tray.ContextMenuStrip=menu; tray.DoubleClick+=(s,e)=>Show();
      window.Closing+=(s,e)=>{ if(!quitting) { e.Cancel=true; window.Hide(); tray.ShowBalloonTip(2500,"知时仍在运行","日历提醒继续运行。双击托盘图标打开，右键可彻底退出。",Forms.ToolTipIcon.Info); } };
      window.SourceInitialized+=(s,e)=>{
        var handle=new WindowInteropHelper(window).Handle;
        if(RegisterHotKey(handle,701,0x0001|0x0002,0x20)) HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled) { if(message==0x0312 && w.ToInt32()==701) { CaptureTick(true); handled=true; } return IntPtr.Zero; });
      };
      captureTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(8) }; captureTimer.Tick+=(s,e)=>CaptureTick(); captureTimer.Start();
      reminderTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) }; reminderTimer.Tick+=(s,e)=>{ Reminders(); CloudTick(); }; reminderTimer.Start();
      if(args.Contains("--render")) window.ContentRendered+=(s,e)=>RenderPreview();
      if(qaPipeline) window.ContentRendered+=(s,e)=>StartPipelineCheck();
      Refresh(); Page(args.Contains("--settings") ? "Settings" : "Calendar"); window.Show(); app.Run();
    }
    void Seed(bool reminder) {
      if(store.Data.Events.Count>0) return;
      DateTime now=DateTime.Now;
      store.Ingest("明天下午三点开会，讨论 SDK 联调进度","示例消息","演示项目",now,true);
      store.Ingest("明天下午拉会，核对客户问题","示例消息","客户沟通",now,true);
      store.Ingest("后天下午五点提交测试报告","示例消息","版本交付",now,true);
      store.Ingest("会议改到后天下午四点","示例消息","演示项目",now,false);
      store.Data.Logs.Add(new WorkLog { App="示例编辑器",Title="SDK 联调记录",Summary="示例工作轨迹，不是实际采集",StartedAt=now.AddMinutes(-35).ToString("o"),EndedAt=now.ToString("o"),Seconds=35*60 });
      if(reminder) { var e=Parser.Parse("今天23:59开会",now,true); Store.Reschedule(e,now.AddMinutes(1)); e.Title="提醒链路验证（测试数据）"; e.Source="自测"; e.ObservedAt=now.ToString("o"); store.Data.Events.Add(e); }
      store.Save(); selectedDay=DateTime.Today.AddDays(1); month=new DateTime(selectedDay.Year,selectedDay.Month,1); selectedId=store.Data.Events[0].Id;
    }
    void Show() { window.Show(); window.WindowState=WindowState.Normal; window.Activate(); }
    void Quit() {
      capturing=false; generation++; quitting=true; cloudCancel.Cancel();
      captureTimer.Stop(); reminderTimer.Stop();
      try { if(worker!=null && !worker.HasExited) worker.Kill(); } catch {}
      UnregisterHotKey(new WindowInteropHelper(window).Handle,701);
      store.Save(); tray.Dispose(); Application.Current.Shutdown();
    }
    void Page(string page) {
      foreach(string name in new[]{"Calendar","Inbox","Logs","Settings"}) {
        Find<FrameworkElement>(name+"Page").Visibility=name==page ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("Nav"+name).Background=Brush(name==page ? "#356153" : "#203C40");
      }
      RefreshLists();
    }
    void Toggle() {
      capturing=!capturing; generation++; if(!capturing) { cloudCancel.Cancel(); cloudCancel=new CancellationTokenSource(); }
      Find<TextBlock>("CaptureState").Text=capturing ? "正在留意工作消息" : "采集已暂停";
      Find<TextBlock>("CaptureDetail").Text=capturing ? "读取前台文字与可见通知 · "+(store.Data.Settings.CloudEnabled ? "候选工作文字将发送至配置的云端 API" : "基础时间规则运行中，云端推理未启用") : "点击开始记录，自动读取前台文字与可见通知；日历提醒保持运行。";
      Find<Button>("ToggleCapture").Content=capturing ? "暂停记录" : "开始记录";
      tray.Text=capturing ? "知时 · 正在记录工作" : "知时 · 采集已暂停";
      Status(capturing ? "采集已开启 · 请切换到需要记录的工作窗口" : "已暂停采集 · 进行中的采集结果也会丢弃");
      if(capturing) CaptureTick();
    }
    async void CaptureTick(bool onDemand=false) {
      if((!capturing && !onDemand) || busy) return;
      busy=true; int epoch=generation;
      var settings=store.Data.Settings;
      try {
        var frames=await Task.Run(()=>ReadFrames(settings));
        if((!capturing && !onDemand) || epoch!=generation || quitting) return;
        int added=0, updated=0,received=0; string last="";
        foreach(Frame frame in frames) {
          if(string.IsNullOrEmpty(frame.App) || CapturePolicy.IsExcluded(frame.App,frame.Title,"")) continue;
          if(frame.IsNotification && CapturePolicy.IsExcludedNotification(frame.Text)) continue;
          if(qaPipeline && frame.App!="TestMessage") continue;
          var lines=frame.Messages ?? new List<IncomingLine>();
          if(lines.Count==0) lines=Cloud.CandidateLines(frame.Text).Select(x=>new IncomingLine { Text=x }).ToList();
          foreach(var line in lines.Take(16)) {
            DateTime sent; bool known=DateTime.TryParse(line.SentAt,out sent);
            var message=Workflow.Receive(store,frame.App,frame.Title ?? "",frame.Conversation ?? "",line.Text,DateTime.Now,known ? (DateTime?)sent : null);
            if(message==null) continue; received++;
            if(store.Data.Settings.CloudEnabled) QueueCloud(message);
            else { var result=Workflow.Local(store,message); added+=result.Created; updated+=result.Updated; if(result.EventIds.Count>0) selectedId=result.EventIds.Last(); }
          }
          if(!frame.IsNotification) store.RecordActivity(frame.App,frame.Title ?? "",DateTime.Now,8);
          last=frame.App+" · "+frame.Method;
          if(!string.IsNullOrEmpty(frame.Error)) last+=" · 部分内容未读到";
        }
        if(frames.Count>0) store.Save();
        Status(added+updated>0 ? "自动整理：新增 "+added+" 项，更新 "+updated+" 项" : received>0 ? "已记录 "+received+" 条新工作消息"+(store.Data.Settings.CloudEnabled ? "，正在等待语义分析" : "") : last.Length>0 ? "最近查看："+last+"  /  "+DateTime.Now.ToString("HH:mm:ss") : "等待工作窗口 · 自身、登录窗口和空闲状态不采集");
        if(added+updated>0) { tray.ShowBalloonTip(3500,"工作安排已更新","新增 "+added+" 项，更新 "+updated+" 项。日历中保留了原文和变更记录。",Forms.ToolTipIcon.Info); }
        if(received>0) { Refresh(); if(store.Data.Settings.CloudEnabled) CloudTick(onDemand); } else RefreshCounts();
        if(onDemand) { Show(); Refresh(); }
      } catch(Exception ex) { Status("本次采集未完成："+ex.Message); }
      finally { busy=false; }
    }
    List<Frame> ReadFrames(Settings settings) {
      string excluded=string.Join(",",settings.ExcludedApps.Split(',').Select(x=>x.Trim()).Where(x=>Regex.IsMatch(x,@"^[\w.\-]+$")));
      var start=new ProcessStartInfo(Path.Combine(basePath,"CaptureWorker.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,Arguments=(settings.OcrEnabled ? "" : "--no-ocr ")+(settings.ToastsEnabled ? "--toasts " : "")+"--exclude="+excluded };
      using(var process=new Process { StartInfo=start }) {
        worker=process; process.Start(); var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(14000)) { process.Kill(); throw new TimeoutException("窗口读取超时，下一轮将重试"); }
        if(process.ExitCode!=0) throw new Exception(errors.Result.Trim());
        return new JavaScriptSerializer { MaxJsonLength=512000 }.Deserialize<List<Frame>>(output.Result) ?? new List<Frame>();
      }
    }
    void RefreshCounts() {
      Find<TextBlock>("ActiveCount").Text=store.Data.Events.Count(x=>x.Status=="active").ToString();
      Find<TextBlock>("PendingCount").Text=store.Data.Events.Count(x=>x.Status=="pending").ToString();
      Find<TextBlock>("LogCount").Text=store.Data.Messages.Count(x=>x.State!="ignored" && x.ObservedAt.StartsWith(DateTime.Today.ToString("yyyy-MM-dd"))).ToString();
    }
    void Refresh() {
      RefreshCounts();
      Find<TextBlock>("MonthTitle").Text=month.ToString("yyyy年 M月");
      var grid=Find<UniformGrid>("MonthGrid"); grid.Children.Clear();
      DateTime first=month.AddDays(-((int)month.DayOfWeek+6)%7);
      for(int i=0;i<42;i++) {
        DateTime day=first.AddDays(i); string date=day.ToString("yyyy-MM-dd");
        var events=store.Data.Events.Where(x=>x.Date==date && x.Status!="cancelled" && x.Status!="done").ToList();
        var content=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text=day.Day.ToString(),HorizontalAlignment=HorizontalAlignment.Center,FontSize=14,FontWeight=day==DateTime.Today ? FontWeights.Bold : FontWeights.Normal,Foreground=Brush(day==selectedDay ? "#FFFFFF" : day.Month==month.Month ? "#2B4B44" : "#B6C2B9") });
        content.Children.Add(new TextBlock { Text=events.Count>0 ? events.Count+" 项" : " ",HorizontalAlignment=HorizontalAlignment.Center,FontSize=9,Margin=new Thickness(0,3,0,0),Foreground=Brush(day==selectedDay ? "#DCEDE1" : events.Any(x=>x.Status=="pending") ? "#AD844C" : "#2D8870") });
        var button=new Button { Content=content,Padding=new Thickness(1),Margin=new Thickness(2),Background=Brush(day==selectedDay ? "#2F7663" : day==DateTime.Today ? "#EAF2EB" : "#FFFFFF"),BorderBrush=Brush("#FFFFFF"),ToolTip=date+(events.Count>0 ? "\n"+string.Join("\n",events.Select(x=>x.Title)) : "") };
        button.Click+=(s,e)=>{ selectedDay=day; var item=store.Data.Events.FirstOrDefault(x=>x.Date==date && x.Status!="cancelled"); selectedId=item==null ? "" : item.Id; Refresh(); };
        grid.Children.Add(button);
      }
      Find<TextBlock>("SelectedDateLabel").Text=selectedDay.ToString("M月d日  dddd",CultureInfo.GetCultureInfo("zh-CN"));
      var panel=Find<StackPanel>("DayEvents"); panel.Children.Clear();
      var dayEvents=store.Data.Events.Where(x=>x.Date==selectedDay.ToString("yyyy-MM-dd") && x.Status!="cancelled").OrderBy(x=>x.Time).ToList();
      if(dayEvents.Count==0) panel.Children.Add(Text("当天还没有日程。识别到的安排会自动出现在这里。",12,"#839386"));
      foreach(var e in dayEvents) panel.Children.Add(EventCard(e,false));
      RefreshDetails(); RefreshLists();
    }
    UIElement EventCard(WorkEvent item,bool full) {
      var panel=new StackPanel();
      panel.Children.Add(Text((full ? item.When : item.Time.Length>0 ? item.Time : item.Period.Length>0 ? item.Period : "时间待定")+"   ·   "+item.StateLabel,11,item.Status=="pending" ? "#AA7B3C" : "#3A8169"));
      panel.Children.Add(Text(item.Title,13));
      if(full) panel.Children.Add(Text(item.Source+"  /  "+item.Warning,11,"#7B8C81"));
      var b=new Button { Content=panel,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(12,10,12,5),Margin=new Thickness(0,0,0,9),Background=Brush(item.Id==selectedId ? "#EFF5EF" : "#FAFCF9") };
      b.Click+=(s,e)=>{ selectedId=item.Id; DateTime date; if(DateTime.TryParse(item.Date,out date)) { selectedDay=date; month=new DateTime(date.Year,date.Month,1); } Page("Calendar"); Refresh(); };
      return b;
    }
    void RefreshDetails() {
      var p=Find<StackPanel>("Details"); p.Children.Clear();
      var e=store.Data.Events.FirstOrDefault(x=>x.Id==selectedId);
      if(e==null) {
        p.Children.Add(Text("从一条消息开始",20)); p.Children.Add(Text("日历会记住明确的安排，也会替你留住尚待确认的事。",13,"#758D7D"));
        p.Children.Add(Text("01  开始记录",14)); p.Children.Add(Text("切换到工作软件。前台文字与可见通知会在本机识别。",12,"#758D7D"));
        p.Children.Add(Text("02  自动记入日历",14)); p.Children.Add(Text("“明天下午拉会”会记在明天，具体时间留待核对。",12,"#758D7D"));
        p.Children.Add(Text("03  到点提醒",14)); p.Children.Add(Text("核对具体时间后，默认提前 15 分钟弹窗。",12,"#758D7D"));
        p.Children.Add(Button("录入消息，体验识别",InputMessage)); return;
      }
      p.Children.Add(Text(e.Kind+"  /  "+e.StateLabel,12,"#43816C")); p.Children.Add(Text(e.Title,18)); p.Children.Add(Text(e.When,14));
      if(e.Status=="active") p.Children.Add(Text("提前 "+e.ReminderMinutes+" 分钟提醒",12,"#718C7B"));
      if(e.Status=="pending" && e.ReviewAt.Length>0) p.Children.Add(Text("将在 "+FormatTime(e.ReviewAt)+" 提醒核对具体安排",12,"#94743C"));
      if(e.Warning.Length>0) p.Children.Add(new Border { Background=Brush("#FCF3E4"),Padding=new Thickness(12),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,4,0,16),Child=Text(e.Warning,12,"#94743C") });
      p.Children.Add(Text("消息依据",12,"#8A988C")); p.Children.Add(Text(e.Evidence,13)); p.Children.Add(Text("来源："+e.Source+"\n窗口："+e.Window+"\n首次记录："+FormatTime(e.ObservedAt),11,"#8A988C"));
      if(e.Preparations.Count>0) {
        p.Children.Add(Text("准备事项 · 可勾选完成",12,"#43816C"));
        foreach(var step in e.Preparations) { var check=new CheckBox { Content=Text(step.Text,12),IsChecked=step.Done,Margin=new Thickness(0,0,0,6) }; check.Checked+=(s,a)=>{ step.Done=true; store.Save(); }; check.Unchecked+=(s,a)=>{ step.Done=false; store.Save(); }; p.Children.Add(check); }
        p.Children.Add(Button("把准备事项安排进日历",()=>SchedulePreparation(e)));
      } else if(e.Suggestion.Length>0) { p.Children.Add(Text("准备建议",12,"#43816C")); p.Children.Add(Text(e.Suggestion,12)); }
      if(store.Data.Changes.Any(x=>x.EventId==e.Id && !x.Undone)) {
        p.Children.Add(Text("最近变更："+store.Data.Changes.Last(x=>x.EventId==e.Id && !x.Undone).Description,12,"#7B8C81"));
        p.Children.Add(Button("撤销最近一次自动更新",()=>{ Workflow.Undo(store,e.Id); store.Save(); Refresh(); }));
      }
      if(e.Kind=="变更") p.Children.Add(Button("关联原日程并处理",()=>LinkChange(e))); else p.Children.Add(Button("核对时间 / 修改安排",()=>Edit(e)));
      if(e.Status!="done") p.Children.Add(Button("标为完成",()=>{ e.Status="done"; store.Save(); Refresh(); }));
      if(e.Status!="cancelled") p.Children.Add(Button("移出日历（保留原文）",()=>{ e.Status="cancelled"; store.Save(); Refresh(); }));
      else p.Children.Add(Button("恢复到待核对",()=>{ e.Status="pending"; Workflow.SetReview(store,e,DateTime.Now); store.Save(); Refresh(); }));
      p.Children.Add(Button("云端分析 / 补充准备事项",()=>Reason(e)));
    }
    static string FormatTime(string value) { DateTime t; return DateTime.TryParse(value,out t) ? t.ToString("MM-dd HH:mm") : value; }
    void RefreshLists() {
      var inbox=Find<StackPanel>("InboxItems"); inbox.Children.Clear();
      if(store.Data.CloudQueue.Count>0) { inbox.Children.Add(Text("等待分析 "+store.Data.CloudQueue.Count+" 条消息 · 连续失败 "+store.Data.CloudQueue.Count(x=>x.Attempts>=3)+" 条",12,"#94743C")); inbox.Children.Add(Button("重试云端分析",()=>{ foreach(var job in store.Data.CloudQueue) job.Attempts=0; nextCloud=DateTime.MinValue; CloudTick(true); })); }
      foreach(var e in store.Data.Events.Where(x=>x.Status=="pending").OrderBy(x=>x.Date)) inbox.Children.Add(EventCard(e,true));
      if(inbox.Children.Count==0) inbox.Children.Add(Text("暂时没有待核对的安排。",14,"#829384"));
      var logs=Find<StackPanel>("LogItems"); logs.Children.Clear();
      var archived=store.Data.Events.Where(x=>(x.Status=="cancelled" || x.Status=="done") && !CapturePolicy.IsExcluded(x.Source,x.Window,"")).Reverse().Take(30).ToList();
      if(archived.Count>0) { logs.Children.Add(Text("已完成 / 已移出的安排 · 点击可查看原文与恢复",14)); foreach(var old in archived) logs.Children.Add(EventCard(old,true)); logs.Children.Add(Text("近期工作消息",15)); }
      foreach(var message in store.Data.Messages.Where(x=>x.State!="ignored").Reverse().Take(100)) {
        var p=new StackPanel(); p.Children.Add(Text(FormatTime(message.ObservedAt)+"  ·  "+message.Source+"  /  "+(message.Conversation.Length>0 ? message.Conversation : message.Window),12,"#52816C"));
        p.Children.Add(Text(message.Summary.Length>0 ? message.Summary : message.State=="queued" ? "等待云端分析" : "工作线索已记录",14));
        p.Children.Add(Text(message.Text.Length>500 ? message.Text.Substring(0,500)+"…" : message.Text,12,"#718A7D"));
        logs.Children.Add(new Border { Child=p,BorderBrush=Brush("#E4ECE5"),BorderThickness=new Thickness(0,0,0,1),Margin=new Thickness(0,0,0,16) });
      }
      if(store.Data.Logs.Count>0) logs.Children.Add(Text("窗口活跃片段",15));
      foreach(var e in store.Data.Logs.Where(x=>!CapturePolicy.IsExcluded(x.App,x.Title,"")).Reverse().Take(100)) {
        var p=new StackPanel(); p.Children.Add(Text(FormatTime(e.StartedAt)+" — "+FormatTime(e.EndedAt)+"    ·    "+e.App,12,"#52816C")); p.Children.Add(Text(e.Title,14)); p.Children.Add(Text("采样活跃时长约 "+Math.Max(1,(int)Math.Round(e.Seconds/60.0))+" 分钟",11,"#829384"));
        logs.Children.Add(new Border { Child=p,BorderBrush=Brush("#E4ECE5"),BorderThickness=new Thickness(0,0,0,1),Margin=new Thickness(0,0,0,15) });
      }
      if(logs.Children.Count==0) logs.Children.Add(Text("开始记录后，这里会呈现工作窗口的活跃片段。",14,"#829384"));
    }
    Window Dialog(string title,StackPanel content,int width=530) {
      return new Window { Owner=window,Title=title,Width=width,SizeToContent=SizeToContent.Height,MaxHeight=760,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Brush("#F6F8F4"),FontFamily=window.FontFamily,Foreground=Brush("#294849"),Content=new ScrollViewer { Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto },Resources=window.Resources };
    }
    void InputMessage() {
      var p=new StackPanel { Margin=new Thickness(24) }; p.Children.Add(Text("把收到的原话放进来",21));
      p.Children.Add(Text("按消息发送的日期理解“明天”和“下周”。自动采集不需要此步骤。",12,"#7C8E80"));
      var input=new TextBox { Height=125,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,15) }; p.Children.Add(input);
      p.Children.Add(Text("消息发送日期",12)); var date=new DatePicker { SelectedDate=DateTime.Today,Margin=new Thickness(0,0,0,15) }; p.Children.Add(date);
      var error=Text("例如：明天下午三点开会，讨论 SDK 联调进度",12,"#7C8E80"); p.Children.Add(error);
      Window dialog=null;
      p.Children.Add(Button("识别并记入日历",()=>{
        if(!date.SelectedDate.HasValue) { error.Text="请填写消息发送日期"; return; }
        var message=Workflow.Receive(store,"手动录入","用户确认消息日期","手动记录",input.Text,DateTime.Now,date.SelectedDate.Value.Add(DateTime.Now.TimeOfDay));
        if(message==null) { error.Text="消息已经记录，或内容是示例，请填写真实工作消息。"; return; }
        if(store.Data.Settings.CloudEnabled) QueueCloud(message); else { var result=Workflow.Local(store,message); if(result.EventIds.Count>0) selectedId=result.EventIds.Last(); }
        store.Save(); dialog.Close(); Page("Calendar"); Refresh(); Status("工作消息已记录"); if(store.Data.Settings.CloudEnabled) { nextCloud=DateTime.MinValue; CloudTick(true); }
      })); dialog=Dialog("录入工作消息",p); dialog.ShowDialog();
    }
    void Edit(WorkEvent item) {
      var p=new StackPanel { Margin=new Thickness(24) }; p.Children.Add(Text("核对工作安排",21));
      p.Children.Add(Text("事项名称",12)); var title=new TextBox { Text=item.Title,Margin=new Thickness(0,0,0,15),TextWrapping=TextWrapping.Wrap }; p.Children.Add(title);
      p.Children.Add(Text("日期",12)); DateTime parsed; var date=new DatePicker { SelectedDate=DateTime.TryParse(item.Date,out parsed) ? (DateTime?)parsed : null,Margin=new Thickness(0,0,0,15) }; p.Children.Add(date);
      p.Children.Add(Text("具体时间（24 小时制，例如 15:00；不确定可留空）",12)); var time=new TextBox { Text=item.Time,Margin=new Thickness(0,0,0,15) }; p.Children.Add(time);
      p.Children.Add(Text("提前多少分钟提醒",12)); var offset=new TextBox { Text=item.ReminderMinutes.ToString(),Margin=new Thickness(0,0,0,15) }; p.Children.Add(offset);
      var pending=new CheckBox { Content="仍需核对，暂不开启具体时间提醒",IsChecked=item.Time.Length==0,Margin=new Thickness(0,0,0,15) }; p.Children.Add(pending);
      var error=Text("原文："+item.Evidence,12,"#7C8E80"); p.Children.Add(error); Window d=null;
      p.Children.Add(Button("保存安排",()=>{
        int minutes; DateTime clock;
        if(string.IsNullOrWhiteSpace(title.Text) || !int.TryParse(offset.Text,out minutes) || minutes<0 || minutes>10080) { error.Text="请填写事项及 0—10080 分钟的提前提醒时间"; return; }
        if(time.Text.Trim().Length>0 && !DateTime.TryParseExact(time.Text.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out clock)) { error.Text="时间请填写 HH:mm，例如 15:00"; return; }
        if(pending.IsChecked!=true && (!date.SelectedDate.HasValue || time.Text.Trim().Length==0)) { error.Text="启用提醒需要完整日期和具体时间"; return; }
        item.Title=title.Text.Trim(); item.Date=date.SelectedDate.HasValue ? date.SelectedDate.Value.ToString("yyyy-MM-dd") : ""; item.Time=time.Text.Trim(); item.ReminderMinutes=minutes;
        item.Status=pending.IsChecked==true ? "pending" : "active"; item.Warning=pending.IsChecked==true ? "用户保留为待核对事项" : "";
        item.LastReminder=""; item.SnoozeUntil=""; item.UpdatedAt=DateTime.Now.ToString("o"); Workflow.SetReview(store,item,DateTime.Now); store.Save(); d.Close(); Refresh();
      })); d=Dialog("核对 / 修改日程",p); d.ShowDialog();
    }
    void LinkChange(WorkEvent change) {
      var candidates=store.Data.Events.Where(x=>x.Id!=change.Id && x.Kind!="变更" && x.Status!="done" && x.Status!="cancelled").ToList();
      var p=new StackPanel { Margin=new Thickness(24) }; p.Children.Add(Text("将变更关联到原日程",20)); p.Children.Add(Text(change.Evidence));
      var list=new ComboBox { Margin=new Thickness(0,10,0,18),ItemsSource=candidates,DisplayMemberPath="Title",MinHeight=36 }; p.Children.Add(list);
      var cancel=new CheckBox { Content="取消选中的原日程（保留原文，可恢复）",IsChecked=change.Evidence.Contains("取消"),Margin=new Thickness(0,0,0,15) }; p.Children.Add(cancel);
      var error=Text("改期会打开时间核对；取消会保留记录。",12,"#7C8E80"); p.Children.Add(error); Window d=null;
      p.Children.Add(Button("应用到选中的日程",()=>{
        var original=list.SelectedItem as WorkEvent; if(original==null) { error.Text="请选择原日程"; return; }
        if(cancel.IsChecked==true) { original.Status="cancelled"; original.Evidence+="\n变更原文："+change.Evidence; change.Status="done"; store.Save(); d.Close(); Refresh(); }
        else { d.Close(); var proposal=new WorkEvent { Title=original.Title,Date=change.Date,Time=change.Time,Evidence=original.Evidence+"\n变更原文："+change.Evidence,ReminderMinutes=original.ReminderMinutes }; Edit(proposal); if(proposal.UpdatedAt.Length>0) { original.Title=proposal.Title; original.Date=proposal.Date; original.Time=proposal.Time; original.Status=proposal.Status; original.Warning=proposal.Warning; original.Evidence=proposal.Evidence; original.ReminderMinutes=proposal.ReminderMinutes; original.LastReminder=""; original.SnoozeUntil=""; original.UpdatedAt=proposal.UpdatedAt; change.Status="done"; store.Save(); Refresh(); } }
      })); d=Dialog("处理改期或取消",p); d.ShowDialog();
    }
    void LoadSettings() {
      Find<CheckBox>("OcrEnabled").IsChecked=store.Data.Settings.OcrEnabled; Find<CheckBox>("ToastsEnabled").IsChecked=store.Data.Settings.ToastsEnabled;
      Find<TextBox>("ExcludedApps").Text=store.Data.Settings.ExcludedApps; Find<TextBox>("ModelName").Text=store.Data.Settings.Model;
      Find<TextBox>("ApiBase").Text=store.Data.Settings.ApiBase; Find<CheckBox>("CloudEnabled").IsChecked=store.Data.Settings.CloudEnabled;
      Find<TextBox>("MyNames").Text=store.Data.Settings.MyNames; Find<CheckBox>("ReviewReminders").IsChecked=store.Data.Settings.ReviewReminders;
      try { Find<PasswordBox>("ApiKey").Password=Cloud.Unprotect(store.Data.Settings.KeyCipher); } catch { Status("无法解密已有密钥，请重新填写 API Key"); }
    }
    void SaveSettings() {
      bool enabled=Find<CheckBox>("CloudEnabled").IsChecked==true;
      string address=Find<TextBox>("ApiBase").Text.Trim(),key=Find<PasswordBox>("ApiKey").Password.Trim(),model=Find<TextBox>("ModelName").Text.Trim();
      if(enabled) { try { Cloud.Endpoint(address); if(key.Length==0 || model.Length==0) throw new Exception("请填写模型名与 API Key"); } catch(Exception ex) { Status(ex.Message); return; } }
      generation++; cloudCancel.Cancel(); cloudCancel=new CancellationTokenSource();
      store.Data.Settings=new Settings { OcrEnabled=Find<CheckBox>("OcrEnabled").IsChecked==true,ToastsEnabled=Find<CheckBox>("ToastsEnabled").IsChecked==true,ExcludedApps=Find<TextBox>("ExcludedApps").Text,Model=model,ApiBase=address,KeyCipher=Cloud.Protect(key),CloudEnabled=enabled,MyNames=Find<TextBox>("MyNames").Text.Trim(),ReviewReminders=Find<CheckBox>("ReviewReminders").IsChecked==true };
      foreach(var item in store.Data.Events) Workflow.SetReview(store,item,DateTime.Now);
      foreach(var job in store.Data.CloudQueue) job.Attempts=0;
      if(capturing) Find<TextBlock>("CaptureDetail").Text=enabled ? "读取前台文字与可见通知 · 候选工作文字将发送至配置的云端 API" : "读取前台文字与可见通知 · 云端推理已停用";
      store.Save(); Status("设置已保存 · 下一轮采集使用新设置");
    }
    void Export() {
      var dialog=new Microsoft.Win32.SaveFileDialog { FileName="工作日历-"+DateTime.Today.ToString("yyyyMMdd")+".ics",Filter="日历文件 (*.ics)|*.ics" };
      if(dialog.ShowDialog(window)==true) { Store.ExportIcs(store.Data.Events,dialog.FileName); Status("已导出已确认的具体时间日程："+dialog.FileName); }
    }
    void Reminders() {
      foreach(var e in store.Data.Events.Where(x=>Store.IsDue(x,DateTime.Now) && !openReminders.Contains(x.Id)).Take(1).ToList()) {
        ShowReminder(e); Store.MarkReminded(e); store.Save();
      }
    }
    void ShowReminder(WorkEvent item) {
      var p=new StackPanel { Margin=new Thickness(22) }; p.Children.Add(Text(item.Status=="pending" ? "知时提醒 · 核对安排" : "知时提醒",13,"#377E66")); p.Children.Add(Text(item.Title,19)); p.Children.Add(Text(item.When,14));
      if(item.Status=="pending") p.Children.Add(Text("请核对具体时间。这是待办核对提醒，不代表会议此刻开始。",12,"#94743C"));
      foreach(var step in item.Preparations.Where(x=>!x.Done).Take(3)) p.Children.Add(Text("待准备："+step.Text,12));
      var buttons=new StackPanel { Orientation=Orientation.Horizontal }; var toast=new Window { Title="知时 · 日程提醒",Width=430,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.ToolWindow,ResizeMode=ResizeMode.NoResize,Topmost=true,ShowActivated=false,ShowInTaskbar=true,Background=Brush("#F5F8F0"),FontFamily=window.FontFamily,Content=p,Resources=window.Resources };
      buttons.Children.Add(Button("稍后 10 分钟",()=>{ item.SnoozeUntil=DateTime.Now.AddMinutes(10).ToString("o"); store.Save(); toast.Close(); }));
      buttons.Children.Add(Button("查看安排",()=>{ selectedId=item.Id; Show(); Page("Calendar"); Refresh(); toast.Close(); })); p.Children.Add(buttons);
      toast.Loaded+=(s,e)=>{ var area=SystemParameters.WorkArea; toast.Left=area.Right-toast.ActualWidth-20; toast.Top=area.Bottom-toast.ActualHeight-20; displayedReminders++; if(qaPipeline) RenderWindow(toast,"reminder-preview.png"); };
      openReminders.Add(item.Id); toast.Closed+=(s,e)=>openReminders.Remove(item.Id); toast.Show(); System.Media.SystemSounds.Asterisk.Play();
    }
    void QueueCloud(WorkMessage message) {
      if(!store.Data.Settings.CloudEnabled || store.Data.CloudQueue.Any(x=>x.MessageId==message.Id)) return;
      if(store.Data.CloudQueue.Count>=100) { message.State="deferred"; return; }
      message.State="queued";
      store.Data.CloudQueue.Add(new CloudJob { Text=message.Text,Source=message.Source,Window=message.Window,Conversation=message.Conversation,ObservedAt=message.ReceivedAt,TimestampKnown=message.TimestampKnown,MessageId=message.Id,Key=message.Fingerprint });
    }
    async void CloudTick(bool onDemand=false) {
      if((!capturing && !onDemand) || cloudBusy || quitting || !store.Data.Settings.CloudEnabled || DateTime.Now<nextCloud) return;
      var deferred=store.Data.Messages.FirstOrDefault(x=>x.State=="deferred"); if(deferred!=null) QueueCloud(deferred);
      var job=store.Data.CloudQueue.FirstOrDefault(x=>x.Attempts<3); if(job==null) return;
      var message=store.Data.Messages.FirstOrDefault(x=>x.Id==job.MessageId) ?? new WorkMessage { Id=job.MessageId,Text=job.Text,Source=job.Source,Window=job.Window,Conversation=job.Conversation,ObservedAt=job.ObservedAt,ReceivedAt=job.ObservedAt,TimestampKnown=job.TimestampKnown };
      cloudBusy=true; int epoch=generation; var token=cloudCancel.Token; job.Attempts++; nextCloud=DateTime.Now.AddSeconds(30);
      try {
        var answer=await Cloud.Analyze(store.Data.Settings,job.Text,DateTime.Parse(job.ObservedAt),token,context:Cloud.ContextFor(store,message),timestampKnown:message.TimestampKnown);
        if(token.IsCancellationRequested || epoch!=generation || quitting) { job.Attempts--; return; }
        var result=Workflow.Model(store,message,answer); if(result.EventIds.Count>0) selectedId=result.EventIds.Last(); store.Data.CloudQueue.Remove(job); store.Save(); Refresh();
        Status("云端分析完成 · "+result.Description);
      } catch(OperationCanceledException) { if(token.IsCancellationRequested) job.Attempts=Math.Max(0,job.Attempts-1); Status("云端分析已取消或超时；候选安排保留在本机"); nextCloud=DateTime.Now.AddMinutes(1); }
      catch(Exception ex) { Status("云端分析失败："+ex.Message+"。本地记录保留，最多重试 3 次。"); store.Save(); nextCloud=DateTime.Now.AddMinutes(1); }
      finally { cloudBusy=false; }
    }
    async void Reason(WorkEvent item) {
      if(!store.Data.Settings.CloudEnabled) { Status("请先在采集设置填写 API 地址、模型和密钥，并启用云端推理"); Page("Settings"); return; }
      if(cloudBusy) { Status("已有云端分析正在进行，请稍后重试"); return; }
      cloudBusy=true; var token=cloudCancel.Token; Status("正在进行云端分析…");
      try {
        DateTime observed; if(!DateTime.TryParse(item.ObservedAt,out observed)) observed=DateTime.Now;
        var message=store.Data.Messages.FirstOrDefault(x=>x.Id==item.MessageId) ?? new WorkMessage { Source=item.Source,Window=item.Window,Conversation=item.Conversation,Text=item.Evidence,ObservedAt=observed.ToString("o"),ReceivedAt=observed.ToString("o") };
        var answer=await Cloud.Analyze(store.Data.Settings,message.Text,observed,token,context:Cloud.ContextFor(store,message),timestampKnown:message.TimestampKnown);
        if(token.IsCancellationRequested || quitting) return;
        int count=0;
        foreach(var answerItem in answer.events.Where(x=>x!=null && !string.IsNullOrWhiteSpace(x.evidence) && Parser.Normalize(message.Text).Contains(Parser.Normalize(x.evidence)))) { Workflow.AddPreparations(item,answerItem.suggestions); count++; }
        store.Save(); Refresh(); Status("云端分析完成 · 已补充准备建议，已核对的时间保持不变");
      } catch(OperationCanceledException) { Status("云端分析已取消或超时"); }
      catch(Exception ex) { Status("云端分析失败："+ex.Message+"。原有日历保持可用。"); }
      finally { cloudBusy=false; }
    }
    void AddSettingsControls() {
      var panel=(StackPanel)Find<TextBox>("ApiBase").Parent;
      var nameLabel=Text("我的姓名 / 群昵称（多个用逗号分隔，用于识别 @我）",12);
      var names=new TextBox { Name="MyNames",Margin=new Thickness(0,0,0,15) }; window.RegisterName("MyNames",names);
      panel.Children.Insert(1,nameLabel); panel.Children.Insert(2,names);
      var review=new CheckBox { Name="ReviewReminders",Content="时间不完整的事项，也在当天提醒我核对",Margin=new Thickness(0,0,0,14) }; window.RegisterName("ReviewReminders",review);
      int index=panel.Children.IndexOf(Find<Button>("SaveSettings")); panel.Children.Insert(index,review);
      var test=Button("测试云端连接（只发送测试句子）",TestCloudConnection); panel.Children.Insert(index+1,test);
      Find<Button>("NavLogs").Content="工作记忆";
      var logsHeader=(StackPanel)((Grid)Find<Border>("LogsPage").Child).Children[0]; ((TextBlock)logsHeader.Children[0]).Text="工作记忆与进展";
      ((TextBlock)logsHeader.Children[1]).Text="保留工作原文、来源和分析结果，关联后续变化。下方同时列出窗口活跃片段。";
      var help=Text("随时按 Ctrl + Alt + Space 识别当前工作窗口；自动记录开启后无需按键。",12,"#718A7D"); panel.Children.Insert(3,help);
    }
    async void TestCloudConnection() {
      Status("正在用虚构消息测试 API 连接…");
      try {
        var settings=new Settings { ApiBase=Find<TextBox>("ApiBase").Text.Trim(),Model=Find<TextBox>("ModelName").Text.Trim(),KeyCipher=Cloud.Protect(Find<PasswordBox>("ApiKey").Password.Trim()),MyNames=Find<TextBox>("MyNames").Text.Trim() };
        var result=await Cloud.Analyze(settings,"明天下午三点开会，讨论项目进度",DateTime.Now,cloudCancel.Token,timestampKnown:true);
        Status("云端连接成功，返回 "+result.events.Length+" 个分析结果。请保存设置后开始记录。");
      } catch(Exception ex) { Status("连接未通过："+ex.Message); }
    }
    void SchedulePreparation(WorkEvent parent) {
      var remaining=parent.Preparations.Where(x=>!x.Done).Select(x=>x.Text).ToArray(); if(remaining.Length==0) { Status("准备事项已全部完成"); return; }
      var child=new WorkEvent { Title="准备："+parent.Title,Kind="待办",Source=parent.Source,Window=parent.Window,Conversation=parent.Conversation,Evidence="由以下事项生成的准备建议："+parent.Evidence,Suggestion=string.Join("；",remaining),ObservedAt=DateTime.Now.ToString("o"),Warning="请选择准备时间" };
      Edit(child); if(child.UpdatedAt.Length>0) { store.Data.Events.Add(child); selectedId=child.Id; store.Save(); Refresh(); }
    }
    void StartPipelineCheck() {
      if(qaFixture!=null) return;
      window.Title="知时 · 自动验收（独立测试数据）"; qaStarted=DateTime.Now;
      qaFixture=Process.Start(new ProcessStartInfo(Path.Combine(basePath,"qa","TestMessage.exe"),"--scenario") { UseShellExecute=true });
      captureTimer.Interval=TimeSpan.FromSeconds(2);
      capturing=true; generation++; Find<Button>("ToggleCapture").Content="暂停记录"; Find<TextBlock>("CaptureState").Text="正在验收：仅采集测试窗口";
      var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
      timer.Tick+=(s,e)=>{
        if((DateTime.Now-qaStarted).TotalSeconds<21) return; timer.Stop(); capturing=false; generation++; captureTimer.Stop();
        var meeting=store.Data.Events.FirstOrDefault(x=>x.Kind=="会议"); var report=store.Data.Events.FirstOrDefault(x=>x.Kind=="待办" && x.Title.Contains("报告"));
        var audit=new { captured=store.Data.Messages.Count,only_test_source=store.Data.Messages.All(x=>x.Source=="TestMessage"),same_meeting=store.Data.Events.Count(x=>x.Kind=="会议")==1,updated=store.Data.Changes.Count>=2,cancelled=meeting!=null && meeting.Status=="cancelled",reminded=report!=null && report.LastReminder.Length>0,reminder_window_displayed=displayedReminders>0,fixture_exited=qaFixture.HasExited,data_directory=qaDirectory };
        File.WriteAllText(Path.Combine(basePath,"qa","pipeline-result.json"),new JavaScriptSerializer().Serialize(audit),Encoding.UTF8);
        selectedId=report!=null ? report.Id : ""; selectedDay=DateTime.Today; month=new DateTime(selectedDay.Year,selectedDay.Month,1); Show(); Refresh(); Find<TextBlock>("CaptureState").Text="验收结束 · 采集已暂停"; Find<Button>("ToggleCapture").Content="开始记录"; RenderPreview();
        try { if(qaFixture!=null && !qaFixture.HasExited) qaFixture.Kill(); } catch {}
        var exit=new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) }; exit.Tick+=(a,b)=>{ exit.Stop(); Quit(); }; exit.Start();
      }; timer.Start();
    }
    void RenderPreview() {
      RenderWindow(window,"calendar-preview.png");
    }
    void RenderWindow(Window target,string filename) {
      // Render this app's own WPF visual for layout QA; does not capture other windows.
      target.UpdateLayout();
      var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)target.ActualWidth,(int)target.ActualHeight,96,96,PixelFormats.Pbgra32);
      bitmap.Render(target); var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
      using(var file=File.Create(Path.Combine(basePath,"qa",filename))) encoder.Save(file);
    }
  }
}
