$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Security.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Runtime.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/r:' + (Join-Path $wpf $_) }
$refs += @('Windows.Foundation.winmd','Windows.Globalization.winmd','Windows.Graphics.winmd','Windows.Media.winmd','Windows.Storage.winmd') | ForEach-Object { '/r:' + (Join-Path $metadata $_) }
$compiler = Join-Path $framework 'csc.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'qa') | Out-Null
& $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ @refs ('/out:' + (Join-Path $root 'CaptureWorker.exe')) (Join-Path $root 'src\CaptureWorker.cs') (Join-Path $root 'src\CapturePolicy.cs')
if ($LASTEXITCODE -ne 0) { throw 'Capture worker build failed.' }
& $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ @refs ('/win32manifest:' + (Join-Path $root 'src\app.manifest')) ('/resource:' + (Join-Path $root 'src\MainWindow.xaml') + ',MainWindow.xaml') ('/out:' + (Join-Path $root 'WorkCalendar.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Cloud.cs') (Join-Path $root 'src\App.cs') (Join-Path $root 'src\CapturePolicy.cs') (Join-Path $root 'src\Workflow.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& $compiler /nologo /utf8output /target:exe /platform:x64 @refs ('/out:' + (Join-Path $root 'qa\CoreTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Cloud.cs') (Join-Path $root 'tests\CoreTests.cs') (Join-Path $root 'src\CapturePolicy.cs') (Join-Path $root 'src\Workflow.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tests build failed.' }
& (Join-Path $root 'qa\CoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& $compiler /nologo /utf8output /target:exe /platform:x64 @refs ('/out:' + (Join-Path $root 'qa\WorkflowTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Cloud.cs') (Join-Path $root 'src\Workflow.cs') (Join-Path $root 'src\CapturePolicy.cs') (Join-Path $root 'tests\WorkflowTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Workflow test build failed.' }
& (Join-Path $root 'qa\WorkflowTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Workflow tests failed.' }
& $compiler /nologo /utf8output /target:winexe /platform:x64 @refs ('/out:' + (Join-Path $root 'qa\TestMessage.exe')) (Join-Path $root 'tests\TestMessage.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
& $compiler /nologo /utf8output /target:winexe /platform:x64 @refs ('/out:' + (Join-Path $root 'qa\PipelineLauncher.exe')) (Join-Path $root 'tests\PipelineLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Pipeline launcher build failed.' }
Write-Output 'Build and core tests passed.'
