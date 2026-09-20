using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WorkCalendar;
class Tests {
  static int count;
  static void Check(bool ok,string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
  static WorkEvent P(string s,bool known=true) { return Parser.Parse(s,new DateTime(2026,9,20,10,0,0),known); }
  static int Main() {
    try {
      var e = P("明天下午三点半开会"); Check(e.Date=="2026-09-21" && e.Time=="15:30" && e.Status=="active","Chinese relative meeting time");
      e=P("明天下午拉会"); Check(e.Date=="2026-09-21" && e.Time=="" && e.Status=="pending","Afternoon does not become invented precise time");
      e=P("明天3点开会"); Check(e.Time=="" && e.Status=="pending","Ambiguous three oclock requires confirmation");
      e=P("明天15:20提交报告"); Check(e.Time=="15:20" && e.Kind=="待办","24 hour deadline");
      e=P("下周一上午十点评审"); Check(e.Date=="2026-09-21" && e.Time=="10:00","Week boundary Sunday to Monday");
      e=Parser.Parse("明天上午九点开会",new DateTime(2026,12,31,10,0,0),true); Check(e.Date=="2027-01-01","Year rollover");
      e=P("2026年9月31日下午3点开会"); Check(e.Date=="" && e.Status=="pending","Invalid date remains unresolved");
      e=P("明天25:90开会"); Check(e.Time=="" && e.Status=="pending","Invalid clock rejected");
      e=P("明天下午3点开会",false); Check(e.Status=="pending" && e.Warning.Contains("历史消息"),"Old screen text cannot become trusted message date");
      e=P("会议改到后天下午四点"); Check(e.Kind=="变更" && e.Status=="pending" && e.Time=="16:00","Reschedule is a proposal linked by user");
      e=P("明天的会议取消了"); Check(e.Kind=="变更" && e.Status=="pending","Cancellation is never a new active meeting");
      e=P("可能明天下午三点开会"); Check(e.Status=="pending","Tentative schedule requires confirmation");
      Check(P("你好，今天风景很好")==null,"Ordinary conversation ignored");
      Check(P("昨天已经完成开会")==null,"Past completed work is not a future task");
      Check(CapturePolicy.IsExcluded("ChatGPT","ChatGPT",""),"Actual Codex desktop process excluded before capture");
      Check(CapturePolicy.IsExcluded("electron","工作助手",@"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\electron.exe"),"Codex package identity excluded even if process renamed");
      Check(CapturePolicy.IsExcluded("msedge","ChatGPT - 工作日历",""),"Development chat in browser excluded");
      Check(!CapturePolicy.IsExcluded("Weixin","客户项目群",""),"Real work chat remains eligible");
      Check(P("例如明天下午三点开会")==null,"Examples do not become calendar events");
      Check(P("using(var bmp=new Bitmap(1000,250)) 明天下午三点开会")==null,"Source code does not become calendar events");
      Check(Cloud.CandidateLines("例如明天下午三点开会").Length==0,"Examples are not sent to cloud");
      Check(Parser.Split("明天15点开会，后天下午交付版本").Count()==2,"Multiple dated clauses split");
      string directory=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString("N"));
      var store=new Store(directory); var now=new DateTime(2026,9,20,10,0,0);
      Check(store.Ingest("明天下午三点开会","fixture","chat",now,false).Count==1,"Capture ingestion persists event");
      store=new Store(directory); Check(store.Ingest("明天下午三点开会","fixture","chat",now.AddDays(1),false).Count==0,"Repeated historic screen text deduplicates across restart and midnight");
      e=P("明天下午三点开会"); Check(Store.IsDue(e,new DateTime(2026,9,21,14,45,0)),"Reminder fires 15 minutes early");
      Store.MarkReminded(e); Check(!Store.IsDue(e,new DateTime(2026,9,21,14,46,0)),"Reminder persists suppression");
      e.SnoozeUntil=new DateTime(2026,9,21,14,56,0).ToString("o"); Check(Store.IsDue(e,new DateTime(2026,9,21,14,57,0)),"Snoozed reminder fires again");
      e.Status="pending"; Check(!Store.IsDue(e,new DateTime(2026,9,21,14,57,0)),"Unconfirmed event never fires exact-time reminder");
      Store.Reschedule(e,new DateTime(2026,9,22,16,0,0)); Check(e.LastReminder=="" && e.SnoozeUntil=="" && e.Status=="active","Reschedule resets reminder revision");
      Store.ExportIcs(new[]{e,P("明天下午拉会")},System.IO.Path.Combine(directory,"test.ics"));
      Check(File.ReadAllText(System.IO.Path.Combine(directory,"test.ics")).Split(new[]{"BEGIN:VEVENT"},StringSplitOptions.None).Length==2,"Calendar export only includes confirmed precise events");
      var cipher=Cloud.Protect("test-key-not-a-real-secret"); Check(cipher!="test-key-not-a-real-secret" && Cloud.Unprotect(cipher)=="test-key-not-a-real-secret","API key DPAPI roundtrip without plaintext storage");
      Check(Cloud.Endpoint("https://api.example.com/v1/").AbsoluteUri=="https://api.example.com/v1/chat/completions","Compatible endpoint normalized");
      bool rejected=false; try { Cloud.Endpoint("http://api.example.com/v1"); } catch(ArgumentException) { rejected=true; } Check(rejected,"Plain HTTP cloud endpoint rejected");
      var model=new ModelAnalysis { events=new[]{new ModelEvent { title="联调会议",evidence="明天下午拉会",date="2026-09-21",time="15:00",kind="会议",suggestions=new[]{"建议准备联调问题"} },new ModelEvent { title="伪造任务",evidence="不存在的原文",date="2026-09-21",time="15:00" }} };
      var safe=Cloud.Validate(model,"明天下午拉会",now); Check(safe.Count==1 && safe[0].Time=="" && safe[0].Status=="pending","Model cannot invent exact time or fabricate source evidence");
      var settings=new Settings { Model="fixture-model",ApiBase="https://api.example.com/v1",KeyCipher=cipher,CloudEnabled=true };
      var fake=new FakeApi(); var result=Cloud.Analyze(settings,"明天下午拉会",now,CancellationToken.None,fake).GetAwaiter().GetResult();
      Check(fake.ValidRequest && result.events.Length==1,"Chat Completions request and JSON response contract");
      Console.WriteLine("ALL " + count + " CHECKS PASSED"); return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
  }
  sealed class FakeApi:HttpMessageHandler {
    public bool ValidRequest;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
      string body=await request.Content.ReadAsStringAsync();
      ValidRequest=request.RequestUri.AbsoluteUri=="https://api.example.com/v1/chat/completions" && request.Headers.Authorization.Scheme=="Bearer" && body.Contains("json_object") && body.Contains("明天下午拉会");
      var json=new JavaScriptSerializer();
      string content=json.Serialize(new { events=new[]{new { title="联调会议",evidence="明天下午拉会",date="",time="",kind="会议",suggestions=new[]{"建议核对具体时间"} }} });
      return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json.Serialize(new { choices=new[]{new { finish_reason="stop",message=new { content=content }} } }),Encoding.UTF8,"application/json") };
    }
  }
}
