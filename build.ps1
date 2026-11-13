# Builds MacLook.exe and MacLookSetup.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK needed.
$ErrorActionPreference = 'Stop'
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf  = "$fw\WPF"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = "$root\build"
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Data.dll",
    "/r:System.Core.dll", "/r:Microsoft.CSharp.dll",
    # WinRT (Now Playing / media controls). The .winmd files ship with Windows 10/11; no SDK needed.
    "/r:$fw\System.Runtime.dll", "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "/r:$env:WINDIR\System32\WinMetadata\Windows.Foundation.winmd",
    "/r:$env:WINDIR\System32\WinMetadata\Windows.Media.winmd",
    "/r:$env:WINDIR\System32\WinMetadata\Windows.Storage.winmd"
)
function Compile([string[]]$extra) {
    & "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 @refs @extra
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

# 1. Build once without an icon, and use it to draw the app icon.
Compile @("/out:$out\MacLook-noicon.exe", "$root\src\*.cs")
& "$out\MacLook-noicon.exe" --make-icon "$out\MacLook.ico" | Out-Null
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Path "$out\MacLook.ico") -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
Remove-Item "$out\MacLook-noicon.exe" -ErrorAction SilentlyContinue

# 2. The real app, with its icon.
Compile @("/out:$root\MacLook.exe", "/win32icon:$out\MacLook.ico", "/win32manifest:$root\src\app.manifest", "$root\src\*.cs")

# 3. The installer. It installs the MacLook.exe that sits next to it (it does not carry a copy inside).
Compile @("/out:$root\MacLookSetup.exe", "/win32icon:$out\MacLook.ico", "/win32manifest:$root\setup\app.manifest",
          "$root\setup\Setup.cs", "$root\src\AppIcon.cs", "$root\src\InstallInfo.cs")

Write-Host "Built $root\MacLook.exe"
Write-Host "Built $root\MacLookSetup.exe"

# 4. The download: both exes in one zip.
$zip = "$root\MacLook.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$root\MacLookSetup.exe", "$root\MacLook.exe" -DestinationPath $zip
Write-Host "Built $zip"
