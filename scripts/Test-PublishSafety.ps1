$ErrorActionPreference = 'Stop'
$publishRoot = Split-Path -Parent $PSScriptRoot
$allowedFiles = @(
    '.gitignore',
    '.gitattributes',
    'README.md',
    'build.ps1',
    'src/App.cs',
    'src/CapturePolicy.cs',
    'src/CaptureWorker.cs',
    'src/Cloud.cs',
    'src/Core.cs',
    'src/MainWindow.xaml',
    'src/Workflow.cs',
    'src/app.manifest',
    'tests/CoreTests.cs',
    'tests/WorkflowTests.cs',
    'tests/TestMessage.cs',
    'tests/PipelineLauncher.cs',
    'tests/DemoLauncher.cs',
    'scripts/Test-PublishSafety.ps1'
)
$trackedFiles = @(git -C $publishRoot -c core.quotepath=false ls-files --cached)
if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) { throw 'No staged source files found. Stage the intended source files first.' }
$problems = New-Object 'System.Collections.Generic.List[string]'
$patterns = @{
    'API key or token' = '(?<![A-Za-z0-9])(?:sk-[A-Za-z0-9_-]{18,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'
    'DPAPI encrypted credential' = 'AQAAANCMnd8BFdERjHoAwE[A-Za-z0-9+/]{80,}={0,2}'
    'Private key' = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'User home path' = '(?i)[A-Z]:\\Users\\(?!Public\\|Default\\)[^\\\s]+\\'
    'Personal mailbox' = '(?i)[A-Z0-9._%+-]+@(?:gmail|qq|163|126|outlook|hotmail)\.com'
    'Embedded bearer credential' = '(?i)Bearer\s+[A-Za-z0-9_-]{24,}'
    'Nonempty saved key' = '(?i)"KeyCipher"\s*:\s*"[^"]+"'
}
foreach ($path in $trackedFiles) {
    if ($allowedFiles -notcontains $path) { $problems.Add('Unexpected tracked path: ' + $path); continue }
    $stagedContent = @(git -C $publishRoot show (':' + $path)) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw ('Cannot read staged file: ' + $path) }
    foreach ($label in $patterns.Keys) {
        if ([regex]::IsMatch($stagedContent, $patterns[$label])) { $problems.Add($label + ' in ' + $path) }
    }
}
$forbiddenExamples = @('data/state.json','data/state.json.bak','qa/calendar-preview.png','qa/notes.json','WorkCalendar.exe','CaptureWorker.exe','.env','local-settings.json','api-key.txt')
foreach ($path in $forbiddenExamples) {
    git -C $publishRoot check-ignore --quiet -- $path
    if ($LASTEXITCODE -ne 0) { $problems.Add('Sensitive/runtime path is not ignored: ' + $path) }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output $_ }; throw 'Publication check failed. Values are intentionally not printed.' }
Write-Output ('PASS: ' + $trackedFiles.Count + ' staged source files; runtime data is ignored; no configured secret patterns found.')
