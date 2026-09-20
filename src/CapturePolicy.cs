using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WorkCalendar {
  public static class CloudTextGate {
    public static bool IsCandidate(string text) { return !string.IsNullOrWhiteSpace(text) && !CapturePolicy.IsIllustrationOrCode(text) && Regex.IsMatch(text,"明天|后天|周[一二三四五六日天]|任务|客户|反馈|报告|安排|负责|需要|麻烦|请|开会|会议|截止|联调|版本|SDK|修复|排查|提交|交付|改到|改成|改为|取消|完成|发了|发给|定在|确定为|时间是"); }
  }
  public static class CapturePolicy {
    public static bool IsExcludedNotification(string text) {
      return Regex.IsMatch(text ?? "", @"(?i:\b(?:Codex|ChatGPT|WorkCalendar)\b)|知时|工作安排已更新");
    }
    public static bool IsExcluded(string process, string title, string path) {
      string name = (process ?? "").Trim();
      if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0,name.Length-4);
      if (name.Length == 0) return true;
      if (Regex.IsMatch(name, "^(WorkCalendar|CaptureWorker|Codex|ChatGPT|LockApp|CredentialUIBroker|LogonUI)$", RegexOptions.IgnoreCase)) return true;
      if ((path ?? "").IndexOf("OpenAI.Codex",StringComparison.OrdinalIgnoreCase)>=0) return true;
      // The desktop client is called ChatGPT.exe; a browser can also host the development chat.
      if (Regex.IsMatch(title ?? "", @"\b(Codex|ChatGPT)\b", RegexOptions.IgnoreCase)) return true;
      return Regex.IsMatch(title ?? "", "密码|验证码|登录|解锁|Password|Sign in", RegexOptions.IgnoreCase);
    }
    public static bool IsIllustrationOrCode(string text) {
      return Regex.IsMatch(text ?? "", @"例如|举例|示例|比如说|假设.{0,12}(?:收到|消息|明天)|测试用例|(?i:Regex\.(?:IsMatch|Match)|using\s*\(|(?:public|private|protected)\s+(?:static\s+)?(?:void|string|bool|class)|Console\.Write|\.Children\.Add)");
    }
  }
}
