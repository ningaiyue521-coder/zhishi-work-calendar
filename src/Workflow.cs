using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WorkCalendar {
  public sealed class WorkflowResult {
    public int Created, Updated, Ignored;
    public List<string> EventIds=new List<string>();
    public string Description { get { return "新增 "+Created+" 项，更新 "+Updated+" 项"+(Ignored>0 ? "，忽略 "+Ignored+" 项无关内容" : ""); } }
  }
  public static class Workflow {
    public static string Channel(string source,string window,string conversation) { return source+"|"+(string.IsNullOrWhiteSpace(conversation) ? window : conversation); }
    public static string Channel(WorkEvent e) { return Channel(e.Source,e.Window,e.Conversation); }
    public static string Channel(WorkMessage m) { return Channel(m.Source,m.Window,m.Conversation); }
    public static WorkMessage Receive(Store store,string source,string window,string conversation,string text,DateTime observed,DateTime? sent) {
      if(CapturePolicy.IsExcluded(source,window,"") || string.IsNullOrWhiteSpace(text)) return null;
      text=Parser.Normalize(text); if(text.Length<4 || text.Length>6500 || CapturePolicy.IsIllustrationOrCode(text)) return null;
      string key=Store.Hash(Channel(source,window,conversation)+"|"+text+(sent.HasValue ? "|"+sent.Value.ToString("yyyy-MM-dd HH:mm") : ""));
      if(store.Data.Messages.Any(x=>x.Fingerprint==key)) return null;
      var m=new WorkMessage { Source=source,Window=window,Conversation=conversation ?? "",Text=text,ObservedAt=observed.ToString("o"),ReceivedAt=(sent ?? observed).ToString("o"),TimestampKnown=sent.HasValue,Fingerprint=key };
      store.Data.Messages.Add(m); return m;
    }
    public static bool IsRelatedToMe(string text,string names) {
      if(Regex.IsMatch(text,"与我无关|不用你参加|无需你参加|不需要你参加|不是给你的|仅供参考")) return false;
      var at=Regex.Matches(text,@"[@＠]([^\s，,。:：]{1,20})");
      if(at.Count==0 || string.IsNullOrWhiteSpace(names)) return true;
      var mine=Regex.Split(names,"[,，;；\\s]+").Where(x=>x.Length>0).ToList();
      return at.Cast<Match>().Any(m=>m.Groups[1].Value=="所有人" || m.Groups[1].Value=="全体成员" || mine.Contains(m.Groups[1].Value));
    }
    public static WorkflowResult Local(Store store,WorkMessage message) {
      var result=new WorkflowResult();
      if(!IsRelatedToMe(message.Text,store.Data.Settings.MyNames)) { message.State="ignored"; result.Ignored++; return result; }
      foreach(var sentence in Parser.Split(message.Text)) {
        if(CapturePolicy.IsIllustrationOrCode(sentence)) { result.Ignored++; continue; }
        string action=Action(sentence);
        var candidate=Parser.Parse(sentence,DateTime.Parse(message.ReceivedAt),message.TimestampKnown);
        if(candidate==null && action=="create") continue;
        if(action!="create") {
          var targets=Targets(store,message,sentence);
          if(targets.Count==1 && message.TimestampKnown) {
            ApplyChange(store,targets[0],message,sentence,action,result); continue;
          }
          if(candidate==null) candidate=new WorkEvent { Title=sentence,Evidence=sentence,Kind="变更" };
          candidate.Kind="变更"; candidate.Status="pending"; candidate.Warning=!message.TimestampKnown ? "消息发送时间不明，请核对是否为最新变更" : targets.Count>1 ? "同一会话有多个可能的安排，请选择要修改的事项" : "未找到同一会话的原安排，请关联原事项";
        }
        if(candidate==null) continue;
        Add(store,candidate,message,result);
      }
      message.State=result.Created+result.Updated>0 ? "processed" : "recorded";
      message.Summary=result.Created+result.Updated>0 ? result.Description : "已留存工作线索";
      return result;
    }
    static string Action(string text) {
      if(Regex.IsMatch(text,"(?:取消|不用开|不开了|不用交|不用发)")) return "cancel";
      if(Regex.IsMatch(text,"改到|改成|改为|推迟|延期|提前到|定在|确定为|时间是")) return "update";
      if(Regex.IsMatch(text,"已(?:经)?(?:提交|交付|发送|发出|发给|完成)|报告发了|SDK发了|会议结束")) return "complete";
      return "create";
    }
    static List<WorkEvent> Targets(Store store,WorkMessage message,string sentence) {
      if(string.IsNullOrWhiteSpace(message.Conversation) && Regex.IsMatch(message.Window ?? "","^(微信|Weixin|WeChat|QQ|企业微信|飞书|钉钉)$",RegexOptions.IgnoreCase)) return new List<WorkEvent>();
      var list=store.Data.Events.Where(x=>Channel(x)==Channel(message) && (x.Status=="active" || x.Status=="pending") && x.Kind!="变更").ToList();
      if(Regex.IsMatch(sentence,"会|评审|讨论")) list=list.Where(x=>x.Kind=="会议").ToList();
      else if(Regex.IsMatch(sentence,"报告|SDK|版本|交付|提交")) {
        var words=Regex.Matches(sentence,"报告|SDK|版本|固件|交付|提交").Cast<Match>().Select(x=>x.Value).ToArray();
        list=list.Where(x=>words.Any(w=>(x.Title+" "+x.Evidence).Contains(w))).ToList();
      }
      DateTime received=DateTime.Parse(message.ReceivedAt);
      return list.Where(x=>{ DateTime seen; var original=store.Data.Messages.FirstOrDefault(m=>m.Id==x.MessageId); string baseline=original==null ? x.ObservedAt : original.ReceivedAt; return DateTime.TryParse(baseline,out seen) && seen<=received.AddMinutes(1) && (received-seen).TotalDays<=7; }).ToList();
    }
    public static WorkflowResult Model(Store store,WorkMessage message,ModelAnalysis analysis) {
      var result=new WorkflowResult();
      if(!IsRelatedToMe(message.Text,store.Data.Settings.MyNames)) { message.State="ignored"; result.Ignored++; return result; }
      foreach(var m in (analysis.events ?? new ModelEvent[0]).Take(8)) {
        if(m==null || string.IsNullOrWhiteSpace(m.evidence) || string.IsNullOrWhiteSpace(m.title)) continue;
        string evidence=Parser.Normalize(m.evidence);
        if(!Parser.Normalize(message.Text).Contains(evidence) || evidence.Length<4 || CapturePolicy.IsIllustrationOrCode(evidence)) { result.Ignored++; continue; }
        if(m.relevance=="other" || m.action=="ignore" || (m.confidence>0 && m.confidence<0.65)) { result.Ignored++; continue; }
        string action=Action(evidence);
        if(action=="create" && m.confidence>=0.85 && (m.action=="update" || m.action=="cancel" || m.action=="complete")) action=m.action;
        if(action!="create") {
          var targets=Targets(store,message,evidence);
          var referenced=targets.FirstOrDefault(x=>x.Id==m.related_event_id);
          if(referenced!=null && m.confidence>=0.85) targets=new List<WorkEvent>{referenced};
          if(targets.Count==1 && message.TimestampKnown && m.relevance=="self" && m.confidence>=0.85) { ApplyChange(store,targets[0],message,evidence,action,result); AddPreparations(targets[0],m.suggestions); continue; }
        }
        var parsed=Parser.Parse(evidence,DateTime.Parse(message.ReceivedAt),message.TimestampKnown);
        var e=parsed ?? new WorkEvent { Evidence=evidence,Warning="模型识别出的工作待办，具体日期尚未明确" };
        e.Title=m.title.Length>100 ? m.title.Substring(0,100) : m.title;
        e.Kind=action!="create" ? "变更" : m.kind=="会议" ? "会议" : "待办";
        if(e.Kind=="变更") { e.Status="pending"; e.Warning="需选择要修改的原事项"; }
        // A model must not turn an approximate time into an invented exact time.
        if(parsed==null) { e.Date=""; e.Time=""; e.Status="pending"; }
        if(m.relevance=="unknown" || m.relevance==null || m.confidence<0.85) { e.Status="pending"; e.Warning=Append(e.Warning,"需核对是否与你有关"); }
        e.Analysis="云端语义分析"; AddPreparations(e,m.suggestions); Add(store,e,message,result);
      }
      foreach(var note in (analysis.memories ?? new ModelMemory[0]).Take(4)) {
        if(note!=null && !string.IsNullOrWhiteSpace(note.evidence) && !string.IsNullOrWhiteSpace(note.summary) && Parser.Normalize(message.Text).Contains(Parser.Normalize(note.evidence))) {
          message.Summary=note.summary.Length>600 ? note.summary.Substring(0,600) : note.summary;
        }
      }
      message.State=result.Created+result.Updated>0 ? "processed" : string.IsNullOrEmpty(message.Summary) ? "ignored" : "recorded";
      if(message.State=="ignored") result.Ignored++;
      if(string.IsNullOrEmpty(message.Summary) && message.State=="processed") message.Summary=result.Description;
      return result;
    }
    static void Add(Store store,WorkEvent e,WorkMessage message,WorkflowResult result) {
      var existing=store.Data.Events.FirstOrDefault(x=>Channel(x)==Channel(message) && Parser.Normalize(x.Evidence)==Parser.Normalize(e.Evidence) && (!message.TimestampKnown || SameMessageDate(store,x,message)));
      if(existing!=null) { AddPreparations(existing,e.Preparations.Select(x=>x.Text)); return; }
      e.Source=message.Source; e.Window=message.Window; e.Conversation=message.Conversation; e.MessageId=message.Id; e.ObservedAt=message.ObservedAt;
      e.Fingerprint=Store.Hash(Channel(message)+"|"+Parser.Normalize(e.Evidence)); e.Analysis=e.Analysis.Length>0 ? e.Analysis : "本机规则识别";
      SetReview(store,e,DateTime.Parse(message.ObservedAt));
      if(e.Preparations.Count==0 && e.Suggestion.Length>0) AddPreparations(e,new[]{e.Suggestion});
      store.Data.Events.Add(e); store.Data.Seen[e.Fingerprint]=message.ObservedAt; result.Created++; result.EventIds.Add(e.Id);
    }
    static bool SameMessageDate(Store store,WorkEvent e,WorkMessage message) {
      var original=store.Data.Messages.FirstOrDefault(x=>x.Id==e.MessageId); DateTime before,after;
      return original!=null && DateTime.TryParse(original.ReceivedAt,out before) && DateTime.TryParse(message.ReceivedAt,out after) && before.Date==after.Date;
    }
    static void ApplyChange(Store store,WorkEvent target,WorkMessage message,string evidence,string action,WorkflowResult result) {
      DateTime observed=DateTime.Parse(message.ReceivedAt);
      var revision=new EventChange { EventId=target.Id,MessageId=message.Id,At=message.ObservedAt,BeforeJson=new JavaScriptSerializer().Serialize(target),Description=evidence };
      if(action=="cancel") target.Status="cancelled";
      else if(action=="complete") target.Status="done";
      else {
        var parsed=Parser.Parse(evidence,observed,message.TimestampKnown);
        if(parsed==null) parsed=new WorkEvent { Evidence=evidence };
        string date=parsed.Date.Length>0 ? parsed.Date : target.Date;
        string period=parsed.Period.Length>0 ? parsed.Period : target.Time.Length>0 ? int.Parse(target.Time.Substring(0,2))>=12 ? "下午" : "上午" : target.Period;
        if(parsed.Time.Length==0 && date.Length>0 && Regex.IsMatch(evidence,"[零〇一二两三四五六七八九十0-9]+[点时]|[0-9]+[:：][0-9]+")) {
          var combined=Parser.Parse(date+" "+period+" "+evidence,observed,true); if(combined!=null) parsed.Time=combined.Time;
        }
        if(date.Length==0 && parsed.Time.Length==0) { target.Warning=Append(target.Warning,"收到改期消息，仍待确定新时间"); target.Status="pending"; }
        else {
          target.Date=date; target.Time=parsed.Time; target.Period=period;
          bool trusted=message.TimestampKnown && (target.Status=="active" || (parsed.Date.Length>0 && parsed.Time.Length>0));
          target.Status=trusted && target.Start.HasValue ? "active" : "pending";
          target.Warning=target.Status=="active" ? "" : "已结合本会话更新，请核对消息日期及具体时间";
        }
      }
      target.Evidence+="\n后续消息："+evidence; target.MessageId=message.Id; target.UpdatedAt=message.ObservedAt; target.LastReminder=""; target.SnoozeUntil="";
      SetReview(store,target,DateTime.Parse(message.ObservedAt)); store.Data.Changes.Add(revision); result.Updated++; result.EventIds.Add(target.Id);
    }
    public static void SetReview(Store store,WorkEvent e,DateTime now) {
      e.ReviewAt="";
      if(e.Status!="pending" || e.Kind=="变更" || !store.Data.Settings.ReviewReminders) return;
      DateTime date; if(!DateTime.TryParseExact(e.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)) return;
      if(date.Date<now.Date) return;
      var review=date.Date.AddHours(9); if(review<now) review=now.AddMinutes(10);
      e.ReviewAt=review.ToString("o");
    }
    public static void AddPreparations(WorkEvent e,IEnumerable<string> items) {
      foreach(var text in (items ?? new string[0]).Where(x=>!string.IsNullOrWhiteSpace(x)).Take(5)) {
        string clean=text.Trim(); if(clean.Length>350) clean=clean.Substring(0,350);
        if(!e.Preparations.Any(x=>x.Text==clean)) e.Preparations.Add(new Preparation { Text=clean });
      }
    }
    public static bool Undo(Store store,string eventId) {
      var change=store.Data.Changes.LastOrDefault(x=>x.EventId==eventId && !x.Undone); if(change==null) return false;
      var before=new JavaScriptSerializer().Deserialize<WorkEvent>(change.BeforeJson); int index=store.Data.Events.FindIndex(x=>x.Id==eventId); if(before==null || index<0) return false;
      store.Data.Events[index]=before; change.Undone=true; return true;
    }
    static string Append(string a,string b) { return a.Length==0 ? b : a+"；"+b; }
  }
}
