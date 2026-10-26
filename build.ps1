param([string]$OutputName = 'WhaleCompanion.exe')
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName) { throw 'OutputName must be a filename in the project directory' }
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkRoot = Split-Path $compiler
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot ('WPF\' + $_)) }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ('/out:' + (Join-Path $projectRoot $OutputName)) ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')) ('/win32icon:' + (Join-Path $projectRoot 'whale.ico')) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output ('Built ' + $OutputName)
