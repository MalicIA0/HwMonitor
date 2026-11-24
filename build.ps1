# Compile HW Monitor en Release et (re)cree un raccourci sur le Bureau.
# Usage : clic droit -> "Executer avec PowerShell", ou : powershell -File build.ps1
$ErrorActionPreference = "Stop"
$proj = $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "Compilation..." -ForegroundColor Cyan
& $dotnet build "$proj\HwMonitor.csproj" -c Release

$exe = Join-Path $proj "bin\Release\net9.0-windows\HwMonitor.exe"
if (-not (Test-Path $exe)) { throw "Build KO : $exe introuvable" }

$desktop = [Environment]::GetFolderPath("Desktop")
$lnk = Join-Path $desktop "HW Monitor.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = Split-Path $exe
$sc.IconLocation = $exe
$sc.Save()

Write-Host "OK" -ForegroundColor Green
Write-Host "  Executable : $exe"
Write-Host "  Raccourci  : $lnk"
