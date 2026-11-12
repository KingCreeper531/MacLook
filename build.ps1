# Builds MacLook.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
$ErrorActionPreference = 'Stop'
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /out:"$root\MacLook.exe" `
    /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" `
    /r:"$fw\System.Xaml.dll" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Data.dll `
    /r:System.Core.dll /r:Microsoft.CSharp.dll `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $root\MacLook.exe"
