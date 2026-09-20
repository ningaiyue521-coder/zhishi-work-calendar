using System;
using System.IO;
using System.Linq;
using WorkCalendar;
class WorkflowTests {
  static int count; static DateTime now=new DateTime(2026,9,20,14,0,0);
  static void Check(bool value,string name) { if(!value) throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
  static Store NewStore() { return new Store(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"workflow-tests",Guid.NewGuid().ToString("N"))); }
  static WorkMessage Msg(Store s,string text,string room="项目 A",bool known=true) { return Workflow.Receive(s,"TestChat",room,room,text,now,known ? (DateTime?)now : null); }
  static ModelAnalysis Model(string text,string action="create",string relevance="self") { return new ModelAnalysis { events=new[]{new ModelEvent { title=text,evidence=text,action=action,relevance=relevance,confidence=0.95,kind=text.Contains("会议") ? "会议" : "待办",suggestions=new[]{"建议核对议题和资料"} }} }; }
  static int Main() {
    try {
      var s=NewStore(); var m=Msg(s,"明天下午三点开会，讨论接口联调"); var result=Workflow.Local(s,m); var meeting=s.Data.Events.Single(); string id=meeting.Id;
      Check(result.Created==1 && meeting.Status=="active" && meeting.Time=="15:00","Precise trusted message enters calendar automatically");
      Check(meeting.Preparations.Count>0,"Meeting has actionable preparation checklist");
      now=now.AddMinutes(1); Workflow.Local(s,Msg(s,"改成四点"));
      Check(s.Data.Events.Count==1 && meeting.Id==id && meeting.Time=="16:00","Follow-up inherits date and period without duplicate meeting");
      Check(s.Data.Changes.Count==1,"Automatic change keeps prior state");
      now=now.AddMinutes(1); Workflow.Local(s,Msg(s,"会议取消了")); Check(meeting.Status=="cancelled" && s.Data.Events.Count==1,"Cancellation links existing meeting");
      Check(Workflow.Undo(s,id) && s.Data.Events.Single().Status=="active" && s.Data.Events.Single().Time=="16:00","Undo restores cancelled meeting");
      Check(Workflow.Undo(s,id) && s.Data.Events.Single().Time=="15:00","Undo previous reschedule restores original time");
      s.Save(); var reload=new Store(Path.GetDirectoryName(s.Path)); Check(reload.Data.Messages.Count==3 && reload.Data.Changes.Count==2,"Context and revisions persist after restart");
      Check(Workflow.Receive(reload,"TestChat","项目 A","项目 A",m.Text,DateTime.Parse(m.ObservedAt),DateTime.Parse(m.ReceivedAt))==null,"Same source message deduplicates after restart");
      var other=Msg(s,"会议改到后天下午四点","项目 B"); Workflow.Local(s,other); Check(s.Data.Events.First(x=>x.Id==id).Time=="15:00" && s.Data.Events.Any(x=>x.Kind=="变更"),"Other conversation cannot modify original meeting");
      var ambiguous=NewStore(); Workflow.Local(ambiguous,Msg(ambiguous,"明天下午三点开会")); Workflow.Local(ambiguous,Msg(ambiguous,"后天上午十点评审会议")); Workflow.Local(ambiguous,Msg(ambiguous,"改成四点")); Check(ambiguous.Data.Events.Count(x=>x.Kind=="变更")==1 && ambiguous.Data.Changes.Count==0,"Ambiguous follow-up remains a proposal");
      var generic=NewStore(); var a=Workflow.Receive(generic,"Weixin","微信","","明天下午三点开会",now,now); Workflow.Local(generic,a); var b=Workflow.Receive(generic,"Weixin","微信","","会议取消了",now.AddMinutes(1),now.AddMinutes(1)); Workflow.Local(generic,b); Check(generic.Data.Events.First().Status=="active","Generic app title is not treated as reliable conversation identity");
      var fuzzy=NewStore(); Workflow.Local(fuzzy,Msg(fuzzy,"明天下午拉会")); var f=fuzzy.Data.Events.Single(); Check(f.Date=="2026-09-21" && f.Time=="" && f.ReviewAt.Length>0,"Afternoon meeting is recorded with a review reminder");
      Check(Store.IsDue(f,new DateTime(2026,9,21,9,0,0)),"Incomplete time triggers clearly separate morning review"); Store.MarkReminded(f); Check(!Store.IsDue(f,new DateTime(2026,9,21,9,1,0)),"Review reminder does not repeat");
      var unknown=NewStore(); Workflow.Local(unknown,Msg(unknown,"明天下午三点开会","项目 A",false)); Check(unknown.Data.Events.Single().Status=="pending","Old screen text cannot silently become a trusted precise reminder");
      var historical=NewStore(); Workflow.Local(historical,Msg(historical,"明天下午三点开会")); Workflow.Local(historical,Msg(historical,"会议取消了","项目 A",false)); Check(historical.Data.Events.First().Status=="active" && historical.Data.Events.Last().Kind=="变更","Unknown-time cancellation cannot remove a live event");
      var repeating=NewStore(); Workflow.Local(repeating,Msg(repeating,"明天下午三点开会")); now=now.AddDays(1); Workflow.Local(repeating,Msg(repeating,"明天下午三点开会")); Check(repeating.Data.Events.Count==2 && repeating.Data.Events[0].Date!=repeating.Data.Events[1].Date,"Repeated wording on a new day creates the new arrangement"); now=now.AddDays(-1);
      var at=NewStore(); at.Data.Settings.MyNames="李工,小李"; Workflow.Local(at,Msg(at,"@张工 明天下午三点开会")); Check(at.Data.Events.Count==0,"Task addressed to someone else ignored when own names configured"); Workflow.Local(at,Msg(at,"@李工 明天下午三点开会")); Check(at.Data.Events.Count==1,"Task addressed to user retained");
      var ai=NewStore(); var input=Msg(ai,"明天下午三点开会"); Workflow.Model(ai,input,Model(input.Text)); Check(ai.Data.Events.Count==1 && ai.Data.Events[0].Status=="active","Validated semantic result reaches live calendar");
      var ignored=NewStore(); var ig=Msg(ignored,"明天下午三点开会"); Workflow.Model(ignored,ig,Model(ig.Text,"create","other")); Check(ignored.Data.Events.Count==0 && ig.State=="ignored","Cloud unrelated-person classification creates no calendar noise");
      var invented=NewStore(); var inv=Msg(invented,"明天下午拉会"); Workflow.Model(invented,inv,Model("明天15:00开会")); Check(invented.Data.Events.Count==0,"Invented evidence is rejected before persistence");
      var modelTime=NewStore(); var mt=Msg(modelTime,"明天下午拉会"); var mtAnswer=Model(mt.Text); mtAnswer.events[0].time="15:00"; Workflow.Model(modelTime,mt,mtAnswer); Check(modelTime.Data.Events.Single().Time=="","Model exact time cannot replace fuzzy source wording");
      var semantic=NewStore(); Workflow.Local(semantic,Msg(semantic,"明天下午三点开会")); var sm=Msg(semantic,"这次碰头就算了"); Workflow.Model(semantic,sm,Model(sm.Text,"cancel")); Check(semantic.Data.Events.Single().Status=="cancelled","Model recognizes contextual cancellation without fixed keywords");
      var memories=NewStore(); var note=Msg(memories,"客户已确认新版本通过验证"); Workflow.Model(memories,note,new ModelAnalysis { events=new ModelEvent[0],memories=new[]{new ModelMemory { evidence=note.Text,summary="客户确认新版本验证通过" }} }); Check(memories.Data.Events.Count==0 && note.Summary=="客户确认新版本验证通过" && note.State=="recorded","Work progress is remembered without inventing a future task");
      Check(Cloud.ContextFor(ai,input).Contains(ai.Data.Events[0].Id),"Model receives existing event identifiers for linking");
      var complete=NewStore(); Workflow.Local(complete,Msg(complete,"明天下午五点提交报告")); Workflow.Local(complete,Msg(complete,"报告已经发给客户")); Check(complete.Data.Events.Single().Status=="done","Completion message closes matching deliverable");
      Check(Workflow.Receive(complete,"ChatGPT","ChatGPT","","明天下午三点开会",now,now)==null,"Development assistant excluded at ingestion boundary");
      Check(CapturePolicy.IsExcludedNotification("ChatGPT\n明天下午三点开会") && CapturePolicy.IsExcludedNotification("知时\n工作安排已更新"),"Assistant and own notification banners cannot re-enter the pipeline");
      Console.WriteLine("ALL "+count+" WORKFLOW CHECKS PASSED"); return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
  }
}
