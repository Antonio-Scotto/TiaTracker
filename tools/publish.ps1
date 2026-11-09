# Pubblica TiaTracker in dist\TiaTracker e ne fa i pacchetti per un altro PC:
#   dist\TiaTracker_<versione>_setup.msi      installer (Programmi, menu Start)
#   dist\TiaTracker_<versione>_portatile.zip  versione portatile (dati accanto all'exe)
#
#   app      net10 self-contained win-x64, ReadyToRun, niente single-file ne' trimming
#            (WPF e la riflessione di System.Text.Json non lo sopportano)
#   worker\  tiatracker-worker-v21.exe (e v18 se c'e' la DLL di TIA V18 su questo PC)
#
# Le DLL Siemens non si ridistribuiscono: i worker le trovano sul PC di destinazione.
#
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-NoZip] [-NoInstaller]

param(
    [switch]$NoZip,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist\TiaTracker'

# Si svuota invece di toglierla: un terminale o Esplora risorse aperti li' dentro la tengono occupata.
if (Test-Path $dist) {
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force
}

Write-Host '== App (self-contained, ReadyToRun)'
dotnet publish (Join-Path $root 'src\TiaTracker.App\TiaTracker.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw 'publish dell''app fallito' }

Write-Host '== Worker V21'
dotnet build (Join-Path $root 'src\TiaTracker.Worker.V21\TiaTracker.Worker.V21.csproj') -c Release -o (Join-Path $dist 'worker\v21') -nologo
if ($LASTEXITCODE -ne 0) { throw 'build del worker V21 fallito' }

$v18Dll = 'C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll'
if (Test-Path $v18Dll) {
    Write-Host '== Worker V18'
    dotnet build (Join-Path $root 'src\TiaTracker.Worker.V18\TiaTracker.Worker.V18.csproj') -c Release -o (Join-Path $dist 'worker\v18') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'build del worker V18 fallito' }
}
else {
    Write-Warning "Worker V18 non incluso: manca $v18Dll"
}

# Senza worker l'app parte ma ogni snapshot da TIA fallisce: meglio fermarsi qui.
$w21 = Join-Path $dist 'worker\v21\tiatracker-worker-v21.exe'
if (-not (Test-Path $w21)) { throw "Worker V21 mancante nel pacchetto: $w21" }
Write-Host "== Worker V21: $w21"
$w18 = Join-Path $dist 'worker\v18\tiatracker-worker-v18.exe'
if (Test-Path $w18) { Write-Host "== Worker V18: $w18" }

# hello carica le DLL Openness senza avviare TIA: verifica che il worker funzioni su questo PC.
# Openness avvia Siemens.Automation.ObjectFrame.FileStorage.Server, che sopravvive al worker e
# tiene occupata la cartella (al publish dopo dist\TiaTracker non si svuota): si chiude qui.
$helloOut = Join-Path $env:TEMP 'tiatracker_publish_hello.txt'
$p = Start-Process $w21 -ArgumentList 'hello' -WorkingDirectory $env:TEMP -NoNewWindow -Wait -PassThru -RedirectStandardOutput $helloOut
$hello = Get-Content $helloOut -ErrorAction SilentlyContinue | Select-Object -Last 1
Remove-Item $helloOut -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name = 'Siemens.Automation.ObjectFrame.FileStorage.Server.exe'" |
    Where-Object { $_.CommandLine -like "*/cpid=$($p.Id)*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if ($p.ExitCode -ne 0) { Write-Warning "hello del worker V21 uscito con $($p.ExitCode) (su questo PC manca TIA V21?): $hello" }
else { Write-Host "== hello V21 OK" }

# Niente simboli di debug nel pacchetto, tranne quelli dei worker (servono a leggere gli errori Openness).
Get-ChildItem $dist -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $PSScriptRoot 'LEGGIMI.txt') $dist

$version = (Get-Item (Join-Path $dist 'TiaTracker.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
Write-Host "== TiaTracker $version in $dist"

if (-not $NoZip) {
    # Versione portatile: con portable.flag accanto all'exe i dati stanno in data\ nella stessa
    # cartella (si porta su una chiavetta o su un altro PC cosi' com'e').
    $stage = Join-Path $root 'dist\portatile'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item $dist (Join-Path $stage 'TiaTracker') -Recurse
    Set-Content (Join-Path $stage 'TiaTracker\portable.flag') 'Versione portatile: i dati stanno nella cartella data accanto a TiaTracker.exe.' -Encoding utf8
    $zip = Join-Path $root ("dist\TiaTracker_{0}_portatile.zip" -f $version)
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage 'TiaTracker') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $stage -Recurse -Force
    Write-Host "== Portatile: $zip"
}

if (-not $NoInstaller) {
    # Installer MSI (WiX 5, scaricato da NuGet al primo build): Programmi\TiaTracker + menu Start.
    $msiOut = Join-Path $root 'dist\installer'
    & (Join-Path $root 'installer\make-files.ps1') -Dist $dist
    dotnet build (Join-Path $root 'installer\TiaTracker.Installer.wixproj') -c Release -nologo `
        -p:ProductVersion=$version -o $msiOut
    if ($LASTEXITCODE -ne 0) { throw 'build dell''installer fallito' }
    $msi = Join-Path $root ("dist\TiaTracker_{0}_setup.msi" -f $version)
    Move-Item (Join-Path $msiOut 'TiaTracker_setup.msi') $msi -Force
    Remove-Item $msiOut -Recurse -Force
    Write-Host "== Installer: $msi"
}
