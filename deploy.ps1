# Met a jour HW Monitor PARTOUT, en une seule fois.
#
# Le piege que ce script supprime : l'exe existe en TROIS exemplaires. All Access
# embarque le sien dans resources\perf et son bouton "Mettre a jour" ne fait que
# recopier CELUI-LA. Si on ne met a jour que l'overlay installe, All Access croit
# qu'une mise a jour est dispo et, au clic, REMET l'ancienne version.
# Ce script pousse donc la meme compilation aux trois endroits d'un coup.
#
# Usage : clic droit -> "Executer avec PowerShell"
#         ou : powershell -File deploy.ps1 -NoLaunch

param([switch]$NoLaunch)

$ErrorActionPreference = "Stop"

# L'overlay tourne en admin (requireAdministrator dans app.manifest) : sans elevation
# on ne peut ni le fermer ni remplacer son .exe verrouille. On se relance donc en admin.
$moi = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $moi).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $psArgs = @('-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($NoLaunch) { $psArgs += '-NoLaunch' }
    Start-Process powershell -Verb RunAs -ArgumentList $psArgs
    exit
}

$proj    = $PSScriptRoot
$publish = Join-Path $proj "bin\publish"
$exe     = Join-Path $publish "HwMonitor.exe"
$dotnet  = Join-Path $env:USERPROFILE "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

# 1er = l'overlay installe (celui que lance le raccourci du Bureau).
# 2e  = le projet All Access (embarque au prochain build de l'appli).
# 3e  = All Access deja installe (source du bouton "Mettre a jour").
$cibles = @(
    "$env:LOCALAPPDATA\Programs\HW Monitor",
    "$env:USERPROFILE\GameVault\resources\perf",
    "$env:LOCALAPPDATA\Programs\gamevault\resources\perf"
)

Write-Host ""
Write-Host "1/4  Compilation..." -ForegroundColor Cyan
# self-contained + fichier unique + compression : un seul .exe de ~65 Mo, sans
# dependance a .NET sur la machine. C'est la recette qui produit l'exe distribue.
& $dotnet publish "$proj\HwMonitor.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $publish -v m
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "Build KO : $exe introuvable" }

$version = (Get-Item $exe).VersionInfo.FileVersion
$hash    = (Get-FileHash $exe -Algorithm MD5).Hash
Write-Host "     version $version  ($hash)" -ForegroundColor DarkGray

Write-Host "2/4  Fermeture de l'overlay..." -ForegroundColor Cyan
# Un exe en cours d'execution est verrouille : sans ca, la copie echoue.
# PresentMon est le sous-processus qui mesure les FPS, il se ferme avec.
Get-Process HwMonitor, PresentMon -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Write-Host "3/4  Deploiement..." -ForegroundColor Cyan
$echecs = 0
foreach ($dir in $cibles) {
    if (-not (Test-Path $dir)) { Write-Host "     -- absent, ignore : $dir" -ForegroundColor DarkGray; continue }
    $dst = Join-Path $dir "HwMonitor.exe"
    # Version precedente conservee : retour arriere = renommer .prev en .exe.
    if (Test-Path $dst) { Copy-Item $dst "$dst.prev" -Force }
    Copy-Item $exe $dst -Force
    # PresentMon DOIT rester a cote de HwMonitor.exe, sinon plus de FPS.
    $pm = Join-Path $dir "PresentMon.exe"
    if (-not (Test-Path $pm)) { Copy-Item (Join-Path $proj "PresentMon.exe") $pm -Force }

    if ((Get-FileHash $dst -Algorithm MD5).Hash -eq $hash) {
        Write-Host "     OK  $dst" -ForegroundColor Green
    } else {
        Write-Host "     KO  $dst" -ForegroundColor Red; $echecs++
    }
}
if ($echecs -gt 0) { throw "$echecs copie(s) en echec." }

if ($NoLaunch) {
    Write-Host "4/4  Relance ignoree (-NoLaunch)." -ForegroundColor DarkGray
} else {
    Write-Host "4/4  Relance de l'overlay..." -ForegroundColor Cyan
    Start-Process (Join-Path $cibles[0] "HwMonitor.exe")
}

Write-Host ""
Write-Host "Termine - HW Monitor $version est en place partout." -ForegroundColor Green
Write-Host "All Access n'affichera pas de mise a jour : les trois copies sont identiques." -ForegroundColor DarkGray
Write-Host ""
