using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WorkCalendar {
  public sealed class WorkEvent {
    public string Id = Guid.NewGuid().ToString("N");
    public string Title = "", Evidence = "", Source = "", Window = "", ObservedAt = "";
    public string Date = "", Time = "", Period = "", Status = "pending", Kind = "待办", Warning = "", Suggestion = "";
    public string Fingerprint = "", LastReminder = "", SnoozeUntil = "", UpdatedAt = "";
    public int ReminderMinutes = 15;
    public string Conversation = "", MessageId = "", ReviewAt = "", Analysis = "";
    public List<Preparation> Preparations = new List<Preparation>();
    [ScriptIgnore] public DateTime? Start {
      get { DateTime parsed; return System.DateTime.TryParseExact(Date + " " + Time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ? (DateTime?)parsed : null; }
    }
    [ScriptIgnore] public string When { get { return (Date.Length == 0 ? "日期待定" : Date) + "  " + (Time.Length > 0 ? Time : (Period.Length > 0 ? Period + " · 时间待定" : "时间待定")); } }
    [ScriptIgnore] public string StateLabel { get { return Status == "active" ? "已安排" : Status == "done" ? "已完成" : Status == "cancelled" ? "已移出" : "待核对"; } }
  }
  public sealed class Preparation { public string Text = ""; public bool Done; }
  public sealed class WorkMessage {
    public string Id = Guid.NewGuid().ToString("N"), Source = "", Window = "", Conversation = "", Text = "", ReceivedAt = "", ObservedAt = "", Fingerprint = "", State = "received", Summary = "";
    public bool TimestampKnown;
  }
  public sealed class EventChange {
    public string Id = Guid.NewGuid().ToString("N"), EventId = "", MessageId = "", At = "", Description = "", BeforeJson = "";
    public bool Undone;
  }
  public sealed class WorkLog {
    public string Id = Guid.NewGuid().ToString("N");
    public string App = "", Title = "", StartedAt = "", EndedAt = "", Summary = "";
    public int Seconds;
  }
  public sealed class Settings {
    public bool OcrEnabled = true, ToastsEnabled = true;
    public string ExcludedApps = "KeePass,1Password,Bitwarden,LockApp,CredentialUIBroker";
    public string Model = "", ApiBase = "", KeyCipher = "";
    public bool CloudEnabled;
    public string MyNames = "";
    public bool ReviewReminders = true;
  }
  public sealed class CloudJob {
    public string Text = "", Source = "", Window = "", ObservedAt = "", Key = "";
    public int Attempts;
    public string MessageId = "", Conversation = "";
    public bool TimestampKnown;
  }
  public sealed class State {
    public int Version = 1;
    public List<WorkEvent> Events = new List<WorkEvent>();
    public List<WorkLog> Logs = new List<WorkLog>();
    public Dictionary<string,string> Seen = new Dictionary<string,string>();
    public Settings Settings = new Settings();
    public List<CloudJob> CloudQueue = new List<CloudJob>();
    public Dictionary<string,string> CloudSeen = new Dictionary<string,string>();
    public List<WorkMessage> Messages = new List<WorkMessage>();
    public List<EventChange> Changes = new List<EventChange>();
  }
  public sealed class Store {
    public readonly string Path;
    public State Data;
    static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
    public Store(string directory) {
      Directory.CreateDirectory(directory);
      Path = System.IO.Path.Combine(directory,"state.json");
      Data = File.Exists(Path) ? Json().Deserialize<State>(File.ReadAllText(Path, Encoding.UTF8)) : new State();
      if (Data == null || Data.Events == null || Data.Logs == null || Data.Seen == null || Data.Settings == null) throw new InvalidDataException("本机数据无法读取。请保留 data 目录及 state.json.bak 后再恢复。");
    }
    public void Save() {
      string temporary = Path + ".tmp";
      File.WriteAllText(temporary, Json().Serialize(Data), new UTF8Encoding(false));
      if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak"); else File.Move(temporary, Path);
    }
    public List<WorkEvent> Ingest(string text, string source, string window, DateTime messageTime, bool timestampKnown) {
      List<WorkEvent> added = new List<WorkEvent>();
      foreach (string sentence in Parser.Split(text)) {
        string key = Hash(source + "|" + window + "|" + Parser.Normalize(sentence));
        // Screen observations have no stable message id. Keep their first-seen date across restarts.
        if (!timestampKnown && Data.Seen.ContainsKey(key)) continue;
        WorkEvent e = Parser.Parse(sentence, messageTime, timestampKnown);
        if (e == null) continue;
        if (timestampKnown) key = Hash(key + "|" + messageTime.ToString("yyyy-MM-dd"));
        if (Data.Seen.ContainsKey(key)) continue;
        Data.Seen[key] = messageTime.ToString("o");
        e.Source = source; e.Window = window; e.ObservedAt = messageTime.ToString("o"); e.Fingerprint = key;
        Data.Events.Add(e); added.Add(e);
      }
      if (added.Count > 0) Save();
      return added;
    }
    public static string Hash(string text) {
      using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
    public void RecordActivity(string app, string title, DateTime now, int seconds) {
      WorkLog last = Data.Logs.LastOrDefault(); DateTime end;
      if (last != null && last.App == app && last.Title == title && DateTime.TryParse(last.EndedAt, out end) && (now - end).TotalSeconds < 35 && end.Date == now.Date) {
        last.EndedAt = now.ToString("o"); last.Seconds += seconds;
      } else {
        Data.Logs.Add(new WorkLog { App = app, Title = title, StartedAt = now.ToString("o"), EndedAt = now.ToString("o"), Seconds = seconds, Summary = "使用 " + app + "：" + title });
      }
    }
    public static bool IsDue(WorkEvent e, DateTime now) {
      if (e.Status != "active" && e.Status != "pending") return false;
      DateTime review;
      bool needsReview=e.Status=="pending" && DateTime.TryParse(e.ReviewAt,out review);
      if(e.Status=="pending" && !needsReview) return false;
      if(e.Status=="active" && !e.Start.HasValue) return false;
      DateTime start=needsReview ? DateTime.Parse(e.ReviewAt) : e.Start.Value;
      string revision=needsReview ? "review|"+e.ReviewAt : e.Date+" "+e.Time;
      DateTime snooze;
      if (DateTime.TryParse(e.SnoozeUntil, out snooze)) return now >= snooze && now <= start.AddDays(1);
      return e.LastReminder != revision && now >= start.AddMinutes(needsReview ? 0 : -e.ReminderMinutes) && now <= start.AddDays(1);
    }
    public static void MarkReminded(WorkEvent e) { e.LastReminder=e.Status=="pending" ? "review|"+e.ReviewAt : e.Date+" "+e.Time; e.SnoozeUntil=""; }
    public static void Reschedule(WorkEvent e, DateTime start) {
      e.Date = start.ToString("yyyy-MM-dd"); e.Time = start.ToString("HH:mm"); e.Status = "active"; e.LastReminder = ""; e.SnoozeUntil = ""; e.UpdatedAt = DateTime.Now.ToString("o");
    }
    public static void ExportIcs(IEnumerable<WorkEvent> events, string path) {
      var b = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//WorkCalendar//Local//ZH\r\nCALSCALE:GREGORIAN\r\n");
      foreach (WorkEvent e in events.Where(x => x.Status == "active" && x.Start.HasValue)) {
        var lines = new [] {"BEGIN:VEVENT", "UID:" + e.Id + "@workcalendar.local", "DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"), "DTSTART:" + e.Start.Value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"), "SUMMARY:" + Escape(e.Title), "DESCRIPTION:" + Escape(e.Evidence + "\n来源：" + e.Source), "BEGIN:VALARM", "TRIGGER:-PT" + e.ReminderMinutes + "M", "ACTION:DISPLAY", "DESCRIPTION:" + Escape(e.Title), "END:VALARM", "END:VEVENT"};
        foreach (string line in lines) b.Append(Fold(line)).Append("\r\n");
      }
      b.Append("END:VCALENDAR\r\n"); File.WriteAllText(path,b.ToString(),new UTF8Encoding(false));
    }
    static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,"); }
    static string Fold(string s) {
      var b = new StringBuilder(); int bytes = 0;
      foreach (char c in s) { int n = Encoding.UTF8.GetByteCount(c.ToString()); if (bytes + n > 72) { b.Append("\r\n "); bytes = 1; } b.Append(c); bytes += n; }
      return b.ToString();
    }
  }
  public static class Parser {
    const string Actions = "拉会|开会|会议|评审|同步|讨论|联调|验收|提交|交付|截止|汇报|反馈|回复|提醒|安排|准备|整理|排查|修复|测试|验证|发版|跟进|处理|看看|看下|发.{0,8}(?:报告|资料|版本|SDK)|给.{0,12}(?:报告|资料|版本|SDK)|(?:SDK|报告|资料|版本).{0,10}(?:给|发)";
    const string DateWords = "今天|明天|后天|大后天|[本下这]?周[一二三四五六日天]|星期[一二三四五六日天]|\\d{1,2}月\\d{1,2}[日号]|20\\d{2}[-/]\\d{1,2}[-/]\\d{1,2}";
    public static string Normalize(string s) { return Regex.Replace(s.Normalize(NormalizationForm.FormKC), "[ \\t]+", " ").Trim(); }
    public static IEnumerable<string> Split(string text) {
      string normalized = Normalize(text ?? "");
      normalized = Regex.Replace(normalized, "[，,；;](?=\\s*(?:" + DateWords + "))", "\n");
      return Regex.Split(normalized, "[\\r\\n。！？!?]+").Select(x => x.Trim()).Where(x => x.Length >= 4 && x.Length <= 1000);
    }
    public static WorkEvent Parse(string raw, DateTime reference, bool timestampKnown) {
      string s = Normalize(raw);
      if(CapturePolicy.IsIllustrationOrCode(s)) return null;
      bool change = Regex.IsMatch(s,"取消|改到|改为|改成|推迟|延期|提前到");
      if (!Regex.IsMatch(s, Actions) && !change) return null;
      bool hasDate = Regex.IsMatch(s, DateWords);
      if (!hasDate && !change && !Regex.IsMatch(s,"记得|需要|务必|待办|请|麻烦|负责|截至|截止")) return null;
      if (Regex.IsMatch(s,"已完成|已经完成|开过会|昨天|前天") && !hasDate && !change) return null;
      var e = new WorkEvent { Evidence = raw, Title = s.Length > 68 ? s.Substring(0,68) + "…" : s, Kind = change ? "变更" : Regex.IsMatch(s,"拉会|开会|会议|评审") ? "会议" : "待办" };
      var warnings = new List<string>();
      DateTime? day = null;
      Match full = Regex.Match(s,@"(?<!\d)(20\d{2})[年/\-](\d{1,2})[月/\-](\d{1,2})[日号]?");
      Match md = Regex.Match(s,@"(?<!\d)(\d{1,2})月(\d{1,2})[日号]");
      try {
        if (full.Success) day = new DateTime(int.Parse(full.Groups[1].Value),int.Parse(full.Groups[2].Value),int.Parse(full.Groups[3].Value));
        else if (md.Success) { day = new DateTime(reference.Year,int.Parse(md.Groups[1].Value),int.Parse(md.Groups[2].Value)); if (day.Value < reference.Date.AddDays(-30)) { day = day.Value.AddYears(1); warnings.Add("原文缺少年份，暂按下次该日期展示"); } }
        else if (s.Contains("大后天")) day = reference.Date.AddDays(3);
        else if (s.Contains("后天")) day = reference.Date.AddDays(2);
        else if (s.Contains("明天")) day = reference.Date.AddDays(1);
        else if (s.Contains("今天")) day = reference.Date;
        else {
          Match week = Regex.Match(s,@"(下下|下|本|这)?(?:周|星期)([一二三四五六日天])");
          if (week.Success) {
            int target = "一二三四五六日天".IndexOf(week.Groups[2].Value); if (target == 7) target = 6;
            int current = ((int)reference.DayOfWeek + 6) % 7;
            int delta = target - current;
            string prefix = week.Groups[1].Value;
            if (prefix == "下") delta += 7; else if (prefix == "下下") delta += 14; else if (prefix == "" && delta < 0) delta += 7;
            day = reference.Date.AddDays(delta);
          }
        }
      } catch (ArgumentOutOfRangeException) { warnings.Add("原文日期无效，请核对"); }
      if (day.HasValue) e.Date = day.Value.ToString("yyyy-MM-dd"); else warnings.Add("原文没有明确日期");
      Match period = Regex.Match(s,"凌晨|早上|上午|中午|下午|傍晚|晚上|晚间");
      e.Period = period.Success ? period.Value : "";
      Match hour = Regex.Match(s,@"(?<![\d年月/\-])(?:(凌晨|早上|上午|中午|下午|傍晚|晚上|晚间)\s*)?([零〇一二两三四五六七八九十\d]{1,3})(?:[:：](\d{1,2})(?!\d)|[点时](半|一刻|三刻|[零〇一二两三四五六七八九十\d]{1,3}分?)?)");
      if (hour.Success) {
        int h = Number(hour.Groups[2].Value), m = 0;
        string suffix = hour.Groups[4].Value;
        if (hour.Groups[3].Success) m = Number(hour.Groups[3].Value);
        else if (suffix == "半") m = 30; else if (suffix == "一刻") m = 15; else if (suffix == "三刻") m = 45; else if (suffix.Length > 0) m = Number(suffix.Replace("分", ""));
        string p = hour.Groups[1].Success ? hour.Groups[1].Value : e.Period;
        bool ambiguous = false;
        if (Regex.IsMatch(p,"下午|傍晚|晚上|晚间") && h > 0 && h < 12) h += 12;
        if (p == "中午" && h >= 1 && h <= 4) h += 12;
        if ((p == "凌晨" || p == "上午" || p == "早上") && h == 12) h = 0;
        if (Regex.IsMatch(p,"晚上|晚间") && h == 12) { ambiguous = true; warnings.Add("晚上十二点的日期边界需要确认"); }
        if (p.Length == 0 && h >= 1 && h <= 11 && !(hour.Groups[3].Success && hour.Groups[2].Value.StartsWith("0"))) { ambiguous = true; warnings.Add("未说明上午或下午"); }
        if (h < 0 || h > 23 || m < 0 || m > 59) warnings.Add("原文时间无效");
        else if (!ambiguous) e.Time = h.ToString("00") + ":" + m.ToString("00");
      } else warnings.Add("原文没有具体几点");
      if (!timestampKnown) warnings.Add("仅知首次看到的时间，未取得消息发送日期；需核对是否为历史消息");
      if (Regex.IsMatch(s,"可能|预计|如果|假如|要不要|是否|大概|左右|再定|待定")) warnings.Add("原文安排尚未确定");
      if (Regex.IsMatch(s,@"每[天周月年]|每天|每周")) warnings.Add("重复安排需单独确认，目前仅记录本次候选");
      if (change) warnings.Add("这是改期或取消消息，请关联原日程后应用");
      if (e.Start.HasValue && e.Start.Value < reference) warnings.Add("识别的时间已过去");
      if (e.Kind == "会议") e.Suggestion = "建议会前核对议题、会议链接及需准备的资料";
      if (Regex.IsMatch(s,"SDK|版本|固件") && Regex.IsMatch(s,"交付|发|提交")) e.Suggestion = "建议交付前核对版本、验证结果及接收人";
      e.Warning = string.Join("；",warnings.Distinct());
      e.Status = warnings.Count == 0 && e.Start.HasValue ? "active" : "pending";
      return e;
    }
    static int Number(string s) {
      int n; if (int.TryParse(s,out n)) return n;
      s = s.Replace("两","二").Replace("〇","零");
      if (s == "十") return 10;
      if (s.Contains("十")) { string[] p = s.Split('十'); int tens = p[0].Length == 0 ? 1 : "零一二三四五六七八九".IndexOf(p[0]); int ones = p[1].Length == 0 ? 0 : "零一二三四五六七八九".IndexOf(p[1]); return tens < 0 || ones < 0 ? -1 : tens * 10 + ones; }
      return s.Length == 1 ? "零一二三四五六七八九".IndexOf(s) : -1;
    }
  }
}
