# Zhishi - Windows Work Calendar

A native Windows tool for recording work context and managing reminders. It collects candidate text from the active application window and visible notifications, uses conversation context to organize tasks, events, and preparation steps, and keeps the original text for review.

> Version 0.2. Foreground text capture has been verified with Feishu. WeChat conversation capture is not yet working, and QQ, DingTalk, and other applications have not been validated. General screen capture does not provide complete access to every application's background messages.

## Features

- Reads accessible text from the active window, with Windows' local Chinese OCR as a fallback
- Recognizes Chinese dates and times; ambiguous arrangements go to a review list
- Associates follow-up messages in the same conversation with rescheduling, cancellation, or completion, with undo support for automatic updates
- Supports a configurable cloud model compatible with Chat Completions JSON mode to assess relevance, summarize work progress, and suggest preparation steps
- Records message sources, original text, and processing results; preparation steps can be checked off or scheduled separately
- Provides a local monthly calendar, review list, system-tray operation, reminder pop-ups, and ICS export
- Excludes Codex / ChatGPT, the application itself, and login or password windows by default

## Build and run

Requires Windows 10/11 x64, .NET Framework 4.8/WPF, and the Windows OCR components. No additional NuGet, Node.js, or Python dependencies are required.

Run Windows PowerShell from the repository root:

```powershell
.\build.ps1
```

The script uses Windows' .NET Framework compiler and builds and tests:

- `WorkCalendar.exe`: main application
- `CaptureWorker.exe`: capture process; keep it in the same directory as the main application
- `qa/`: local test programs and outputs, excluded from Git

Double-click `WorkCalendar.exe`. In the current Chinese interface, open Capture Settings (`采集设置`), enter your name or group-chat nickname, and configure the provider's API URL, model name, and API key. Click Test Cloud Connection (`测试云端连接`), enable cloud analysis and save after a successful test, then click Start Recording (`开始记录`).

The API setting accepts a base URL such as `https://api.example.com/v1` or a complete `/chat/completions` endpoint. The example domain is not a working service; configure your own provider. This repository contains no real account configuration or credentials. Local time parsing, the calendar, manual entry, and reminders remain available with cloud analysis disabled.

Automatic recording samples every 8 seconds. Press `Ctrl + Alt + Space` in a work window to capture it once. Closing the main window leaves the application in the system tray. Choose Quit (`彻底退出`) from the tray menu or settings to stop the application.

## Example behavior

All examples below are fictional. The local parser currently targets Chinese; the English column explains each sample rather than claiming support for English input.

| Sample input | English meaning | Result |
| --- | --- | --- |
| `明天下午三点开会` | Meeting tomorrow at 3 p.m. | With a trusted message date, creates an event for tomorrow at 15:00 and reminds 15 minutes beforehand by default |
| `明天下午拉会` | Arrange a meeting tomorrow afternoon | Records tomorrow with the exact time unresolved and schedules a review reminder |
| `会议改成下午四点` | Move the meeting to 4 p.m. | Updates the original meeting when the message time is trusted and the target in the same conversation is clear |
| `会议取消了` | The meeting is cancelled | Associates the message with the original event, removes it from the calendar, and keeps the evidence and undo history |
| `报告已经发给客户` | The report has been sent to the client | Marks a clearly matched task as complete; the progress can also be recorded in work memory |

Screen text without a message date may be historical. It does not become a trusted exact-time reminder automatically, and the application does not turn an unspecified afternoon into 15:00. Rescheduling or cancellation with an unclear target goes to the review list.

## Data and privacy

The application creates `data/state.json` at runtime. It contains local settings, work text, events, queued analysis, and history. Saving keeps a `state.json.bak` backup. API keys are encrypted with Windows DPAPI and bound to the current Windows account; other local data, including messages and events, is not encrypted as a whole.

Screenshots are processed in memory for local OCR and are neither saved nor uploaded. **When cloud analysis is enabled, candidate work text, sources, limited context from the same conversation, related events, and the configured user names are sent to the API selected by the user.** Recording can be paused, cloud analysis can be disabled, and more applications can be excluded.

Do not commit `data/`, `qa/`, executables, screenshots, logs, or local configuration. This repository uses a source-file allowlist and includes a publication check:

```powershell
.\scripts\Test-PublishSafety.ps1
```

The script checks Git-staged paths and common sensitive-data patterns. It does not read runtime data or make network requests, and it does not replace manual review. Use fictional inputs and redacted information when reporting an issue; do not attach real conversations, data backups, API keys, or screenshots of configured credentials.

## Current limitations

- Capture reads accessible foreground content and attempts to read visible notifications. Minimized or unopened conversations, notifications that disappear, and custom-rendered interfaces may not be readable.
- Verified Feishu foreground capture does not imply access to all Feishu messages, group-chat history, or background notifications.
- The tested WeChat version does not expose conversation text through its window accessibility interface; dedicated screen-reading support still needs work.
- The cloud protocol has been tested against a simulated service. Actual results depend on the configured provider and model.
- Reminders cannot appear while the computer is asleep, shut down, or the application has fully exited. When running again, the application can catch up on reminders from the previous day.
- Startup at login, third-party calendar synchronization, and multi-device synchronization are not configured.

## Validation

`build.ps1` runs 65 core and workflow checks covering Chinese time boundaries, deduplication, persistence, rescheduling and cancellation, undo, conversation isolation, source exclusion, reminders, ICS, DPAPI, and the simulated API protocol.

Optional desktop workflow validation:

```powershell
.\WorkCalendar.exe --qa-pipeline
```

This mode opens a fictional message window for about 20 seconds and uses an isolated data directory to verify the sequence: meeting notice, reschedule, cancellation, report task, and reminder pop-up. Results are written to `qa/pipeline-result.json`; production `data/` is not modified. Synthetic tests do not replace validation against real applications.

## Source structure

```text
src/
  App.cs              Desktop UI, tray, reminders, and capture scheduling
  MainWindow.xaml     Main WPF interface
  CaptureWorker.cs    Accessible window text and local OCR
  CapturePolicy.cs    Source exclusion and candidate-text filtering
  Core.cs             Data, time parsing, persistence, and ICS
  Workflow.cs         Conversation context, changes, undo, and work memory
  Cloud.cs            Cloud protocol, context, and credential encryption
tests/                Fictional inputs, simulated API, and desktop fixtures
scripts/              Publication checks
```
